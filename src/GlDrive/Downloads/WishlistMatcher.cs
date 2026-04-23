using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class WishlistMatcher
{
    private readonly WishlistStore _wishlist;
    private readonly DownloadManager _downloadManager;
    private readonly FtpOperations _ftp;
    private readonly DownloadConfig _config;
    private readonly string _serverId;
    private readonly string _serverName;

    private readonly object _matchLock = new();

    public event Action<WishlistItem, string, string>? MatchFound; // item, category, release

    public WishlistMatcher(WishlistStore wishlist, DownloadManager downloadManager, FtpOperations ftp,
        DownloadConfig config, string serverId, string serverName)
    {
        _wishlist = wishlist;
        _downloadManager = downloadManager;
        _ftp = ftp;
        _config = config;
        _serverId = serverId;
        _serverName = serverName;
    }

    public void OnNewRelease(string category, string releaseName, string remotePath)
    {
        try
        {
            // Phase 1: collect matches under lock (no calls to DownloadManager)
            var matched = new List<(WishlistItem item, DownloadItem download)>();
            lock (_matchLock)
            {
                foreach (var item in _wishlist.Items)
                {
                    if (item.Status == WishlistStatus.Paused || item.Status == WishlistStatus.Completed)
                        continue;

                    if (item.GrabbedReleases.Contains(releaseName, StringComparer.OrdinalIgnoreCase))
                        continue;

                    bool matches = item.Type switch
                    {
                        MediaType.Movie => SceneNameParser.MatchesMovie(releaseName, item.Title, item.Year, item.Quality),
                        MediaType.TvShow => SceneNameParser.MatchesTvEpisode(releaseName, item.Title, item.Quality),
                        _ => false
                    };

                    string? missReason = matches ? null : ClassifyMissReason(releaseName, item);
                    try
                    {
                        App.TelemetryRecorder?.Record(TelemetryStream.WishlistAttempts,
                            new WishlistAttemptEvent
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

                    if (!matches) continue;

                    Log.Information("Wishlist match: [{Category}] {Release} -> {Title} (server: {Server})",
                        category, releaseName, item.Title, _serverName);

                    var localPath = BuildLocalPath(item, category, releaseName);
                    matched.Add((item, new DownloadItem
                    {
                        RemotePath = remotePath,
                        ReleaseName = releaseName,
                        LocalPath = localPath,
                        WishlistItemId = item.Id,
                        Category = category,
                        ServerId = _serverId,
                        ServerName = _serverName
                    }));
                }
            }

            // Phase 2: enqueue outside lock (no nested lock risk)
            foreach (var (item, downloadItem) in matched)
            {
                if (!_downloadManager.Enqueue(downloadItem))
                {
                    Log.Information("Wishlist match skipped (duplicate): {Release}", releaseName);
                    continue;
                }

                item.GrabbedReleases.Add(releaseName);
                _wishlist.Update(item);
                MatchFound?.Invoke(item, category, releaseName);
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
    {
        var basePath = _config.GetPathForCategory(category);
        var safeRelease = PathSanitizer.Sanitize(releaseName);
        var safeTitle = PathSanitizer.Sanitize(item.Title);

        if (item.Type == MediaType.Movie)
        {
            var yearStr = item.Year.HasValue ? $" ({item.Year})" : "";
            return Path.Combine(basePath, "Movies", $"{safeTitle}{yearStr}", safeRelease);
        }

        // TV: extract season from release name
        var parsed = SceneNameParser.Parse(releaseName);
        var seasonFolder = parsed.Season.HasValue ? $"Season {parsed.Season:D2}" : "Season 01";
        return Path.Combine(basePath, "TV", safeTitle, seasonFolder, safeRelease);
    }
}
