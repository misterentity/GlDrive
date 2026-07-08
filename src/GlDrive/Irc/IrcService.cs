using System.Security.Cryptography;
using System.Text;
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
    private readonly HashSet<string> _decryptFailHintSent = new(StringComparer.OrdinalIgnoreCase);
    // Auto-recovery for stale/corrupt DH1080 keys: a private-message key derived by an older
    // build (e.g. pre-v3.10.16, whose public-key decoder could drop bits and derive the wrong
    // key) makes a peer's messages permanently unreadable. After a few consecutive decrypt
    // failures we transparently re-run the key exchange. Bounded + rate-limited so a peer using
    // an unrelated static key can't trigger /keyx spam. Touched only under _decryptPromoteLock.
    private readonly Dictionary<string, int> _decryptFailStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAutoRekey = new(StringComparer.OrdinalIgnoreCase);
    // Auto-rekeys fired for this target WITHOUT an intervening successful decrypt. Caps the
    // total attempts per staleness episode so a peer a fresh exchange can never repair (e.g. one
    // using a separate static key, or spamming "+OK <garbage>") can't drive an endless stream of
    // DH1080_INIT NOTICEs. Reset to 0 on the next successful decrypt (episode boundary).
    private readonly Dictionary<string, int> _autoRekeyAttempts = new(StringComparer.OrdinalIgnoreCase);
    // Corroboration for alternate-KDF recovery: the candidate key that matched a peer's LAST
    // undecryptable message. We only promote a speculative KDF once the SAME candidate decrypts
    // a SECOND message — a single short-block false match (Blowfish garbage that happens to look
    // like text after NUL-trim) can't hijack the key. Cleared on any real decrypt or re-key.
    private readonly Dictionary<string, string> _altKdfPending = new(StringComparer.OrdinalIgnoreCase);
    private const int AutoRekeyFailThreshold = 3;
    private const int MaxAutoRekeyAttempts = 3;
    private static readonly TimeSpan AutoRekeyCooldown = TimeSpan.FromMinutes(5);
    // Serializes the GetKey→(sync)decrypt→SetKeyWithAlt variant-promotion compound so two
    // incoming messages can't both read the same pre-promotion snapshot and race the write,
    // promoting the wrong base64-alphabet variant. Decrypt is fully synchronous — this lock
    // is NEVER held across an await (the codebase forbids lock-across-await).
    private readonly object _decryptPromoteLock = new();

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

    // Resolved legacy fallback charset for FiSH decode (peers not using UTF-8). Null = UTF-8 only.
    private readonly Encoding? _fishFallbackCharset;

    public IrcService(ServerConfig serverConfig, CertificateManager certManager)
    {
        _serverConfig = serverConfig;
        _certManager = certManager;
        _fishKeyStore = new FishKeyStore(serverConfig.Id);
        _fishFallbackCharset = ResolveFallbackCharset(serverConfig.Irc.FallbackCharset);
    }

    private static Encoding? ResolveFallbackCharset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            // windows-125x etc. live in the CodePages provider (not registered by default on .NET).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(name.Trim());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "IRC FiSH fallback charset '{Charset}' is not available — using UTF-8 only", name);
            return null;
        }
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
                _client?.SignalDisconnect();
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

            case "PING":
            case "PONG":
                // Liveness already refreshed at the top of this method. PING is auto-answered
                // with PONG in IrcClient.ReadLoop; both land here only to count as inbound
                // traffic — no further action, and must not fall through to the default branch
                // (which would post the token as a system message).
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

        // Auto-join EVERY channel we're invited to, whether or not it's in config
        // (Dave's request). Prefer the configured entry when present so its FiSH key
        // and settings apply; otherwise join with defaults. Clear any pending-invite
        // retry bookkeeping (this invite satisfies a prior 473 wait, if any).
        var configured = _serverConfig.Irc.Channels
            .FirstOrDefault(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));
        _pendingInviteJoins.Remove(channel);
        Log.Information("Auto-joining invited channel {Channel} (invited by {Nick})", channel, msg.Nick);
        _ = JoinConfiguredChannelAsync(configured ?? new IrcChannelConfig { Name = channel, AutoJoin = true });
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
        string? autoRekeyTarget = null;
        if (_serverConfig.Irc.FishEnabled && FishCipher.IsEncrypted(text))
        {
            var prefix = text.StartsWith("+OK *") ? "CBC" : "ECB";
            // The +OK / +OK * prefix is plaintext FiSH protocol signaling — reliable
            // regardless of whether decryption succeeds. Sync stored mode to match the
            // peer so our outgoing encrypts use the cipher mode they can decrypt.
            // Previously this only ran on successful decrypts, leaving us stuck in CBC
            // when a peer's ECB-only client (mIRC fish_inj.dll etc.) sent us ECB messages
            // we couldn't decrypt.
            // The whole GetKey→decrypt→promote compound runs under _decryptPromoteLock so a
            // concurrent message for the same target can't promote the wrong alphabet variant.
            // Decrypt is synchronous; no await is taken inside this lock.
            lock (_decryptPromoteLock)
            {
                var keyEntry = _fishKeyStore.GetKey(effectiveTarget);
                if (keyEntry != null)
                {
                    var peerMode = prefix == "CBC" ? FishMode.CBC : FishMode.ECB;
                    if (keyEntry.Mode != peerMode)
                    {
                        _fishKeyStore.SetKeyWithAlt(effectiveTarget, keyEntry.Key, keyEntry.AltKey, peerMode);
                        Log.Information("FiSH mode updated for {Target}: {OldMode} → {NewMode} (from incoming prefix)",
                            effectiveTarget, keyEntry.Mode, peerMode);
                        keyEntry = _fishKeyStore.GetKey(effectiveTarget)!;
                    }

                    var keys = new[] { keyEntry.Key, keyEntry.AltKey };
                    // Key decisions (selection, promotion, streak, auto-rekey) use the reliable
                    // UTF-8 signal ONLY. The legacy charset is applied separately, display-only.
                    var (decrypted, winIdx, qualities) = FishCipher.DecryptWithFallback(text, null, keys);
                    var bestQ = winIdx >= 0 ? qualities[winIdx] : 0;
                    if (decrypted != null && bestQ >= FishCipher.FailedDecryptQualityThreshold)
                    {
                        Log.Information("FiSH PM {Target}: prefix={Prefix} cipherLen={CL} keyMask={KM} winKey={Idx} decrypted={Stats}",
                            effectiveTarget, prefix, text.Length, MaskKey(keyEntry.Key), winIdx, TextStats(decrypted));

                        // If a non-primary key won, swap so future encrypts/decrypts use it primary.
                        if (winIdx > 0)
                        {
                            var promoted = PromoteKeyToPrimary(keys, winIdx);
                            _fishKeyStore.SetKeyWithAlt(effectiveTarget, promoted[0], promoted[1], keyEntry.Mode);
                            Log.Information("FiSH key swap for {Target}: index {Idx} promoted to primary", effectiveTarget, winIdx);
                        }

                        text = decrypted;
                        wasEncrypted = true;
                        ResetDecryptFailStreak(effectiveTarget);
                    }
                    else if (TryDecryptWithAlternateKdf(effectiveTarget, text, keyEntry) is { } alt)
                    {
                        // Canonical key failed but a non-canonical KDF variant of the shared secret
                        // decrypted it — the peer's client derives its key differently. Promoted.
                        Log.Information("FiSH PM {Target}: recovered via alternate KDF '{Kdf}' — promoted to primary",
                            effectiveTarget, alt.Kdf);
                        text = alt.Text;
                        wasEncrypted = true;
                        ResetDecryptFailStreak(effectiveTarget);
                    }
                    else if (TryLegacyDisplay(text, keys) is { } legacyText)
                    {
                        // Readable only via the configured legacy charset (peer not using UTF-8).
                        // DISPLAY ONLY — key state is deliberately untouched (no streak reset, no
                        // promotion, no auto-rekey), so a false legacy decode can't corrupt keys or
                        // spam /keyx, and the UTF-8-driven machinery is unaffected.
                        Log.Information("FiSH PM {Target}: displayed via legacy charset {CS} (key state unchanged)",
                            effectiveTarget, _fishFallbackCharset!.WebName);
                        text = legacyText;
                        wasEncrypted = true;
                    }
                    else
                    {
                        // All keys produced garbage (wrong key entirely — peer using a different KDF or static key).
                        var ch = CipherHash(text);
                        Log.Warning("FiSH PM {Target}: decrypt failed. prefix={Prefix} cipherLen={CL} cipherHash={Hash} keyMask={KM} qualities=[{Q0:F2},{Q1:F2}] manual={Manual} hasSecret={HasSecret}",
                            effectiveTarget, prefix, text.Length, ch, MaskKey(keyEntry.Key),
                            qualities.Length > 0 ? qualities[0] : 0,
                            qualities.Length > 1 ? qualities[1] : 0,
                            keyEntry.Manual, !string.IsNullOrEmpty(keyEntry.DhSecretHex));
                        text = $"🔒 [FiSH decrypt failed — {prefix}, cipher {ch}]";
                        wasEncrypted = false;
                        PostDecryptFailureHint(effectiveTarget, keyEntry, prefix);
                        // Auto-rekey keys off the UTF-8-decode signal. On a server with a legacy
                        // FallbackCharset the peer isn't using UTF-8, so a UTF-8 failure is NOT
                        // evidence of a wrong key — suppress auto-rekey there (key management is
                        // manual /keyx) to avoid spurious re-keys on short legacy messages.
                        if (_fishFallbackCharset == null && ShouldAutoReKeyOnFailure(effectiveTarget, keyEntry))
                            autoRekeyTarget = effectiveTarget;
                    }
                }
                else
                {
                    Log.Information("FiSH PM {Target}: no key stored. prefix={Prefix} cipherLen={CL}",
                        effectiveTarget, prefix, text.Length);
                }
            }
        }

        // Fired OUTSIDE _decryptPromoteLock (InitiateKeyExchange does async I/O).
        if (autoRekeyTarget != null)
            TriggerAutoReKey(autoRekeyTarget);

        AddMessage(effectiveTarget, new IrcMessageItem { Nick = nick, Text = StripFormatting(text), WasEncrypted = wasEncrypted });
    }

    /// <summary>
    /// Transparently re-runs a DH1080 exchange for a target whose stored key stopped decrypting.
    /// Fire-and-forget. The caller (<see cref="ShouldAutoReKeyOnFailure"/>) is what bounds this —
    /// via the per-target cooldown and the <see cref="MaxAutoRekeyAttempts"/> per-episode cap — so
    /// a peer a fresh exchange can't repair gets at most a few INIT NOTICEs, not an endless stream.
    /// </summary>
    private void TriggerAutoReKey(string target)
    {
        Log.Information("FiSH auto-recovery: {Target} DH1080 key failed to decrypt {N}x — re-initiating key exchange",
            target, AutoRekeyFailThreshold);
        AddSystemMessage(target, "🔑 Encrypted messages stopped decrypting — automatically re-running the key exchange...");
        _ = InitiateKeyExchange(target);
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
        string? autoRekeyTarget = null;

        if (_serverConfig.Irc.FishEnabled && FishCipher.IsEncrypted(text))
        {
            var prefix = text.StartsWith("+OK *") ? "CBC" : "ECB";
            // Same atomic GetKey→decrypt→promote compound as HandlePrivmsg — guarded so a
            // concurrent message can't race the variant promotion. Decrypt is synchronous;
            // no await is taken inside this lock.
            lock (_decryptPromoteLock)
            {
                var keyEntry = _fishKeyStore.GetKey(effectiveTarget);
                if (keyEntry != null)
                {
                    // Sync stored mode from incoming prefix even on decrypt failure — see HandlePrivmsg.
                    var peerMode = prefix == "CBC" ? FishMode.CBC : FishMode.ECB;
                    if (keyEntry.Mode != peerMode)
                    {
                        _fishKeyStore.SetKeyWithAlt(effectiveTarget, keyEntry.Key, keyEntry.AltKey, peerMode);
                        Log.Information("FiSH mode updated for {Target}: {OldMode} → {NewMode} (from incoming notice prefix)",
                            effectiveTarget, keyEntry.Mode, peerMode);
                        keyEntry = _fishKeyStore.GetKey(effectiveTarget)!;
                    }

                    var keys = new[] { keyEntry.Key, keyEntry.AltKey };
                    // Key decisions (selection, promotion, streak, auto-rekey) use the reliable
                    // UTF-8 signal ONLY. The legacy charset is applied separately, display-only.
                    var (decrypted, winIdx, qualities) = FishCipher.DecryptWithFallback(text, null, keys);
                    var bestQ = winIdx >= 0 ? qualities[winIdx] : 0;
                    if (decrypted != null && bestQ >= FishCipher.FailedDecryptQualityThreshold)
                    {
                        if (winIdx > 0)
                        {
                            var promoted = PromoteKeyToPrimary(keys, winIdx);
                            _fishKeyStore.SetKeyWithAlt(effectiveTarget, promoted[0], promoted[1], keyEntry.Mode);
                        }

                        text = decrypted;
                        wasEncrypted = true;
                        ResetDecryptFailStreak(effectiveTarget);
                    }
                    else if (TryDecryptWithAlternateKdf(effectiveTarget, text, keyEntry) is { } alt)
                    {
                        Log.Information("FiSH NOTICE {Target}: recovered via alternate KDF '{Kdf}' — promoted to primary",
                            effectiveTarget, alt.Kdf);
                        text = alt.Text;
                        wasEncrypted = true;
                        ResetDecryptFailStreak(effectiveTarget);
                    }
                    else if (TryLegacyDisplay(text, keys) is { } legacyText)
                    {
                        // Display-only via the configured legacy charset — see HandlePrivmsg. Key
                        // state deliberately untouched.
                        Log.Information("FiSH NOTICE {Target}: displayed via legacy charset {CS} (key state unchanged)",
                            effectiveTarget, _fishFallbackCharset!.WebName);
                        text = legacyText;
                        wasEncrypted = true;
                    }
                    else
                    {
                        var ch = CipherHash(text);
                        Log.Warning("FiSH NOTICE {Target}: decrypt failed. prefix={Prefix} cipherLen={CL} cipherHash={Hash} keyMask={KM} qualities=[{Q0:F2},{Q1:F2}] manual={Manual} hasSecret={HasSecret}",
                            effectiveTarget, prefix, text.Length, ch, MaskKey(keyEntry.Key),
                            qualities.Length > 0 ? qualities[0] : 0,
                            qualities.Length > 1 ? qualities[1] : 0,
                            keyEntry.Manual, !string.IsNullOrEmpty(keyEntry.DhSecretHex));
                        text = $"🔒 [FiSH decrypt failed — {prefix}, cipher {ch}]";
                        wasEncrypted = false;
                        PostDecryptFailureHint(effectiveTarget, keyEntry, prefix);
                        // Auto-rekey keys off the UTF-8-decode signal. On a server with a legacy
                        // FallbackCharset the peer isn't using UTF-8, so a UTF-8 failure is NOT
                        // evidence of a wrong key — suppress auto-rekey there (key management is
                        // manual /keyx) to avoid spurious re-keys on short legacy messages.
                        if (_fishFallbackCharset == null && ShouldAutoReKeyOnFailure(effectiveTarget, keyEntry))
                            autoRekeyTarget = effectiveTarget;
                    }
                }
            }
        }

        if (autoRekeyTarget != null)
            TriggerAutoReKey(autoRekeyTarget);

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
                var orig = ch.Nicks[idx];
                var stripped = StripNickPrefix(orig);
                var prefix = orig.Substring(0, orig.Length - stripped.Length);
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
                _fishKeyStore.SetKey(ch.Name, fishMatch.Groups[2].Value, mode, manual: false);
            }
            else if (ch.Key.StartsWith('[') && ch.Key.EndsWith(']') && ch.Key.Length > 2)
            {
                // Bare brackets: [key] — use server's default FishMode
                var bareKey = ch.Key[1..^1];
                _fishKeyStore.SetKey(ch.Name, bareKey, _serverConfig.Irc.FishMode, manual: false);
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
        // Defensive validation — the right-click menu used to pass
        // ListBox.SelectedItem which could be null/stale if the user
        // right-clicked without first left-clicking. Per-item context
        // menu (v1.98) now resolves the binding to the actual nick, but
        // log + surface failure paths so future regressions are visible.
        if (string.IsNullOrWhiteSpace(nick))
        {
            Log.Warning("InitiateKeyExchange called with empty nick — likely a stale ContextMenu binding");
            AddSystemMessage("", "Key exchange aborted: no nick selected. Right-click a name in the user list.");
            return;
        }
        nick = nick.TrimStart('@', '+', '%', '~', '&').Trim();
        if (_client == null || !_client.IsConnected)
        {
            Log.Warning("InitiateKeyExchange for {Nick}: IRC client not connected", nick);
            AddSystemMessage(nick, "Key exchange aborted: IRC not connected.");
            return;
        }

        PruneKeyExchanges();

        var dh = new Dh1080();
        _pendingKeyExchanges[nick] = (dh, DateTime.UtcNow);
        var pub = dh.GetPublicKeyBase64();
        Log.Information("DH1080 INIT send to {Nick}: ourPubLen={Len} ourPubMask={Mask}",
            nick, pub.Length, MaskMid(pub));
        await _client.NoticeAsync(nick, Dh1080.FormatInit(pub));
        AddSystemMessage(nick, $"DH1080 key exchange initiated with {nick} — waiting for response...");
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

    /// <summary>Short stable hash of a ciphertext for triage logs (does not leak plaintext).</summary>
    private static string CipherHash(string cipher) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cipher)))[..16].ToLowerInvariant();

    /// <summary>
    /// Reorders a key array so the winning index is at position 0 and the others
    /// follow in their original order. Used to promote a successful alt key to
    /// primary so future encrypts use the variant the peer's client expects.
    /// </summary>
    private static string[] PromoteKeyToPrimary(string[] keys, int winningIndex)
    {
        var result = new string[keys.Length];
        result[0] = keys[winningIndex];
        var j = 1;
        for (var i = 0; i < keys.Length; i++)
            if (i != winningIndex) result[j++] = keys[i];
        return result;
    }

    /// <summary>
    /// Posts a one-time actionable hint to the affected tab when DH1080 keys are stored
    /// but decryption is failing. Most common cause: peer's client uses separate static
    /// keys per cipher mode; DH1080 only filled the CBC slot, so ECB messages encrypt
    /// with whatever stale key is in the peer's ECB slot. Workaround is a manual /key.
    /// </summary>
    private void PostDecryptFailureHint(string target, FishKeyEntry keyEntry, string prefix)
    {
        if (_decryptFailHintSent.Contains(target)) return;
        if (keyEntry.Manual) return; // manual key already in use — user knows what they did
        if (string.IsNullOrEmpty(keyEntry.AltKey)) return; // no DH1080 — likely no-key situation, different problem
        // Auto-recovery re-runs the key exchange first; only fall back to the manual-key advice
        // once we've EXHAUSTED the automatic re-key budget for this staleness episode (i.e. a
        // fresh exchange demonstrably isn't fixing it — likely a static/manual key on their side).
        if (_autoRekeyAttempts.GetValueOrDefault(target) < MaxAutoRekeyAttempts) return;

        _decryptFailHintSent.Add(target);
        AddSystemMessage(target,
            $"⚠ DH1080 succeeded but {prefix} decrypt is failing. Peer's client likely uses a separate static key for {prefix} mode. " +
            $"Ask {target} for a passphrase, then run /key {target} <passphrase> on both sides. /unkey {target} to retry /keyx later.");
    }

    /// <summary>
    /// DISPLAY-ONLY legacy-charset decode, used when UTF-8 decoding failed and the server has a
    /// FallbackCharset configured (peer's client doesn't use UTF-8). Returns the decoded text if it
    /// looks like real text in that charset, else null. The caller must NOT touch key state with
    /// this result: an 8-bit charset can't reliably confirm a key, so key management (streak,
    /// auto-rekey, promotion) stays driven by the UTF-8 signal — this only affects what's shown.
    /// </summary>
    private string? TryLegacyDisplay(string cipherText, string[] keys)
    {
        if (_fishFallbackCharset == null) return null;
        var (decrypted, winIdx, qualities) = FishCipher.DecryptWithFallback(cipherText, _fishFallbackCharset, keys);
        var bestQ = winIdx >= 0 ? qualities[winIdx] : 0;
        return decrypted != null && bestQ >= FishCipher.FailedDecryptQualityThreshold ? decrypted : null;
    }

    // Human labels for Dh1080.AlternateKdfCandidates, in the same order it yields. Logged so a
    // successful recovery tells us EXACTLY which non-canonical KDF the peer's client uses.
    private static readonly string[] AlternateKdfLabels =
    {
        "std-base64-keep-eq/natural(44)",
        "std-base64-keep-eq/fixed(44)",
        "std-base64-trim/fixed(43)",
        "fish-base64/fixed(43)",
        "raw-sha256/natural(32)",
        "raw-sha256/fixed(32)",
    };

    /// <summary>
    /// The canonical key failed — try the known non-canonical DH1080 KDF variants derived from the
    /// stored shared secret (peers that keep '=' padding, hash a fixed-width secret, or use the raw
    /// digest). On success, PROMOTE the winning key to primary so future messages decrypt directly,
    /// and return the plaintext plus the KDF label (for diagnostics). Null if nothing recovers it.
    /// Called while holding <see cref="_decryptPromoteLock"/>; fully synchronous.
    /// </summary>
    private (string Text, string Kdf)? TryDecryptWithAlternateKdf(string target, string cipherText, FishKeyEntry keyEntry)
    {
        if (string.IsNullOrEmpty(keyEntry.DhSecretHex)) return null;
        var candidates = Dh1080.AlternateKdfCandidates(keyEntry.DhSecretHex).ToArray();
        if (candidates.Length == 0) return null;

        // NOTE: speculative candidates are tried UTF-8-ONLY (no legacy fallback). A legacy 8-bit
        // charset maps nearly every byte to a glyph, so allowing it here would let a WRONG candidate
        // "decode" random bytes into plausible text and get promoted. Requiring valid UTF-8 keeps the
        // strong wrong-key signal for key selection; the legacy fallback still applies on the primary
        // path once the right key is promoted.
        var (decrypted, winIdx, qualities) = FishCipher.DecryptWithFallback(cipherText, null, candidates);
        var bestQ = winIdx >= 0 ? qualities[winIdx] : 0;
        // Require HIGH confidence (not the 0.5 floor): we try several speculative keys and the
        // correct KDF yields ~1.0 quality.
        if (decrypted == null || bestQ < 0.85) return null;

        var winner = candidates[winIdx];

        // CORROBORATION: don't act on a single match. Blowfish on a WRONG key can, for a short
        // block, produce a NUL-padded fragment that trims to a plausible-looking string ≥0.85 —
        // promoting that would discard the real key and break the peer both ways. Require the
        // SAME candidate to decrypt a second message before we trust it. First sighting is
        // recorded and shown as still-undecryptable.
        if (_altKdfPending.GetValueOrDefault(target) != winner)
        {
            _altKdfPending[target] = winner;
            return null;
        }
        _altKdfPending.Remove(target);

        // Corroborated. Promote the winner to primary (so future messages + our outgoing use it),
        // but keep the canonical standard key as the alternate so a mistaken promotion self-corrects
        // on the next canonically-encrypted message. SetKeyWithAlt preserves DhSecretHex.
        _fishKeyStore.SetKeyWithAlt(target, winner, keyEntry.Key, keyEntry.Mode);
        var label = winIdx < AlternateKdfLabels.Length ? AlternateKdfLabels[winIdx] : $"candidate#{winIdx}";
        return (decrypted, label);
    }

    /// <summary>
    /// Records a decrypt failure for <paramref name="target"/> and returns true if we should
    /// transparently re-run the DH1080 key exchange (a stale/corrupt DH-derived key can never
    /// recover otherwise). Only applies to private-message, DH-derived (non-manual) keys, and
    /// is rate-limited by <see cref="AutoRekeyCooldown"/>. Must be called while holding
    /// <see cref="_decryptPromoteLock"/>. Reset the streak via <see cref="ResetDecryptFailStreak"/>
    /// on a successful decrypt.
    /// </summary>
    private bool ShouldAutoReKeyOnFailure(string target, FishKeyEntry keyEntry)
    {
        // Manual keys and channel keys are never DH-negotiated — leave them alone.
        // A DH1080-derived key always has a non-empty alternate-alphabet variant.
        if (keyEntry.Manual || IsChannel(target) || string.IsNullOrEmpty(keyEntry.AltKey))
            return false;

        // Already exhausted our re-key budget for this episode — stop (a fresh exchange isn't
        // fixing it). Don't even grow the streak; ResetDecryptFailStreak on a good decrypt clears
        // this and lets a genuinely-recovered key re-key again in a future episode.
        var attempts = _autoRekeyAttempts.GetValueOrDefault(target);
        if (attempts >= MaxAutoRekeyAttempts) return false;

        var streak = _decryptFailStreak.GetValueOrDefault(target) + 1;
        _decryptFailStreak[target] = streak;

        var sinceLast = _lastAutoRekey.TryGetValue(target, out var last)
            ? DateTime.UtcNow - last
            : TimeSpan.MaxValue;
        if (!ShouldAutoRekey(streak, sinceLast, attempts)) return false;

        _lastAutoRekey[target] = DateTime.UtcNow;
        _autoRekeyAttempts[target] = attempts + 1;
        _decryptFailStreak[target] = 0;
        return true;
    }

    /// <summary>
    /// Pure decision: given the current consecutive-failure streak (including the failure being
    /// handled), how long since the last automatic re-key for this target, and how many auto-rekeys
    /// we've already fired without a successful decrypt, should we re-key? Requires the streak to
    /// reach <see cref="AutoRekeyFailThreshold"/>, the cooldown to have elapsed, AND the per-episode
    /// attempt budget (<see cref="MaxAutoRekeyAttempts"/>) not to be exhausted. Internal for testing.
    /// </summary>
    internal static bool ShouldAutoRekey(int failStreakIncludingThis, TimeSpan sinceLastRekey, int priorAttempts)
        => priorAttempts < MaxAutoRekeyAttempts
           && failStreakIncludingThis >= AutoRekeyFailThreshold
           && sinceLastRekey >= AutoRekeyCooldown;

    // A successful decrypt ends the staleness episode: clear the failure streak, the auto-rekey
    // attempt budget, the cooldown stamp, and the one-shot manual-key hint latch, so a future
    // episode starts clean (and can auto-recover + hint again).
    private void ResetDecryptFailStreak(string target)
    {
        _decryptFailStreak.Remove(target);
        _autoRekeyAttempts.Remove(target);
        _lastAutoRekey.Remove(target);
        _decryptFailHintSent.Remove(target);
        _altKdfPending.Remove(target);
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
            var (primaryKey, altKey, secretHex, ourPub) = await Task.Run(() =>
            {
                var d = new Dh1080();
                var v = d.ComputeAllKeyVariantsWithSecret(theirPubKey); // validates peer key + derives variants + secret
                return (v.Standard, v.Fish, v.SecretHex, d.GetPublicKeyBase64());
            });
            // Store both alphabet variants of the derived key — the canonical key is
            // standard-base64(SHA256(shared)) (mIRC FiSH 10, weechat, HexChat, KVIrc);
            // the fish-alphabet variant is a defensive fallback. First decrypt picks the
            // right one and promotes it to primary.
            // Mode follows the server's configured FishMode — defaults to ECB on new
            // configs because older mIRC clients don't support CBC at all. Modern
            // peers' first incoming message will flip us to CBC via the prefix detector.
            // SetDh1080Keys refuses to overwrite a manually-set key.
            var dhMode = _serverConfig.Irc.FishMode;
            if (!_fishKeyStore.SetDh1080Keys(nick, primaryKey, altKey, secretHex, dhMode))
            {
                Log.Warning("DH1080 INIT from {Nick} ignored: manual key already set; not overwriting", nick);
                AddSystemMessage(nick, "Ignored incoming /keyx — a manual key is already set. Use /unkey first if you want DH1080.");
                return;
            }

            Log.Information("DH1080 FINISH send to {Nick}: ourPubLen={Len} ourPubMask={Mask} primaryKey={KM} altKey={AM}",
                nick, ourPub.Length, MaskMid(ourPub), MaskKey(primaryKey), MaskKey(altKey));

            await _client.NoticeAsync(nick, Dh1080.FormatFinish(ourPub));
            AddSystemMessage(nick, $"DH1080 key exchange completed (initiated by {nick}) — {dhMode} mode");
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

            var (primaryKey, altKey, secretHex) = entry.dh.ComputeAllKeyVariantsWithSecret(theirPubKey);
            // Store both alphabet variants + the shared secret — see HandleDh1080InitAsync.
            _pendingKeyExchanges.Remove(nick);
            var dhMode = _serverConfig.Irc.FishMode;
            if (!_fishKeyStore.SetDh1080Keys(nick, primaryKey, altKey, secretHex, dhMode))
            {
                Log.Warning("DH1080 FINISH from {Nick} ignored: manual key already set; not overwriting", nick);
                AddSystemMessage(nick, "DH1080 response received but a manual key is already set — keeping manual. Use /unkey first if you want DH1080.");
                return;
            }

            Log.Information("DH1080 derived for {Nick}: primaryKey={KM} altKey={AM}",
                nick, MaskKey(primaryKey), MaskKey(altKey));

            AddSystemMessage(nick, $"DH1080 key exchange completed — {dhMode} mode");
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
        AddSystemMessage(target, $"FiSH key set ({mode}, manual)");
    }

    public void RemoveFishKey(string target)
    {
        _fishKeyStore.RemoveKey(target);
        if (_channels.TryGetValue(target, out var ch))
            ch.HasFishKey = false;
        AddSystemMessage(target, "FiSH key removed");
    }

    /// <summary>
    /// Flip the FiSH cipher mode for an existing key without re-keying. Used when a peer's
    /// client only supports one mode (e.g. older mIRC fish_inj.dll = ECB-only) and our
    /// outgoing messages would otherwise be unintelligible to them.
    /// </summary>
    public void SetFishMode(string target, FishMode mode)
    {
        var entry = _fishKeyStore.GetKey(target);
        if (entry == null)
        {
            AddSystemMessage(target, $"No FiSH key for {target} — set one with /key or /keyx first.");
            return;
        }
        if (entry.Mode == mode)
        {
            AddSystemMessage(target, $"FiSH mode for {target} already {mode}");
            return;
        }
        _fishKeyStore.SetKeyWithAlt(target, entry.Key, entry.AltKey, mode);
        AddSystemMessage(target, $"FiSH mode for {target} set to {mode}");
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
