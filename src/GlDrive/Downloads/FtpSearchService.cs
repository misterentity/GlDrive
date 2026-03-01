using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class FtpSearchService
{
    private readonly FtpConnectionPool _pool;
    private readonly NotificationConfig _notifConfig;

    public FtpSearchService(FtpConnectionPool pool, NotificationConfig notifConfig)
    {
        _pool = pool;
        _notifConfig = notifConfig;
    }

    public async Task<List<SearchResult>> Search(string keyword, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var watchPath = _notifConfig.WatchPath.TrimEnd('/');

        try
        {
            // List categories (single LIST call)
            var categories = await ListDirect(watchPath, ct);
            var categoryDirs = categories
                .Where(i => i.Type == FtpObjectType.Directory)
                .Select(i => i.Name)
                .ToList();

            if (categoryDirs.Count == 0) return results;

            // Search all categories in parallel (bounded by pool size)
            var tasks = categoryDirs.Select(cat => SearchCategory(watchPath, cat, keyword, ct));
            var categoryResults = await Task.WhenAll(tasks);

            foreach (var batch in categoryResults)
                results.AddRange(batch);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "FTP search failed for: {Keyword}", keyword);
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchCategory(string watchPath, string category, string keyword, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var catPath = $"{watchPath}/{category}";

        try
        {
            var releases = await ListDirect(catPath, ct);

            foreach (var release in releases)
            {
                if (release.Type != FtpObjectType.Directory) continue;
                if (release.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new SearchResult
                    {
                        ReleaseName = release.Name,
                        Category = category,
                        RemotePath = release.FullName,
                        Size = release.Size,
                        Modified = release.Modified
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to search {Category}, skipping", catPath);
        }

        return results;
    }

    /// <summary>
    /// Borrows a connection directly from the pool for a fresh LIST (no filesystem cache).
    /// The pool's bounded size naturally throttles concurrency.
    /// </summary>
    private async Task<FtpListItem[]> ListDirect(string remotePath, CancellationToken ct)
    {
        await using var conn = await _pool.Borrow(ct);

        if (_pool.UseCpsv)
            return await CpsvDataHelper.ListDirectory(conn.Client, remotePath, _pool.ControlHost, ct);

        return await conn.Client.GetListing(remotePath, FtpListOption.AllFiles, ct);
    }

    public async Task<List<FtpListItem>> GetReleaseFiles(string remotePath, CancellationToken ct = default)
    {
        var items = await ListDirect(remotePath, ct);
        return items.Where(i => i.Type == FtpObjectType.File).ToList();
    }
}
