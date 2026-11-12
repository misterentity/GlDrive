using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.Spread;

/// <summary>The site, IRC and race operations a <see cref="RequestFiller"/> needs.</summary>
public sealed class RequestFillerHost
{
    /// <summary>Find the release on another connected site; null when no site holds it.</summary>
    public required Func<string, CancellationToken, Task<SearchResult?>> FindSource { get; init; }
    /// <summary>Directory names directly under a path on the requesting site; null when the listing failed.</summary>
    public required Func<string, CancellationToken, Task<IReadOnlyList<string>?>> ListDirectories { get; init; }
    /// <summary>(section, release, sourceServerId, sourcePath, destinationPath) → admission result.</summary>
    public required Func<string, string, string, string, string, RaceStartResult> StartFill { get; init; }
    /// <summary>True while the race engine still holds this fill (queued, resuming or running).</summary>
    public required Func<string, bool> IsFillPending { get; init; }
    /// <summary>Withdraw a fill another filler has beaten us to.</summary>
    public required Action<string, string> CancelFill { get; init; }
    public required Func<string, string, Task<bool>> SendIrc { get; init; }
    public required Func<string, CancellationToken, Task<bool>> SendSiteCommand { get; init; }
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Fills site requests. The sitebot's request announce (or the requests directory
/// itself) opens a request; the release is searched for on the other connected
/// sites, raced into <c>&lt;requests&gt;/REQUEST-&lt;release&gt;/&lt;release&gt;</c>, and the
/// request is marked filled once the upload is complete.
///
/// A fill only ever starts while the site's own request directory exists, so a
/// forged announce cannot make this upload anything.
/// </summary>
public sealed class RequestFiller : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FillTimeout = TimeSpan.FromHours(3);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinListInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan VanishGrace = TimeSpan.FromMinutes(2);

