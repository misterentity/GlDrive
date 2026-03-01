using System.IO;
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

    public async void OnNewRelease(string category, string releaseName)
    {
        try
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

                if (!matches) continue;

                Log.Information("Wishlist match: [{Category}] {Release} -> {Title} (server: {Server})",
                    category, releaseName, item.Title, _serverName);

                var remotePath = $"/recent/{category}/{releaseName}";
                var localPath = BuildLocalPath(item, releaseName);

                var downloadItem = new DownloadItem
                {
                    RemotePath = remotePath,
                    ReleaseName = releaseName,
                    LocalPath = localPath,
                    WishlistItemId = item.Id,
                    Category = category,
                    ServerId = _serverId,
                    ServerName = _serverName
                };

                _downloadManager.Enqueue(downloadItem);

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

    private string BuildLocalPath(WishlistItem item, string releaseName)
    {
        var basePath = _config.LocalPath;

        if (item.Type == MediaType.Movie)
        {
            var yearStr = item.Year.HasValue ? $" ({item.Year})" : "";
            return Path.Combine(basePath, "Movies", $"{item.Title}{yearStr}", releaseName);
        }

        // TV: extract season from release name
        var parsed = SceneNameParser.Parse(releaseName);
        var seasonFolder = parsed.Season.HasValue ? $"Season {parsed.Season:D2}" : "Season 01";
        return Path.Combine(basePath, "TV", item.Title, seasonFolder, releaseName);
    }
}
