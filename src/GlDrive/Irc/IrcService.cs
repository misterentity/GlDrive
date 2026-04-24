using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Irc;

public enum IrcServiceState { Disconnected, Connecting, Connected, Reconnecting }

public enum IrcMessageType { Normal, Action, Notice, Join, Part, Quit, Kick, Topic, Mode, System }

public class IrcMessageItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Nick { get; set; } = "";
    public string Text { get; set; } = "";
    public IrcMessageType Type { get; set; }
    public bool WasEncrypted { get; set; }
}

public class IrcChannel
{
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public List<string> Nicks { get; set; } = [];
    public bool HasFishKey { get; set; }
}

public class IrcService : IDisposable
{
    private readonly ServerConfig _serverConfig;
    private readonly CertificateManager _certManager;
    private readonly FishKeyStore _fishKeyStore;
    private IrcClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _serviceTask;
    private readonly Dictionary<string, IrcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    // Pending DH1080 exchanges. Bounded to prevent memory exhaustion via NOTICE floods.
    // Entries carry a timestamp so we can LRU-evict and TTL-expire.
    private readonly Dictionary<string, (Dh1080 dh, DateTime createdAt)> _pendingKeyExchanges =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastDhInitByNick =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxPendingKeyExchanges = 32;
    private static readonly TimeSpan PendingKeyExchangeTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DhInitRateLimit = TimeSpan.FromSeconds(10);
    private string _currentNick = "";
    private bool _disposed;
    private DateTime _connectedAt;

    // Pending channel joins waiting for INVITE (channel → retry count)
    private readonly Dictionary<string, int> _pendingInviteJoins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _invitedChannels = new(StringComparer.OrdinalIgnoreCase);

    // Liveness: periodic PING to detect dead connections
    private Timer? _pingTimer;
    private DateTime _lastPongOrMessage;

    public IrcServiceState State { get; private set; } = IrcServiceState.Disconnected;
    public string ServerId => _serverConfig.Id;
    public string ServerName => _serverConfig.Name;
    public IReadOnlyDictionary<string, IrcChannel> Channels => _channels;
    public string CurrentNick => _currentNick;
    public FishKeyStore KeyStore => _fishKeyStore;
    public Func<string, CancellationToken, Task<string?>>? SiteInviteFunc { get; set; }

    public event Action<IrcServiceState>? StateChanged;
    public event Action<string, IrcMessageItem>? MessageReceived; // target, message
    public event Action<string>? NamesUpdated; // channel
    public event Action<string, string>? TopicChanged; // channel, topic

    public IrcService(ServerConfig serverConfig, CertificateManager certManager)
    {
        _serverConfig = serverConfig;
        _certManager = certManager;
        _fishKeyStore = new FishKeyStore(serverConfig.Id);
    }

    public async Task StartAsync()
    {
        if (_disposed || State != IrcServiceState.Disconnected) return;

        _cts = new CancellationTokenSource();
        _serviceTask = RunAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        StopPingTimer();
        _cts?.Cancel();
        if (_client?.IsConnected == true)
        {
            try { await _client.DisconnectAsync(); } catch { }
        }
        _client?.Dispose();
        _client = null;
        SetState(IrcServiceState.Disconnected);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = _serverConfig.Pool.ReconnectInitialDelaySeconds;
        var maxDelay = _serverConfig.Pool.ReconnectMaxDelaySeconds;
        if (maxDelay <= 0) maxDelay = 120;
        if (delay <= 0) delay = 5;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _connectedAt = DateTime.UtcNow;
                await ConnectAsync(ct);

                // Wait until disconnected
                var tcs = new TaskCompletionSource();
                using var reg = ct.Register(() => tcs.TrySetCanceled());
                Action<string>? disconnectHandler = null;
                if (_client != null)
                {
                    disconnectHandler = _ => tcs.TrySetResult();
                    _client.Disconnected += disconnectHandler;
                    // If ReadLoop already fired Disconnected before we attached, unblock now
                    if (!_client.IsConnected)
                        tcs.TrySetResult();
                }

                try { await tcs.Task; }
                finally
                {
                    // Remove handler to prevent accumulation across reconnects
                    if (_client != null && disconnectHandler != null)
                        _client.Disconnected -= disconnectHandler;
                }

                StopPingTimer();

                // Only reset delay if the connection was stable for at least 60 seconds
                // This prevents rapid reconnect loops that trigger BNC rate limiting
                var uptime = DateTime.UtcNow - _connectedAt;
                if (uptime.TotalSeconds >= 60)
                {
                    delay = _serverConfig.Pool.ReconnectInitialDelaySeconds;
                    if (delay <= 0) delay = 5;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "IRC connection failed for {Server}", _serverConfig.Name);
                AddSystemMessage("*", $"Connection failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested) break;

            SetState(IrcServiceState.Reconnecting);
            AddSystemMessage("*", $"Reconnecting in {delay}s...");

            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
            catch (OperationCanceledException) { break; }

            delay = Math.Min(delay * 2, maxDelay);
        }

        SetState(IrcServiceState.Disconnected);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var irc = _serverConfig.Irc;
        SetState(IrcServiceState.Connecting);
        AddSystemMessage("*", $"Connecting to {irc.Host}:{irc.Port}...");

        _channels.Clear();
        _pendingInviteJoins.Clear();
        _invitedChannels.Clear();

        _client?.Dispose();
        _client = new IrcClient();
        _client.MessageReceived += HandleMessage;
        _client.Connected += () => Log.Information("IRC connected to {Host}:{Port}", irc.Host, irc.Port);
        _client.Disconnected += reason =>
        {
            Log.Information("IRC disconnected from {Host}: {Reason}", irc.Host, reason);
            AddSystemMessage("*", $"Disconnected: {reason}");
        };

        await _client.ConnectAsync(irc.Host, irc.Port, irc.UseTls, _certManager, ct);

        // Authenticate
        var password = CredentialStore.GetIrcPassword(irc.Host, irc.Port, irc.Nick);
        if (!string.IsNullOrEmpty(password))
            await _client.PassAsync(password);

        _currentNick = irc.Nick;
        await _client.NickAsync(irc.Nick);
        await _client.UserAsync(irc.Nick, irc.RealName);

        // Start liveness ping timer (every 90s, detect dead connections)
        _lastPongOrMessage = DateTime.UtcNow;
        StartPingTimer();
    }

