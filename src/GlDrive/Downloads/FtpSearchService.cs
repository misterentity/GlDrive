using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class FtpSearchService
{
    private readonly FtpConnectionPool _pool;
    private readonly SearchConfig _searchConfig;
    private const int MaxResults = 200;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "..", ".banner", ".message"
    };

    public FtpSearchService(FtpConnectionPool pool, SearchConfig searchConfig)
    {
        _pool = pool;
        _searchConfig = searchConfig;
    }

    public async Task<List<SearchResult>> Search(string keyword, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();

        try
        {
            // Search each configured path (bounded by pool size naturally)
            var tasks = _searchConfig.SearchPaths.Select(path =>
                SearchPath(path.TrimEnd('/'), keyword, ct));
            var pathResults = await Task.WhenAll(tasks);

            foreach (var batch in pathResults)
                results.AddRange(batch);

            // Cap total results
            if (results.Count > MaxResults)
                results.RemoveRange(MaxResults, results.Count - MaxResults);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "FTP search failed for: {Keyword}", keyword);
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchPath(string rootPath, string keyword, CancellationToken ct)
    {
        var results = new List<SearchResult>();

        try
        {
            // Borrow one connection for the entire search path to minimize pool contention
            await using var conn = await _pool.Borrow(ct);

            await SearchRecursive(conn.Client, rootPath, keyword, 0, _searchConfig.MaxDepth, results, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to search path {Path}, skipping", rootPath);
        }

        return results;
    }

    private async Task SearchRecursive(
        AsyncFtpClient client, string path, string keyword,
        int depth, int maxDepth, List<SearchResult> results, CancellationToken ct)
    {
        if (results.Count >= MaxResults) return;

        var items = await ListDirect(client, path, ct);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            if (results.Count >= MaxResults) return;
            if (item.Type != FtpObjectType.Directory) continue;
            if (SkipDirs.Contains(item.Name)) continue;

            if (item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                // Derive category from the first directory component after the search root
                var category = ExtractCategory(path, item.FullName);

                results.Add(new SearchResult
                {
                    ReleaseName = item.Name,
                    Category = category,
                    RemotePath = item.FullName,
                    Size = item.Size,
                    Modified = item.Modified
                });
            }

            if (depth < maxDepth)
            {
                await SearchRecursive(client, item.FullName, keyword, depth + 1, maxDepth, results, ct);
            }
        }
    }

    private static string ExtractCategory(string searchRoot, string fullPath)
    {
        // Given searchRoot="/site" and fullPath="/site/TV/Some.Show", extract "TV"
        var relative = fullPath;
        if (relative.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
            relative = relative[searchRoot.Length..];
        relative = relative.TrimStart('/');

        var slash = relative.IndexOf('/');
        return slash > 0 ? relative[..slash] : "";
    }

    private async Task<FtpListItem[]> ListDirect(AsyncFtpClient client, string remotePath, CancellationToken ct)
    {
        if (_pool.UseCpsv)
            return await CpsvDataHelper.ListDirectory(client, remotePath, _pool.ControlHost, ct);

        return await client.GetListing(remotePath, FtpListOption.AllFiles, ct);
    }

    public async Task<List<FtpListItem>> GetReleaseFiles(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        var items = await ListDirect(conn.Client, remotePath, ct);
        return items.Where(i => i.Type == FtpObjectType.File).ToList();
    }
}
