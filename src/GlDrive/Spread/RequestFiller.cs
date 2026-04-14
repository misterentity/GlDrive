using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Services;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// RaceTrade-style auto request filler. Monitors IRC for request announces on
/// one server, searches other connected servers for the requested release,
/// and if found starts a single-destination race that uploads to the requester.
///
/// Per-server config lives in IrcConfig.RequestFiller. Subscribes to the
/// server's IrcService.MessageReceived event during ServerManager setup.
/// </summary>
public class RequestFiller : IDisposable
{
    private readonly string _requesterServerId;
    private readonly IrcService _ircService;
    private readonly RequestFillerConfig _config;
    private readonly ServerManager _serverManager;
    private readonly SpreadManager _spreadManager;
    private readonly Regex? _pattern;

    private readonly Lock _lock = new();
    private readonly Queue<DateTime> _recentFills = new();
    private DateTime _lastFillAt = DateTime.MinValue;
    private readonly HashSet<string> _recentRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recentOrder = new();

    public RequestFiller(
        string requesterServerId,
        IrcService ircService,
        RequestFillerConfig config,
        ServerManager serverManager,
        SpreadManager spreadManager)
    {
        _requesterServerId = requesterServerId;
        _ircService = ircService;
        _config = config;
        _serverManager = serverManager;
        _spreadManager = spreadManager;

        try
        {
            _pattern = new Regex(config.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RequestFiller: invalid pattern for {Server}: {Pattern}",
                requesterServerId, config.Pattern);
            _pattern = null;
            return;
        }

        if (!config.Enabled) return;

        _ircService.MessageReceived += OnMessage;
        Log.Information("RequestFiller active for {Server} (pattern={Pattern}, channel={Channel})",
            requesterServerId, config.Pattern,
            string.IsNullOrEmpty(config.Channel) ? "*" : config.Channel);
    }

    private void OnMessage(string target, IrcMessageItem message)
    {
        if (_pattern == null) return;
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice) return;
        if (!string.IsNullOrEmpty(_config.Channel) &&
            !target.Equals(_config.Channel, StringComparison.OrdinalIgnoreCase)) return;

        Match match;
        try { match = _pattern.Match(message.Text); }
        catch (RegexMatchTimeoutException) { return; }
        if (!match.Success) return;

        var release = match.Groups["release"].Success
            ? match.Groups["release"].Value.Trim()
            : null;
        if (string.IsNullOrEmpty(release) || release.Length < 5) return;

        // Dedupe + rate-limit
        lock (_lock)
        {
            if (!_recentRequests.Add(release)) return;
            _recentOrder.AddLast(release);
            while (_recentRequests.Count > 200 && _recentOrder.First != null)
            {
                _recentRequests.Remove(_recentOrder.First.Value);
                _recentOrder.RemoveFirst();
            }

            var now = DateTime.UtcNow;
            if ((now - _lastFillAt).TotalSeconds < _config.CooldownSeconds) return;

            while (_recentFills.Count > 0 && (now - _recentFills.Peek()).TotalHours >= 1)
                _recentFills.Dequeue();
            if (_recentFills.Count >= _config.MaxPerHour) return;

            _lastFillAt = now;
            _recentFills.Enqueue(now);
        }

        // Fire and forget — search + race
        _ = Task.Run(() => TryFill(release));
    }

    private async Task TryFill(string release)
    {
        try
        {
            Log.Information("RequestFiller: searching for {Release}", release);

            // Search all OTHER connected servers for the release
            foreach (var sourceId in _serverManager.ConnectedServerIds)
            {
                if (sourceId == _requesterServerId) continue;
                var sourceMount = _serverManager.GetServer(sourceId);
                if (sourceMount?.Search is not { } searcher) continue;

                List<SearchResult> results;
                try { results = await searcher.Search(release); }
                catch (Exception ex)
                {
                    Log.Debug(ex, "RequestFiller: search failed on {Source}", sourceId);
                    continue;
                }

                var exact = results.FirstOrDefault(r =>
                    r.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase));
                if (exact == null) continue;

                Log.Information("RequestFiller: found {Release} on {Source} at {Path} — racing to {Target}",
                    release, sourceId, exact.RemotePath, _requesterServerId);

                _spreadManager.StartRace(
                    exact.Category,
                    release,
                    new[] { sourceId, _requesterServerId },
                    SpreadMode.Race,
                    knownSourceServerId: sourceId,
                    knownSourcePath: exact.RemotePath);
                return;
            }

            Log.Information("RequestFiller: no source found for {Release}", release);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RequestFiller: fill failed for {Release}", release);
        }
    }

    public void Dispose()
    {
        _ircService.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}