    private void StartPingTimer()
    {
        StopPingTimer();
        _pingTimer = new Timer(PingTimerCallback, null, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90));
    }

    private void StopPingTimer()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    private async void PingTimerCallback(object? state)
    {
        try
        {
            if (_client == null || !_client.IsConnected) return;

            // If we haven't received anything in 180s, the connection is dead
            var silence = DateTime.UtcNow - _lastPongOrMessage;
            if (silence.TotalSeconds > 180)
            {
                Log.Warning("IRC connection appears dead (no data for {Seconds}s), forcing disconnect", (int)silence.TotalSeconds);
                try { _client?.Dispose(); } catch { }
                return;
            }

            // Send a PING to keep the connection alive and detect issues
            await _client.SendRawAsync($"PING :{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IRC ping timer error");
        }
    }

    private void HandleMessage(IrcMessage msg)
    {
        // Track liveness — any message from server means connection is alive
        _lastPongOrMessage = DateTime.UtcNow;

        switch (msg.Command)
        {
            case "001": // RPL_WELCOME
                SetState(IrcServiceState.Connected);
                _currentNick = msg.Params.FirstOrDefault() ?? _currentNick;
                AddSystemMessage("*", $"Connected as {_currentNick}");
                _ = AutoJoinChannelsAsync();
                break;

            case "433": // ERR_NICKNAMEINUSE
                _ = HandleNickInUseAsync();
                break;

            case "332": // RPL_TOPIC
                HandleTopicReply(msg);
                break;

            case "353": // RPL_NAMREPLY
                HandleNamesReply(msg);
                break;

            case "366": // RPL_ENDOFNAMES
                if (msg.Params.Count >= 2)
                    NamesUpdated?.Invoke(msg.Params[1]);
                break;

            case "INVITE":
                HandleInvite(msg);
                break;

            case "PRIVMSG":
                HandlePrivmsg(msg);
                break;

            case "NOTICE":
                HandleNotice(msg);
                break;

            case "JOIN":
                HandleJoin(msg);
                break;

            case "PART":
                HandlePart(msg);
                break;

            case "QUIT":
                HandleQuit(msg);
                break;

            case "KICK":
                HandleKick(msg);
                break;

            case "TOPIC":
                HandleTopicChange(msg);
                break;

            case "MODE":
                HandleMode(msg);
                break;

            case "NICK":
                HandleNickChange(msg);
                break;

            // Channel join errors — retry invite-only channels
            case "473": // ERR_INVITEONLYCHAN
                HandleJoinError(msg, "invite-only");
                break;
            case "474": // ERR_BANNEDFROMCHAN
                HandleJoinError(msg, "banned");
                break;
            case "475": // ERR_BADCHANNELKEY
                HandleJoinError(msg, "bad key");
                break;

            case "PONG":
                // Handled by liveness tracking above
                break;

            default:
                // Show unhandled server messages (MOTD, errors, info, etc.)
                if (!string.IsNullOrEmpty(msg.Trailing))
                    AddSystemMessage("*", StripFormatting(msg.Trailing));
                break;
        }
    }

    private void HandleInvite(IrcMessage msg)
    {
        // :nick!user@host INVITE yournick :#channel
        var channel = msg.Trailing ?? (msg.Params.Count >= 2 ? msg.Params[1] : "");
        if (string.IsNullOrEmpty(channel)) return;

        AddSystemMessage("*", $"Invited to {channel} by {msg.Nick}");
        _invitedChannels.Add(channel);

        // Auto-join if this channel is in our config
        var configured = _serverConfig.Irc.Channels
            .FirstOrDefault(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (configured != null)
        {
            Log.Information("Auto-joining invited channel {Channel}", channel);
            _ = JoinConfiguredChannelAsync(configured);
        }

        // Also join if we were waiting for this invite after a 473
        if (_pendingInviteJoins.Remove(channel))
        {
            Log.Information("Joining channel {Channel} after receiving pending invite", channel);
            _ = JoinConfiguredChannelAsync(configured ?? new IrcChannelConfig { Name = channel, AutoJoin = true });
        }
    }

    private void HandleJoinError(IrcMessage msg, string reason)
    {
        // :server 473 nick #channel :Cannot join channel (+i) - you must be invited
        var channel = msg.Params.Count >= 2 ? msg.Params[1] : "";
        if (string.IsNullOrEmpty(channel)) return;

        AddSystemMessage("*", $"Cannot join {channel}: {reason}");

        if (reason == "invite-only")
        {
            // Track retry count and schedule retry (SITE INVITE may still be processing)
            var retries = _pendingInviteJoins.GetValueOrDefault(channel, 0);
            if (retries < 3)
            {
                _pendingInviteJoins[channel] = retries + 1;
                var delaySec = (retries + 1) * 5; // 5s, 10s, 15s
                AddSystemMessage("*", $"Will retry joining {channel} in {delaySec}s (attempt {retries + 2}/4)...");
                _ = RetryJoinAfterDelay(channel, delaySec);
            }
            else
            {
                _pendingInviteJoins.Remove(channel);
                AddSystemMessage("*", $"Gave up joining {channel} after {retries + 1} attempts — try /join {channel} manually");
            }
        }
    }

    private async Task RetryJoinAfterDelay(string channel, int delaySec)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySec));
            if (_client == null || !_client.IsConnected) return;
            if (_channels.ContainsKey(channel)) return; // Already joined

            var configured = _serverConfig.Irc.Channels
                .FirstOrDefault(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));
            await JoinConfiguredChannelAsync(configured ?? new IrcChannelConfig { Name = channel, AutoJoin = true });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Retry join failed for {Channel}", channel);
        }
    }

    private void HandlePrivmsg(IrcMessage msg)
    {
        var target = msg.Params.FirstOrDefault() ?? "";
        var text = msg.Trailing ?? "";
        var nick = msg.Nick;
        var wasEncrypted = false;

        // CTCP handling
        if (text.StartsWith('\x01'))
        {
            if (text.StartsWith("\x01ACTION ") && text.EndsWith('\x01'))
            {
                var action = StripFormatting(text[8..^1]);
                var displayTarget = IsChannel(target) ? target : nick;
                AddMessage(displayTarget, new IrcMessageItem { Nick = nick, Text = action, Type = IrcMessageType.Action });
            }
            // Drop all other CTCP messages (VERSION, FINGER, DCC, etc.)
            return;
        }

        // FiSH decryption
        var effectiveTarget = IsChannel(target) ? target : nick;
        if (_serverConfig.Irc.FishEnabled && FishCipher.IsEncrypted(text))
        {
            var keyEntry = _fishKeyStore.GetKey(effectiveTarget);
            var prefix = text.StartsWith("+OK *") ? "CBC" : "ECB";
            if (keyEntry != null)
            {
                var decrypted = FishCipher.Decrypt(text, keyEntry.Key);
                if (decrypted != null)
                {
                    Log.Information("FiSH PM {Target}: prefix={Prefix} cipherLen={CL} keyMask={KM} decrypted={Stats}",
                        effectiveTarget, prefix, text.Length, MaskKey(keyEntry.Key), TextStats(decrypted));
                    text = decrypted;
                    wasEncrypted = true;

                    // Auto-detect peer's encryption mode and update stored key to match
                    // This ensures our outgoing messages use the same mode they use
                    var peerMode = msg.Trailing!.StartsWith("+OK *") ? FishMode.CBC : FishMode.ECB;
                    if (keyEntry.Mode != peerMode)
                    {
                        _fishKeyStore.SetKey(effectiveTarget, keyEntry.Key, peerMode);
                        Log.Information("FiSH mode updated for {Target}: {OldMode} → {NewMode}",
                            effectiveTarget, keyEntry.Mode, peerMode);
                    }
                }
                else
                {
                    Log.Warning("FiSH PM {Target}: decrypt returned null. prefix={Prefix} cipherLen={CL} keyMask={KM}",
                        effectiveTarget, prefix, text.Length, MaskKey(keyEntry.Key));
                }
            }
            else
            {
                Log.Information("FiSH PM {Target}: no key stored. prefix={Prefix} cipherLen={CL}",
                    effectiveTarget, prefix, text.Length);
            }
        }

        AddMessage(effectiveTarget, new IrcMessageItem { Nick = nick, Text = StripFormatting(text), WasEncrypted = wasEncrypted });
    }

    private void HandleNotice(IrcMessage msg)
    {
        var text = msg.Trailing ?? "";
        var nick = msg.Nick;

        // DH1080 key exchange
        if (Dh1080.TryParseInit(text, out var initPubKey))
        {
            _ = HandleDh1080InitAsync(nick, initPubKey);
            return;
        }
        if (Dh1080.TryParseFinish(text, out var finishPubKey))
        {
            HandleDh1080Finish(nick, finishPubKey);
            return;
        }

        var target = msg.Params.FirstOrDefault() ?? "";
        var effectiveTarget = IsChannel(target) ? target : nick;
        var wasEncrypted = false;

        if (_serverConfig.Irc.FishEnabled && FishCipher.IsEncrypted(text))
        {
            var keyEntry = _fishKeyStore.GetKey(effectiveTarget);
            if (keyEntry != null)
            {
                var decrypted = FishCipher.Decrypt(text, keyEntry.Key);
                if (decrypted != null)
                {
                    text = decrypted;
                    wasEncrypted = true;

                    // Auto-detect peer's mode and update
                    var peerMode = msg.Trailing!.StartsWith("+OK *") ? FishMode.CBC : FishMode.ECB;
                    if (keyEntry.Mode != peerMode)
                        _fishKeyStore.SetKey(effectiveTarget, keyEntry.Key, peerMode);
                }
            }
        }

        text = StripFormatting(text);
        if (string.IsNullOrEmpty(nick))
            AddSystemMessage("*", text);
        else
            AddMessage(effectiveTarget, new IrcMessageItem { Nick = nick, Text = text, Type = IrcMessageType.Notice, WasEncrypted = wasEncrypted });
    }

    private void HandleJoin(IrcMessage msg)
    {
        var channel = msg.Trailing ?? msg.Params.FirstOrDefault() ?? "";
        var nick = msg.Nick;

        if (nick.Equals(_currentNick, StringComparison.OrdinalIgnoreCase))
        {
            EnsureChannel(channel);
            _pendingInviteJoins.Remove(channel); // Successfully joined, stop retrying
            AddMessage(channel, new IrcMessageItem { Nick = nick, Text = $"{nick} has joined {channel}", Type = IrcMessageType.Join });
        }
        else
        {
            if (_channels.TryGetValue(channel, out var ch) &&
                !ch.Nicks.Any(n => StripNickPrefix(n).Equals(nick, StringComparison.OrdinalIgnoreCase)))
            {
                ch.Nicks.Add(nick);
                NamesUpdated?.Invoke(channel);
            }
            AddMessage(channel, new IrcMessageItem { Nick = nick, Text = $"{nick} has joined {channel}", Type = IrcMessageType.Join });
        }
    }

    private void HandlePart(IrcMessage msg)
    {
        var channel = msg.Params.FirstOrDefault() ?? "";
        var nick = msg.Nick;
        var reason = msg.Trailing ?? "";

        if (nick.Equals(_currentNick, StringComparison.OrdinalIgnoreCase))
        {
            _channels.Remove(channel);
        }
        else if (_channels.TryGetValue(channel, out var ch))
        {
            ch.Nicks.RemoveAll(n => StripNickPrefix(n).Equals(nick, StringComparison.OrdinalIgnoreCase));
            NamesUpdated?.Invoke(channel);
        }

        AddMessage(channel, new IrcMessageItem
        {
            Nick = nick,
            Text = string.IsNullOrEmpty(reason) ? $"{nick} has left {channel}" : $"{nick} has left {channel} ({reason})",
            Type = IrcMessageType.Part
        });
    }

    private void HandleQuit(IrcMessage msg)
    {
        var nick = msg.Nick;
        var reason = msg.Trailing ?? "";

        foreach (var (channel, ch) in _channels)
        {
            if (ch.Nicks.RemoveAll(n => StripNickPrefix(n).Equals(nick, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                NamesUpdated?.Invoke(channel);
                AddMessage(channel, new IrcMessageItem
                {
                    Nick = nick,
                    Text = string.IsNullOrEmpty(reason) ? $"{nick} has quit" : $"{nick} has quit ({reason})",
                    Type = IrcMessageType.Quit
                });
            }
        }
    }

    private void HandleKick(IrcMessage msg)
    {
        var channel = msg.Params.FirstOrDefault() ?? "";
        var kicked = msg.Params.Count >= 2 ? msg.Params[1] : "";
        var reason = msg.Trailing ?? "";

        if (kicked.Equals(_currentNick, StringComparison.OrdinalIgnoreCase))
        {
            _channels.Remove(channel);
        }
        else if (_channels.TryGetValue(channel, out var ch))
        {
            ch.Nicks.RemoveAll(n => StripNickPrefix(n).Equals(kicked, StringComparison.OrdinalIgnoreCase));
            NamesUpdated?.Invoke(channel);
        }

        AddMessage(channel, new IrcMessageItem
        {
            Nick = msg.Nick,
            Text = $"{kicked} was kicked by {msg.Nick} ({reason})",
            Type = IrcMessageType.Kick
        });
    }

    private void HandleTopicReply(IrcMessage msg)
    {
        // :server 332 nick #channel :topic text
        if (msg.Params.Count < 2) return;
        var channel = msg.Params[1];
        var topic = msg.Trailing ?? "";

        if (_channels.TryGetValue(channel, out var ch))
            ch.Topic = topic;

        TopicChanged?.Invoke(channel, topic);
    }

    private void HandleTopicChange(IrcMessage msg)
    {
        var channel = msg.Params.FirstOrDefault() ?? "";
        var topic = msg.Trailing ?? "";

        if (_channels.TryGetValue(channel, out var ch))
            ch.Topic = topic;

        TopicChanged?.Invoke(channel, topic);
        AddMessage(channel, new IrcMessageItem
        {
            Nick = msg.Nick,
            Text = $"{msg.Nick} changed the topic to: {topic}",
            Type = IrcMessageType.Topic
        });
    }

    private void HandleNamesReply(IrcMessage msg)
    {
        // :server 353 nick = #channel :@nick1 +nick2 nick3
        if (msg.Params.Count < 3) return;
        var channel = msg.Params[2];
        var names = (msg.Trailing ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var ch = EnsureChannel(channel);
        foreach (var name in names)
        {
            var clean = StripNickPrefix(name);
            // Remove existing entry (may have different prefix) then re-add with current prefix
            ch.Nicks.RemoveAll(n => StripNickPrefix(n).Equals(clean, StringComparison.OrdinalIgnoreCase));
            ch.Nicks.Add(name);
        }
    }

    private static string StripNickPrefix(string nick) => nick.TrimStart('@', '+', '%', '~', '&');

    private void HandleMode(IrcMessage msg)
    {
        var target = msg.Params.FirstOrDefault() ?? "";
        var modeStr = string.Join(" ", msg.Params.Skip(1));
        if (!string.IsNullOrEmpty(msg.Trailing))
            modeStr += " " + msg.Trailing;

        if (IsChannel(target))
        {
            // Update nick prefixes for +o/-o/+v/-v
            if (_channels.TryGetValue(target, out var ch) && msg.Params.Count >= 3)
            {
                var modes = msg.Params[1];
                var nickArgs = msg.Params.Skip(2).ToList();
                var adding = true;
                var nickIdx = 0;
                foreach (var c in modes)
                {
                    switch (c)
                    {
                        case '+': adding = true; break;
                        case '-': adding = false; break;
                        case 'o' or 'v' or 'h' when nickIdx < nickArgs.Count:
                            var modeNick = nickArgs[nickIdx++];
                            var prefix = c == 'o' ? '@' : c == 'h' ? '%' : '+';
                            var idx = ch.Nicks.FindIndex(n =>
                                StripNickPrefix(n).Equals(modeNick, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                            {
                                var clean = StripNickPrefix(ch.Nicks[idx]);
                                ch.Nicks[idx] = adding ? $"{prefix}{clean}" : clean;
                            }
                            break;
                        default:
                            // Other modes with params (b, k, l, etc.)
                            if ("bklIeq".Contains(c) && nickIdx < nickArgs.Count) nickIdx++;
                            break;
                    }
                }
                NamesUpdated?.Invoke(target);
            }

            AddMessage(target, new IrcMessageItem
            {
                Nick = msg.Nick,
                Text = $"{msg.Nick} sets mode {modeStr}",
                Type = IrcMessageType.Mode
            });
        }
    }

    private void HandleNickChange(IrcMessage msg)
    {
        var oldNick = msg.Nick;
        var newNick = msg.Trailing ?? msg.Params.FirstOrDefault() ?? "";

        if (oldNick.Equals(_currentNick, StringComparison.OrdinalIgnoreCase))
            _currentNick = newNick;

        foreach (var (channel, ch) in _channels)
        {
            var idx = ch.Nicks.FindIndex(n => StripNickPrefix(n).Equals(oldNick, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                // Preserve the mode prefix when renaming
                var prefix = ch.Nicks[idx][..^StripNickPrefix(ch.Nicks[idx]).Length];
                ch.Nicks[idx] = prefix + newNick;
                NamesUpdated?.Invoke(channel);
                AddMessage(channel, new IrcMessageItem
                {
                    Nick = oldNick,
                    Text = $"{oldNick} is now known as {newNick}",
                    Type = IrcMessageType.System
                });
            }
        }
    }

    private async Task HandleNickInUseAsync()
    {
        try
        {
            if (_client == null) return;
            var altNick = _serverConfig.Irc.AltNick;
            if (!string.IsNullOrEmpty(altNick) && !altNick.Equals(_currentNick, StringComparison.OrdinalIgnoreCase))
            {
                _currentNick = altNick;
                await _client.NickAsync(altNick);
                AddSystemMessage("*", $"Nick in use, trying {altNick}");
            }
            else
            {
                _currentNick = _serverConfig.Irc.Nick + "_";
                await _client.NickAsync(_currentNick);
                AddSystemMessage("*", $"Nick in use, trying {_currentNick}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to handle nick-in-use");
        }
    }

    private async Task AutoJoinChannelsAsync()
    {
        try
        {
            if (_client == null) return;

            // Run SITE INVITE before joining channels
            var inviteNick = _serverConfig.Irc.InviteNick;
            if (!string.IsNullOrEmpty(inviteNick) && SiteInviteFunc != null)
            {
                // Bound the call: a hung FTP pool must not block the entire IRC auto-join loop.
                // 30 seconds covers realistic glftpd → IRC propagation while catching stalls.
                using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    AddSystemMessage("*", $"Running SITE INVITE {inviteNick}...");
                    var reply = await SiteInviteFunc(inviteNick, inviteCts.Token);
                    AddSystemMessage("*", reply ?? "SITE INVITE completed (no reply)");

                    // Wait a moment for the IRC server to process the invite
                    // glftpd SITE INVITE is async — the IRC INVITE arrives shortly after
                    await Task.Delay(2000);
                }
                catch (OperationCanceledException) when (inviteCts.IsCancellationRequested)
                {
                    AddSystemMessage("*", "SITE INVITE timed out after 30s — continuing with channel joins");
                    Log.Warning("SITE INVITE timed out for {Server}", _serverConfig.Name);
                }
                catch (Exception ex)
                {
                    AddSystemMessage("*", $"SITE INVITE failed: {ex.Message}");
                    // Still try to join channels — some may not need invite
                }
            }

            // Small delay between JOINs to avoid flood protection
            foreach (var ch in _serverConfig.Irc.Channels)
            {
                if (!ch.AutoJoin) continue;
                await JoinConfiguredChannelAsync(ch);
                await Task.Delay(500); // Anti-flood delay between joins
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed during auto-join channels for {Server}", _serverConfig.Name);
            AddSystemMessage("*", $"Auto-join error: {ex.Message}");
        }
    }

    private async Task JoinConfiguredChannelAsync(IrcChannelConfig ch)
    {
        if (_client == null || !_client.IsConnected) return;

        // Parse FiSH key from channel key field. Supported formats:
        //   [cbc:key]  — explicit CBC mode
        //   [ecb:key]  — explicit ECB mode
        //   [key]      — bare brackets, use server's default FishMode
        //   key        — plain IRC channel key (no FiSH)
        string? joinKey = null;
        if (!string.IsNullOrEmpty(ch.Key))
        {
            var fishMatch = Regex.Match(ch.Key, @"^\[(cbc|ecb):(.+)\]$", RegexOptions.IgnoreCase);
            if (fishMatch.Success)
            {
                // Explicit mode: [cbc:key] or [ecb:key]
                var mode = fishMatch.Groups[1].Value.Equals("cbc", StringComparison.OrdinalIgnoreCase)
                    ? FishMode.CBC : FishMode.ECB;
                _fishKeyStore.SetKey(ch.Name, fishMatch.Groups[2].Value, mode);
            }
            else if (ch.Key.StartsWith('[') && ch.Key.EndsWith(']') && ch.Key.Length > 2)
            {
                // Bare brackets: [key] — use server's default FishMode
                var bareKey = ch.Key[1..^1];
                _fishKeyStore.SetKey(ch.Name, bareKey, _serverConfig.Irc.FishMode);
                Log.Information("FiSH key set for {Channel} using default mode {Mode}", ch.Name, _serverConfig.Irc.FishMode);
            }
            else
            {
                joinKey = ch.Key; // Regular IRC channel key
            }
        }

        await _client.JoinAsync(ch.Name, joinKey);
    }

    // Public send methods

    public async Task SendMessage(string target, string text)
    {
        if (_client == null || !_client.IsConnected) return;

        // Encrypt if FiSH key exists
        if (_serverConfig.Irc.FishEnabled)
        {
            var keyEntry = _fishKeyStore.GetKey(target);
            if (keyEntry != null)
            {
                var encrypted = FishCipher.Encrypt(text, keyEntry.Key, keyEntry.Mode);
                await _client.PrivmsgAsync(target, encrypted);
                AddMessage(target, new IrcMessageItem { Nick = _currentNick, Text = text, WasEncrypted = true });
                return;
            }
        }

        await _client.PrivmsgAsync(target, text);
        AddMessage(target, new IrcMessageItem { Nick = _currentNick, Text = text });
    }

    public async Task SendAction(string target, string text)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.PrivmsgAsync(target, $"\x01ACTION {text}\x01");
        AddMessage(target, new IrcMessageItem { Nick = _currentNick, Text = text, Type = IrcMessageType.Action });
    }

    public async Task JoinChannel(string channel, string? key = null)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.JoinAsync(channel, key);
    }

    public async Task PartChannel(string channel, string? message = null)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.PartAsync(channel, message);
    }

    public async Task SetTopic(string channel, string topic)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.SendRawAsync($"TOPIC {channel} :{topic}");
    }

    public async Task SendNotice(string target, string text)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.NoticeAsync(target, text);
    }

    public async Task SendRaw(string line)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.SendRawAsync(line);
    }

    /// <summary>
    /// Evicts expired or surplus entries from _pendingKeyExchanges. Called before every insert.
    /// Prevents memory growth under NOTICE flood attacks.
    /// </summary>
    private void PruneKeyExchanges()
    {
        var now = DateTime.UtcNow;

        // TTL eviction
        var expired = _pendingKeyExchanges
            .Where(kv => now - kv.Value.createdAt > PendingKeyExchangeTtl)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var nick in expired)
            _pendingKeyExchanges.Remove(nick);

        // Capacity LRU eviction
        while (_pendingKeyExchanges.Count >= MaxPendingKeyExchanges)
        {
            var oldest = _pendingKeyExchanges.OrderBy(kv => kv.Value.createdAt).First().Key;
            _pendingKeyExchanges.Remove(oldest);
            Log.Debug("DH1080 pending map at capacity — evicting {Nick}", oldest);
        }

        // Also prune rate-limit table periodically
        if (_lastDhInitByNick.Count > MaxPendingKeyExchanges * 2)
        {
            var stale = _lastDhInitByNick
                .Where(kv => now - kv.Value > DhInitRateLimit)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var nick in stale)
                _lastDhInitByNick.Remove(nick);
        }
    }

    public async Task InitiateKeyExchange(string nick)
    {
        if (_client == null || !_client.IsConnected) return;

        PruneKeyExchanges();

        var dh = new Dh1080();
        _pendingKeyExchanges[nick] = (dh, DateTime.UtcNow);
        var pub = dh.GetPublicKeyBase64();
        Log.Information("DH1080 INIT send to {Nick}: ourPubLen={Len} ourPubMask={Mask}",
            nick, pub.Length, MaskMid(pub));
        await _client.NoticeAsync(nick, Dh1080.FormatInit(pub));
        AddSystemMessage(nick, "DH1080 key exchange initiated");
    }

    private static string MaskMid(string s) =>
        s.Length < 12 ? "***" : $"{s[..6]}...{s[^6..]}";

    private static string MaskKey(string k) =>
        k.Length < 8 ? "***" : $"{k[..4]}...{k[^4..]}";

    private static string TextStats(string s)
    {
        if (string.IsNullOrEmpty(s)) return "len=0";
        var printable = 0;
        foreach (var c in s) if (c >= 0x20 && c < 0x7F) printable++;
        return $"len={s.Length},printable={printable}/{s.Length}";
    }

    private async Task HandleDh1080InitAsync(string nick, string theirPubKey)
    {
        try
        {
            if (_client == null) return;

            // Per-nick rate limit — 1 INIT per 10s to blunt CPU amplification from BigInteger.ModPow floods
            var now = DateTime.UtcNow;
            if (_lastDhInitByNick.TryGetValue(nick, out var last) && now - last < DhInitRateLimit)
            {
                Log.Debug("DH1080 init from {Nick} rate-limited", nick);
                return;
            }
            _lastDhInitByNick[nick] = now;

            Log.Information("DH1080 INIT recv from {Nick}: peerPubLen={Len} peerPubMask={Mask}",
                nick, theirPubKey.Length, MaskMid(theirPubKey));

            // Offload ModPow to ThreadPool so the IRC read loop isn't blocked by ~10ms of crypto
            var dh = await Task.Run(() =>
            {
                var d = new Dh1080();
                _ = d.ComputeSharedSecret(theirPubKey); // validates peer key early, throws on bad key
                return d;
            });

            var sharedSecret = dh.ComputeSharedSecret(theirPubKey);
            // DH1080 FiSH standard uses CBC mode
            _fishKeyStore.SetKey(nick, sharedSecret, FishMode.CBC);

            var ourPub = dh.GetPublicKeyBase64();
            Log.Information("DH1080 FINISH send to {Nick}: ourPubLen={Len} ourPubMask={Mask} derivedKeyLen={KL} derivedKeyMask={KM}",
                nick, ourPub.Length, MaskMid(ourPub), sharedSecret.Length, MaskKey(sharedSecret));

            await _client.NoticeAsync(nick, Dh1080.FormatFinish(ourPub));
            AddSystemMessage(nick, $"DH1080 key exchange completed (initiated by {nick}) — CBC mode");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DH1080 init handling failed for {Nick}", nick);
        }
    }

    private void HandleDh1080Finish(string nick, string theirPubKey)
    {
        try
        {
            if (!_pendingKeyExchanges.TryGetValue(nick, out var entry)) return;

            Log.Information("DH1080 FINISH recv from {Nick}: peerPubLen={Len} peerPubMask={Mask}",
                nick, theirPubKey.Length, MaskMid(theirPubKey));

            var sharedSecret = entry.dh.ComputeSharedSecret(theirPubKey);
            // DH1080 FiSH standard uses CBC mode
            _fishKeyStore.SetKey(nick, sharedSecret, FishMode.CBC);
            _pendingKeyExchanges.Remove(nick);

            Log.Information("DH1080 derived for {Nick}: derivedKeyLen={KL} derivedKeyMask={KM}",
                nick, sharedSecret.Length, MaskKey(sharedSecret));

            AddSystemMessage(nick, "DH1080 key exchange completed — CBC mode");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DH1080 finish handling failed for {Nick}", nick);
            _pendingKeyExchanges.Remove(nick);
            AddSystemMessage(nick, $"DH1080 key exchange failed: {ex.Message}");
        }
    }

    public void SetFishKey(string target, string key, FishMode mode)
    {
        _fishKeyStore.SetKey(target, key, mode);
        if (_channels.TryGetValue(target, out var ch))
            ch.HasFishKey = true;
        AddSystemMessage(target, $"FiSH key set ({mode})");
    }

    // Helpers

    private IrcChannel EnsureChannel(string name)
    {
        if (!_channels.TryGetValue(name, out var ch))
        {
            ch = new IrcChannel
            {
                Name = name,
                HasFishKey = _fishKeyStore.GetKey(name) != null
            };
            _channels[name] = ch;
        }
        return ch;
    }

    private static readonly Regex MircFormatRegex = new(
        @"\x03(\d{1,2}(,\d{1,2})?)?|\x02|\x1D|\x1F|\x16|\x0F|\x1E",
        RegexOptions.Compiled);

    private static string StripFormatting(string text) => MircFormatRegex.Replace(text, "");

    private static bool IsChannel(string target) =>
        target.Length > 0 && (target[0] == '#' || target[0] == '&' || target[0] == '!' || target[0] == '+');

    private void AddMessage(string target, IrcMessageItem item)
    {
        MessageReceived?.Invoke(target, item);
    }

    private void AddSystemMessage(string target, string text)
    {
        AddMessage(target, new IrcMessageItem { Text = text, Type = IrcMessageType.System });
    }

    private void SetState(IrcServiceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPingTimer();
        _cts?.Cancel();
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }
}
