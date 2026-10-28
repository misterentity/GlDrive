using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class WishlistMatcher
{
    private readonly WishlistStore _wishlist;
    private readonly Func<DownloadItem, bool> _enqueue;
    private readonly Action<WishlistAttemptEvent>? _recordAttempt;
    private readonly DownloadConfig _config;
    private readonly string _serverId;
    private string _serverName;
    private readonly Lock _wantedLifecycle = new();
    private WantedAcquisitionCoordinator? _wantedCoordinator;
    private bool _wantedAttached;

    internal string ServerId => _serverId;

    public void AttachWantedCoordinator(WantedAcquisitionCoordinator coordinator)
    {
        lock (_wantedLifecycle) { _wantedCoordinator = coordinator; _wantedAttached = true; }
        coordinator.RegisterMatcher(this);
    }

    internal void DetachWantedCoordinator()
    {
        lock (_wantedLifecycle) _wantedAttached = false;
    }

    public void Deactivate() => DetachWantedCoordinator();

    public bool TryAcquireSeries(string itemId, string category, string release, string path)
    {
        lock (_wantedLifecycle)
            return _wantedAttached && _wishlist.GetById(itemId) is { MonitorMode: not SeriesMonitorMode.Legacy } &&
                ProcessRelease(category, release, path, false, null, itemId);
    }

    internal void UpdateServerName(string name) => _serverName = name;

    public event Action<WishlistItem, string, string>? MatchFound; // item, category, release

    public WishlistMatcher(WishlistStore wishlist, DownloadManager downloadManager, FtpOperations ftp,
        DownloadConfig config, string serverId, string serverName)
        : this(wishlist, downloadManager.Enqueue, config, serverId, serverName,
            attempt => App.TelemetryRecorder?.Record(TelemetryStream.WishlistAttempts, attempt))
    {
        downloadManager.AttachWishlist(wishlist, serverId);
    }

    internal WishlistMatcher(WishlistStore wishlist, Func<DownloadItem, bool> enqueue,
        DownloadConfig config, string serverId, string serverName, Action<WishlistAttemptEvent>? recordAttempt = null)
    {
        _wishlist = wishlist;
        _enqueue = enqueue;
        _recordAttempt = recordAttempt;
        _config = config;
        _serverId = serverId;
        _serverName = serverName;
    }

    public void OnIrcRelease(ServerConfig server, string section, string releaseName)
    {
        var remotePath = WishlistIrcRelease.ResolvePath(server, section, releaseName);
        if (remotePath == null)
        {
            Log.Debug("Wishlist IRC announce skipped: no safe path for [{Section}] {Release}", section, releaseName);
            return;
        }
        OnNewRelease(section, releaseName, remotePath, fromIrc: true);
    }

    public void OnNewRelease(string category, string releaseName, string remotePath, bool fromIrc = false, long? sizeBytes = null)
    {
        if (_wantedCoordinator != null && _config.WantedDecisionWindowEnabled)
        {
            if (_wantedAttached) _wantedCoordinator.Offer(this, category, releaseName, remotePath,
                WantedReadiness.Unknown, fromIrc, sizeBytes);
            return;
        }
        ProcessRelease(category, releaseName, remotePath, fromIrc, sizeBytes);
    }

    public void OnIrcCompletion(ServerConfig server, string section, string releaseName,
        long? sizeBytes = null, DateTimeOffset? completedAtUtc = null)
    {
        if (!_config.WantedDecisionWindowEnabled || !_config.AutoDownloadWishlist || !_wantedAttached || _wantedCoordinator == null) return;
        var path = WishlistIrcRelease.ResolvePath(server, section, releaseName);
        if (path != null) _wantedCoordinator.Offer(this, section, releaseName, path,
            WantedReadiness.TrustedComplete, fromIrc: true, sizeBytes: sizeBytes, completeAtUtc: completedAtUtc);
    }

    internal bool DispatchWanted(string itemId, string category, string release, string path,
        bool fromIrc, long? sizeBytes, Func<bool> stillEligible)
    {
        // Detaching waits for an admission already in progress, so a remount
        // cannot enqueue onto a DownloadManager after RemoveServer returns.
        lock (_wantedLifecycle)
            return _wantedAttached && _config.WantedDecisionWindowEnabled && _config.AutoDownloadWishlist && stillEligible() &&
                ProcessRelease(category, release, path, fromIrc, sizeBytes, itemId);
    }

    private bool ProcessRelease(string category, string releaseName, string remotePath,
        bool fromIrc, long? sizeBytes, string? wantedItemId = null)
    {
        var admitted = false;
        try
        {
            foreach (var item in _wishlist.Items)
            {
                if (wantedItemId != null && item.Id != wantedItemId) continue;
                // Shared across matchers/sites, and claimed before evaluation. No store
                // lock is held while calling DownloadManager or raising UI events.
                var scored = _config.ScoredQualityEnabled;
                var tracked = scored || item.MonitorMode != SeriesMonitorMode.Legacy;
                var allowCompleted = scored && WishlistAcquisition.ResolveProfile(item, _config)?.UpgradeProperRepackAfterCutoff == true;
                if (!_wishlist.TryBeginReleaseEvaluation(item.Id, releaseName, allowCompleted)) continue;
                DownloadItem? downloadItem = null;
                bool reservation = false, enqueueAttempted = false;
                try
                {
                    bool matches = tracked ? WishlistAcquisition.MatchesTitle(item, releaseName) : item.Type switch
                    {
                        MediaType.Movie => SceneNameParser.MatchesMovie(releaseName, item.Title, item.Year, item.Quality),
                        MediaType.TvShow => SceneNameParser.MatchesTvEpisode(releaseName, item.Title, item.Quality),
                        _ => false
                    };

                    string? missReason = matches ? null : ClassifyMissReason(releaseName, item);
                    int? qualityScore = null;
                    if (matches)
                    {
                        downloadItem = new DownloadItem
                        {
                            RemotePath = remotePath, ReleaseName = releaseName,
                            LocalPath = BuildLocalPath(item, category!, releaseName),
                            WishlistItemId = item.Id, Category = category!,
                            ServerId = _serverId ?? "", ServerName = _serverName,
                            BackgroundWantedPull = item.MonitorMode != SeriesMonitorMode.Legacy,
                            TotalBytes = sizeBytes is > 0 ? sizeBytes.Value : 0
                        };
                        if (tracked)
                        {
                            var decision = _wishlist.TryReserveScoredGrab(item.Id, downloadItem, _config, sizeBytes);
                            matches = reservation = decision.Accepted;
                            qualityScore = decision.Score;
                            missReason = matches ? null : decision.Reason;
                        }
                    }
                    try
                    {
                        _recordAttempt?.Invoke(new WishlistAttemptEvent
                            {
                                WishlistItemId = item.Id,
                                Release = releaseName,
                                Score = qualityScore ?? (matches ? 1.0 : 0.0),
                                Matched = matches,
                                MissReason = missReason,
                                Section = category ?? "",
                                ServerId = _serverId ?? ""
                            });
                    }
                    catch (Exception ex) { Log.Debug(ex, "WishlistAttempt emit failed"); }

                    if (!matches)
                    {
                        _wishlist.CompleteReleaseEvaluation(item.Id, releaseName);
                        continue;
                    }

                    Log.Information("Wishlist match: [{Category}] {Release} -> {Title} (server: {Server})",
                        category, releaseName, item.Title, _serverName);

                    enqueueAttempted = true;
                    if (!_enqueue(downloadItem!))
                    {
                        if (reservation) _wishlist.ReleaseScoredReservation(item.Id, downloadItem!.Id, releaseName);
                        _wishlist.ForgetReleaseEvaluation(item.Id, releaseName);
                        Log.Information("Wishlist match skipped (duplicate): {Release}", releaseName);
                        continue;
                    }
                    if (tracked) _wishlist.ConfirmScoredGrab(item.Id, downloadItem!.Id, releaseName);
                    else _wishlist.RecordGrab(item.Id, releaseName);
                    admitted = true;
                    if (fromIrc)
                        Log.Information("Wishlist grab from IRC: [{Category}] {Release} -> {Title} (server: {Server})",
                            category, releaseName, item.Title, _serverName);
                    MatchFound?.Invoke(item, category!, releaseName);
                }
                catch
                {
                    // Once enqueue was attempted its exception may come from an
                    // observer after durable admission. Keep that claim until the
                    // download lifecycle or authoritative restart reconciliation.
                    if (reservation && !enqueueAttempted)
                        _wishlist.ReleaseScoredReservation(item.Id, downloadItem!.Id, releaseName);
                    _wishlist.ForgetReleaseEvaluation(item.Id, releaseName);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in wishlist matcher for [{Category}] {Release}", category, releaseName);
        }
        return admitted;
    }

    private static string ClassifyMissReason(string releaseName, WishlistItem item)
    {
        var parsed = SceneNameParser.Parse(releaseName);

        if (item.Type == MediaType.Movie)
        {
            // MatchesMovie: season/episode check → title → year → quality
            if (parsed.Season != null || parsed.Episode != null) return "not-a-movie";
            if (item.Year.HasValue && parsed.Year.HasValue && parsed.Year != item.Year) return "year-mismatch";
            if (item.Quality != QualityProfile.Any && parsed.Quality != QualityProfile.Any && parsed.Quality != item.Quality)
                return "quality-tag-mismatch";
            // Title is checked after season but before year in MatchesMovie; if we get here it's a title miss
            return "title-fuzzy-below-threshold";
        }

        if (item.Type == MediaType.TvShow)
        {
            // MatchesTvEpisode: season check → title → quality
            if (parsed.Season == null) return "not-a-tv-episode";
            if (item.Quality != QualityProfile.Any && parsed.Quality != QualityProfile.Any && parsed.Quality != item.Quality)
                return "quality-tag-mismatch";
            return "title-fuzzy-below-threshold";
        }

        return "other";
    }

    private string BuildLocalPath(WishlistItem item, string category, string releaseName)
        => DownloadPathResolver.ForWishlist(_config, item, category, releaseName);
}
