using System.Text.RegularExpressions;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class FtpSearchService : IDisposable
{
    private readonly FtpConnectionPool _pool;
    private readonly SearchConfig _searchConfig;
    private const int MaxResults = 200;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "..", ".banner", ".message"
    };

    // SITE SEARCH support: null = not probed yet, true/false = probed
    private bool? _siteSearchSupported;

    // Cached index
    private List<IndexEntry> _index = [];
    private readonly Lock _indexLock = new();
    private CancellationTokenSource? _indexerCts;
    private Task? _indexerTask;

    public FtpSearchService(FtpConnectionPool pool, SearchConfig searchConfig)
    {
        _pool = pool;
        _searchConfig = searchConfig;
    }

    private static string Normalize(string s) => s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

    /// <summary>
    /// Main search entry point. Uses the configured method (or Auto to try all in order).
    /// </summary>
    public async Task<List<SearchResult>> Search(string keyword, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var method = _searchConfig.Method;

        if (method == SearchMethod.SiteSearch)
            return await RunSiteSearch(keyword, progress, ct);

        if (method == SearchMethod.CachedIndex)
            return RunIndexSearch(keyword, progress);

        if (method == SearchMethod.LiveCrawl)
            return await RunLiveCrawl(keyword, progress, ct);

        // Auto mode: try SITE SEARCH → Cached Index → Live Crawl
        return await RunAutoSearch(keyword, progress, ct);
    }

    private async Task<List<SearchResult>> RunAutoSearch(string keyword, IProgress<string>? progress, CancellationToken ct)
    {
        // 1. Try SITE SEARCH if not known to be unsupported
        if (_siteSearchSupported != false)
        {
            var results = await TrySiteSearch(keyword, progress, ct);
            if (results != null)
                return results;
        }

        // 2. Try cached index if populated
        List<IndexEntry> snapshot;
        lock (_indexLock)
            snapshot = _index;

        if (snapshot.Count > 0)
        {
            progress?.Report("Searching cached index...");
            var results = FilterIndex(snapshot, keyword);
            if (results.Count > 0)
            {
                progress?.Report($"{results.Count} result(s) from cached index");
                return results;
            }
        }

        // 3. Fall back to live crawl
        return await RunLiveCrawl(keyword, progress, ct);
    }

    #region SITE SEARCH

    private static readonly Regex SiteSearchLineRegex = new(
        @"^(?<path>/\S+)\s+\((?<files>\d+)F/(?<size>[\d.]+)(?<unit>[KMG]?)/(?<age>\S+)\)",
        RegexOptions.Compiled);

    private async Task<List<SearchResult>> RunSiteSearch(string keyword, IProgress<string>? progress, CancellationToken ct)
    {
        var results = await TrySiteSearch(keyword, progress, ct);
        if (results != null) return results;

        progress?.Report("SITE SEARCH not supported, falling back to live crawl...");
        return await RunLiveCrawl(keyword, progress, ct);
    }

    /// <summary>
    /// Tries SITE SEARCH. Returns null if the server doesn't support it.
    /// </summary>
    private async Task<List<SearchResult>?> TrySiteSearch(string keyword, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report("Trying SITE SEARCH...");
            await using var conn = await _pool.Borrow(ct);
            var reply = await conn.Client.Execute($"SITE SEARCH {keyword}", ct);

            if (!reply.Success)
            {
                Log.Debug("SITE SEARCH returned {Code}: {Message}", reply.Code, reply.Message);
                _siteSearchSupported = false;
                return null;
            }

            _siteSearchSupported = true;
            var results = ParseSiteSearchResponse(reply.Message);
            progress?.Report($"{results.Count} result(s) from SITE SEARCH");
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "SITE SEARCH probe failed, marking unsupported");
            _siteSearchSupported = false;
            return null;
        }
    }

    internal static List<SearchResult> ParseSiteSearchResponse(string response)
    {
        var results = new List<SearchResult>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Strip FTP continuation prefix (e.g. "200-")
            var text = line.Trim();
            if (text.Length > 4 && text[3] == '-' && char.IsDigit(text[0]))
                text = text[4..].Trim();

            var match = SiteSearchLineRegex.Match(text);
            if (!match.Success) continue;

            var path = match.Groups["path"].Value;
            var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var category = ExtractCategoryFromFullPath(path);

            var sizeVal = double.TryParse(match.Groups["size"].Value, out var sv) ? sv : 0;
            var unit = match.Groups["unit"].Value;
            long sizeBytes = unit switch
            {
                "K" => (long)(sizeVal * 1024),
                "M" => (long)(sizeVal * 1024 * 1024),
                "G" => (long)(sizeVal * 1024 * 1024 * 1024),
                _ => (long)sizeVal
            };

            results.Add(new SearchResult
            {
                ReleaseName = name,
                Category = category,
                RemotePath = path,
                Size = sizeBytes
            });

            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    private static string ExtractCategoryFromFullPath(string fullPath)
    {
        // "/site/TV/Some.Show" → "TV"
        var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[^2] : parts.Length >= 2 ? parts[0] : "";
    }

    #endregion

    #region Cached Index

    private List<SearchResult> RunIndexSearch(string keyword, IProgress<string>? progress)
    {
        List<IndexEntry> snapshot;
        lock (_indexLock)
            snapshot = _index;

        if (snapshot.Count == 0)
        {
            progress?.Report("Index is empty — building in background...");
            return [];
        }

        progress?.Report("Searching cached index...");
        var results = FilterIndex(snapshot, keyword);
        progress?.Report($"{results.Count} result(s) from cached index");
        return results;
    }

    private static List<SearchResult> FilterIndex(List<IndexEntry> index, string keyword)
    {
        var normalized = Normalize(keyword);
        var results = new List<SearchResult>();

        foreach (var entry in index)
        {
            if (entry.NormalizedName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SearchResult
                {
                    ReleaseName = entry.Name,
                    Category = entry.Category,
                    RemotePath = entry.Path,
                    Size = entry.Size,
                    Modified = entry.Modified
                });
                if (results.Count >= MaxResults) break;
            }
        }

        return results;
    }

    /// <summary>
    /// Fully rebuilds the in-memory index by crawling all search paths.
    /// </summary>
    public async Task RefreshIndex(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var entries = new List<IndexEntry>();
        progress?.Report("Building search index...");

        foreach (var searchPath in _searchConfig.SearchPaths)
        {
            var root = searchPath.TrimEnd('/');
            try
            {
                await using var conn = await _pool.Borrow(ct);
                await CrawlForIndex(conn.Client, root, root, 0, _searchConfig.MaxDepth, entries, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Index crawl failed for {Path}", root);
            }
        }

        lock (_indexLock)
            _index = entries;

        progress?.Report($"Index built: {entries.Count} entries");
        Log.Information("Search index built with {Count} entries", entries.Count);
    }

    private async Task CrawlForIndex(
        AsyncFtpClient client, string path, string searchRoot,
        int depth, int maxDepth, List<IndexEntry> entries, CancellationToken ct)
    {
        var items = await ListDirect(client, path, ct);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type != FtpObjectType.Directory) continue;
            if (SkipDirs.Contains(item.Name)) continue;

            var category = ExtractCategory(searchRoot, item.FullName);

            entries.Add(new IndexEntry
            {
                Name = item.Name,
                NormalizedName = Normalize(item.Name),
                Path = item.FullName,
                Category = category,
                Size = item.Size,
                Modified = item.Modified
            });

            if (depth < maxDepth)
                await CrawlForIndex(client, item.FullName, searchRoot, depth + 1, maxDepth, entries, ct);
        }
    }

    #endregion

    #region Indexer Lifecycle

    public void StartIndexer()
    {
        if (_searchConfig.Method == SearchMethod.LiveCrawl && _searchConfig.Method != SearchMethod.Auto)
            return; // No indexer needed for pure live crawl (unless Auto which may use it)

        _indexerCts = new CancellationTokenSource();
        _indexerTask = IndexerLoop(_indexerCts.Token);
    }

    public void StopIndexer()
    {
        _indexerCts?.Cancel();
    }

    public async Task StopIndexerAsync(TimeSpan timeout)
    {
        _indexerCts?.Cancel();
        if (_indexerTask != null)
        {
            try { await _indexerTask.WaitAsync(timeout); }
            catch { }
        }
    }

    private async Task IndexerLoop(CancellationToken ct)
    {
        // Initial build
        try
        {
            await RefreshIndex(progress: null, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Initial search index build failed");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _searchConfig.IndexCacheMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                await RefreshIndex(progress: null, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Search index refresh failed");
            }
        }
    }

    #endregion

    #region Live Crawl

    private async Task<List<SearchResult>> RunLiveCrawl(string keyword, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var normalizedKeyword = Normalize(keyword);

        progress?.Report("Live searching...");

        try
        {
            var tasks = _searchConfig.SearchPaths.Select(path =>
                SearchPath(path.TrimEnd('/'), normalizedKeyword, progress, ct));
            var pathResults = await Task.WhenAll(tasks);

            foreach (var batch in pathResults)
                results.AddRange(batch);

            if (results.Count > MaxResults)
                results.RemoveRange(MaxResults, results.Count - MaxResults);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "FTP search failed for: {Keyword}", keyword);
        }

        progress?.Report($"{results.Count} result(s) from live crawl");
        return results;
    }

    private async Task<List<SearchResult>> SearchPath(string rootPath, string keyword, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<SearchResult>();

        try
        {
            await using var conn = await _pool.Borrow(ct);
            await SearchRecursive(conn.Client, rootPath, keyword, 0, _searchConfig.MaxDepth, results, progress, ct);
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
        int depth, int maxDepth, List<SearchResult> results,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (results.Count >= MaxResults) return;

        progress?.Report($"Scanning {path}...");
        var items = await ListDirect(client, path, ct);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= MaxResults) return;
            if (item.Type != FtpObjectType.Directory) continue;
            if (SkipDirs.Contains(item.Name)) continue;

            if (Normalize(item.Name).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
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
                await SearchRecursive(client, item.FullName, keyword, depth + 1, maxDepth, results, progress, ct);
        }
    }

    #endregion

    #region Helpers

    private static string ExtractCategory(string searchRoot, string fullPath)
    {
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

    #endregion

    public async Task<List<FtpListItem>> GetReleaseFiles(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        var items = await ListDirect(conn.Client, remotePath, ct);
        return items.Where(i => i.Type == FtpObjectType.File).ToList();
    }

    public void Dispose()
    {
        StopIndexer();
        _indexerCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private class IndexEntry
    {
        public string Name { get; set; } = "";
        public string NormalizedName { get; set; } = "";
        public string Path { get; set; } = "";
        public string Category { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }
}
