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

    internal void UpdateServerName(string name) => _serverName = name;

    public event Action<WishlistItem, string, string>? MatchFound; // item, category, release

    public WishlistMatcher(WishlistStore wishlist, DownloadManager downloadManager, FtpOperations ftp,
        DownloadConfig config, string serverId, string serverName)
        : this(wishlist, downloadManager.Enqueue, config, serverId, serverName,
            attempt => App.TelemetryRecorder?.Record(TelemetryStream.WishlistAttempts, attempt))
    {
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

    public void OnNewRelease(string category, string releaseName, string remotePath, bool fromIrc = false)
    {
        try
        {
            foreach (var item in _wishlist.Items)
            {
                // Shared across matchers/sites, and claimed before evaluation. No store
                // lock is held while calling DownloadManager or raising UI events.
                if (!_wishlist.TryBeginReleaseEvaluation(item.Id, releaseName)) continue;
                try
                {
                    bool matches = item.Type switch
                    {
                        MediaType.Movie => SceneNameParser.MatchesMovie(releaseName, item.Title, item.Year, item.Quality),
                        MediaType.TvShow => SceneNameParser.MatchesTvEpisode(releaseName, item.Title, item.Quality),
                        _ => false
                    };

                    string? missReason = matches ? null : ClassifyMissReason(releaseName, item);
                    try
                    {
                        _recordAttempt?.Invoke(new WishlistAttemptEvent
                            {
                                WishlistItemId = item.Id,
                                Release = releaseName,
                                Score = matches ? 1.0 : 0.0,
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

                    var localPath = BuildLocalPath(item, category!, releaseName);
                    var downloadItem = new DownloadItem
                    {
                        RemotePath = remotePath,
                        ReleaseName = releaseName,
                        LocalPath = localPath,
                        WishlistItemId = item.Id,
                        Category = category!,
                        ServerId = _serverId ?? "",
                        ServerName = _serverName
                    };
                    if (!_enqueue(downloadItem))
                    {
                        _wishlist.ForgetReleaseEvaluation(item.Id, releaseName);
                        Log.Information("Wishlist match skipped (duplicate): {Release}", releaseName);
                        continue;
                    }
                    _wishlist.RecordGrab(item.Id, releaseName);
                    if (fromIrc)
                        Log.Information("Wishlist grab from IRC: [{Category}] {Release} -> {Title} (server: {Server})",
                            category, releaseName, item.Title, _serverName);
                    MatchFound?.Invoke(item, category!, releaseName);
                }
                catch
                {
                    _wishlist.ForgetReleaseEvaluation(item.Id, releaseName);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in wishlist matcher for [{Category}] {Release}", category, releaseName);
        }
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