    private readonly string _serverId;
    private readonly RequestFillerConfig _config;
    private readonly RequestFillerHost _host;
    private readonly RequestAnnounceParser _parser;
    private readonly RequestTracker _tracker;
    private readonly string _requestPath;
    private readonly SemaphoreSlim _pumping = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Section, DateTime StartedUtc)> _fills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _timer;
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private DateTime _lastListUtc = DateTime.MinValue;
    private readonly HashSet<string> _goneSinceTerminal = new(StringComparer.OrdinalIgnoreCase);
    private Task _announcing = Task.CompletedTask;
    // Where the sitebot talks about requests; used for ones found only in the directory.
    private volatile string? _requestChannel;

    internal Task Announcing => _announcing;

    public RequestFiller(string serverId, RequestFillerConfig config, RequestFillerHost host, bool startTimer = true)
    {
        _serverId = serverId;
        _config = config;
        _host = host;
        _parser = new RequestAnnounceParser(config.Pattern, config.FilledPattern, config.DirectoryPrefix);
        foreach (var pattern in _parser.InvalidPatterns)
            Log.Warning("RequestFiller: invalid pattern for {Server}, using the default: {Pattern}", serverId, pattern);
        _tracker = new RequestTracker(new RequestTrackerOptions
        {
            MaxPerHour = config.MaxPerHour,
            CooldownSeconds = config.CooldownSeconds
        });
        _requestPath = "/" + (string.IsNullOrWhiteSpace(config.RequestPath) ? "requests" : config.RequestPath).Trim().Trim('/');

        Log.Information("RequestFiller active for {Server} (requests={Path}, channel={Channel}, bot={Bot})",
            serverId, _requestPath, string.IsNullOrEmpty(config.Channel) ? "*" : config.Channel,
            string.IsNullOrEmpty(config.BotNick) ? "*" : config.BotNick);
        if (startTimer)
            _timer = new Timer(_ => _ = PumpAsync(), null, TimeSpan.FromSeconds(20), PumpInterval);
    }

    public IReadOnlyList<TrackedRequest> Requests => _tracker.Snapshot();

    /// <summary>Unsubscribes this filler from its event sources; run once on dispose.</summary>
    public Action? Detach { get; set; }

    /// <summary>Request chatter must not reach the release-announce pipeline as a NEW release.</summary>
    public bool IsRequestTraffic(string text) =>
        MentionsRequestDirectory(text)
        || text.TrimStart().StartsWith("!req", StringComparison.OrdinalIgnoreCase)
        || _parser.Parse(text) != null;

    // "REQUEST-" opening a path segment or word, not the tail of a release name
    // such as By_Special_Request-WEB.
    private bool MentionsRequestDirectory(string text)
    {
        var prefix = _parser.DirectoryNameFor("");
        for (var at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(prefix, at + 1, StringComparison.OrdinalIgnoreCase))
            if (at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '_' or '.' or '-'))
                return true;
        return false;
    }

    public void OnMessage(string target, IrcMessageItem message)
    {
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice) return;
        if (!IrcLogStore.IsChannelName(target)) return;
        if (!string.IsNullOrEmpty(_config.Channel) &&
            !target.Equals(_config.Channel, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrEmpty(_config.BotNick) &&
            !message.Nick.Equals(_config.BotNick, StringComparison.OrdinalIgnoreCase)) return;

        var ev = _parser.Parse(message.Text);
        if (ev == null) return;
        _requestChannel = target;
        var now = _host.UtcNow();

        if (ev.Kind == RequestEventKind.Filled)
        {
            if (_tracker.Get(ev.Release) is not { State: RequestState.Open or RequestState.Filling } tracked) return;
            _tracker.MarkFilledElsewhere(ev.Release);
            string? section;
            lock (_lock) section = _fills.Remove(ev.Release, out var fill) ? fill.Section : null;
            Log.Information("RequestFiller: {Release} was filled by {Nick}", ev.Release, ev.Nick ?? "someone else");
            if (tracked.State == RequestState.Filling && section != null)
                Try(() => _host.CancelFill(section, ev.Release), "cancel fill");
            return;
        }

        if (_tracker.Observe(ev.Release, ev.Number, target, now))
            Log.Information("RequestFiller: request #{Number} {Release} from {Nick} on {Server}",
                ev.Number, ev.Release, ev.Nick ?? "?", _serverId);
        _ = PumpAsync();
    }

    /// <summary>A release just appeared on some site; an open request for it is worth retrying now.</summary>
    public void OnReleaseSeen(string release)
    {
        if (_tracker.Get(release) is not { State: RequestState.Open }) return;
        _tracker.Nudge(release, _host.UtcNow());
        _ = PumpAsync();
    }

    public void OnJobEnded(SpreadJob job)
    {
        // Only a race that carried OUR request directory can fill the request; an
        // ordinary race of the same release completes somewhere else on the site.
        var complete = job.DestinationPaths?.ContainsKey(_serverId) == true
            && job.Sites.TryGetValue(_serverId, out var progress) && progress.IsComplete;
        job.Sites.TryGetValue(_serverId, out var site);
        OnFillEnded(job.ReleaseName, complete,
            $"{job.State}, {site?.FilesOwned ?? 0}/{site?.FilesTotal ?? 0} files");
    }

    internal void OnFillEnded(string release, bool complete, string detail)
    {
        lock (_lock)
            if (!_fills.ContainsKey(release)) return;
        // The engine resumes an interrupted fill by itself; it reports again when that ends.
        if (!complete && _host.IsFillPending(release)) return;
        lock (_lock) _fills.Remove(release);
        if (_tracker.Get(release) is not { State: RequestState.Filling } request) return;

        if (!complete)
        {
            _tracker.FillFailed(release, _host.UtcNow());
            Log.Warning("RequestFiller: fill of {Release} ended incomplete ({Detail})", release, detail);
            return;
        }
        _tracker.FillCompleted(release);
        _announcing = AnnounceFilledAsync(request);
    }

    internal async Task PumpAsync()
    {
        if (_cts.IsCancellationRequested || !await _pumping.WaitAsync(0)) return;
        try
        {
            var ct = _cts.Token;
            var now = _host.UtcNow();
            ExpireStaleFills(now);
            // One LIST per round, and only when there is something to act on: the
            // requesting site's main pool may be a single login.
            if (_tracker.DueForSearch(now).Count == 0 && now - _lastReconcileUtc < ReconcileInterval) return;
            if (now - _lastListUtc < MinListInterval) return;
            _lastListUtc = now;

            // The requests directory is the authority on what is open. It adds
            // requests made before startup or over FTP, and retires filled ones.
            var listed = await _host.ListDirectories(_requestPath, ct);
            if (listed == null)
            {
                Log.Debug("RequestFiller: could not list {Path} on {Server}; no fills this round", _requestPath, _serverId);
                return;
            }
            _lastReconcileUtc = now;
            // Keep the directory names exactly as the site spells them.
            var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in listed)
                if (_parser.ReleaseFromDirectoryName(name) is { } listedRelease)
                    open.TryAdd(listedRelease, name);
            ReconcileTerminal(open, now);
            _tracker.Reconcile(open.Keys, now);
            WithdrawVanishedFills(open, now);

            foreach (var request in _tracker.DueForSearch(now))
            {
                ct.ThrowIfCancellationRequested();
                if (!open.TryGetValue(request.Release, out var directory)) continue; // directory not created yet

                var source = await _host.FindSource(request.Release, ct);
                now = _host.UtcNow();
                if (source == null)
                {
                    _tracker.SearchMissed(request.Release, now);
                    Log.Information("RequestFiller: no source yet for {Release} (attempt {Attempt})",
                        request.Release, request.SearchAttempts + 1);
                    continue;
                }
                if (!_tracker.TryBeginFill(request.Release, now))
                {
                    Log.Information("RequestFiller: fill of {Release} deferred by the rate limit", request.Release);
                    continue;
                }

                var destination = $"{_requestPath}/{directory}/{_parser.ReleaseFromDirectoryName(directory)}";
                // Registered before admission: a race that ends quickly must find its fill.
                lock (_lock) _fills[request.Release] = (source.Category, now);
                var result = _host.StartFill(source.Category, request.Release, source.ServerId, source.RemotePath, destination);
                if (result.Outcome is RaceStartOutcome.Started or RaceStartOutcome.Queued or RaceStartOutcome.Duplicate)
                {
                    Log.Information("RequestFiller: filling {Release} from {Source}:{SourcePath} to {Destination} ({Outcome})",
                        request.Release, source.ServerId, source.RemotePath, destination, result.Outcome);
                }
                else
                {
                    lock (_lock) _fills.Remove(request.Release);
                    _tracker.FillFailed(request.Release, now);
                    Log.Information("RequestFiller: could not start fill of {Release}: {Outcome} {Reason}",
                        request.Release, result.Outcome, result.Reason);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "RequestFiller: pump failed for {Server}", _serverId); }
        finally { _pumping.Release(); }
    }

    // A finished request whose directory went away and came back is a new request,
    // even when the sitebot's announce for it was never seen.
    private void ReconcileTerminal(Dictionary<string, string> open, DateTime now)
    {
        foreach (var request in _tracker.Snapshot())
        {
            if (request.State is not (RequestState.Filled or RequestState.Closed)) continue;
            if (!open.ContainsKey(request.Release)) _goneSinceTerminal.Add(request.Release);
            else if (_goneSinceTerminal.Remove(request.Release))
                _tracker.Observe(request.Release, null, null, now);
        }
    }

    // The site removed or renamed the request directory while our fill was pending.
    private void WithdrawVanishedFills(Dictionary<string, string> open, DateTime now)
    {
        List<(string Release, string Section)> vanished;
        lock (_lock)
            vanished = _fills.Where(f => !open.ContainsKey(f.Key) && now - f.Value.StartedUtc > VanishGrace)
                .Select(f => (f.Key, f.Value.Section)).ToList();
        foreach (var (release, section) in vanished)
        {
            lock (_lock) _fills.Remove(release);
            _tracker.MarkFilledElsewhere(release);
            Log.Information("RequestFiller: request directory for {Release} is gone; withdrawing the fill", release);
            Try(() => _host.CancelFill(section, release), "cancel fill");
        }
    }

    // A fill whose race was parked or lost never reports back; free the request to retry.
    private void ExpireStaleFills(DateTime now)
    {
        List<string> stale;
        lock (_lock)
        {
            stale = _fills.Where(f => now - f.Value.StartedUtc > FillTimeout).Select(f => f.Key).ToList();
            foreach (var release in stale) _fills.Remove(release);
        }
        foreach (var release in stale)
        {
            _tracker.FillFailed(release, now);
            Log.Warning("RequestFiller: fill of {Release} did not finish within {Hours} h", release, FillTimeout.TotalHours);
        }
    }

    private async Task AnnounceFilledAsync(TrackedRequest request)
    {
        try
        {
            var command = (string.IsNullOrWhiteSpace(_config.FilledCommand) ? "!reqfilled {release}" : _config.FilledCommand)
                .Replace("{release}", request.Release)
                .Replace("{number}", request.Number?.ToString() ?? "")
                .Trim();
            bool sent;
            if (command.StartsWith("SITE ", StringComparison.OrdinalIgnoreCase))
                sent = await _host.SendSiteCommand(command, _cts.Token);
            else
            {
                var channel = !string.IsNullOrEmpty(_config.Channel) ? _config.Channel : request.Channel ?? _requestChannel;
                if (string.IsNullOrEmpty(channel))
                {
                    Log.Warning("RequestFiller: {Release} is filled but no channel is known for \"{Command}\" — " +
                        "set irc.requestFiller.channel", request.Release, command);
                    return;
                }
                sent = await _host.SendIrc(channel, command);
            }
            if (sent) Log.Information("RequestFiller: filled {Release} — sent \"{Command}\"", request.Release, command);
            else Log.Warning("RequestFiller: {Release} is filled but \"{Command}\" was not delivered", request.Release, command);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "RequestFiller: could not mark {Release} filled", request.Release); }
    }

    private static void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { Log.Debug(ex, "RequestFiller: {What} failed", what); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        Try(() => Detach?.Invoke(), "detach");
    }
}
