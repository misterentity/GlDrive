using System.Collections.Concurrent;
using FluentFTP;
using Serilog;

namespace GlDrive.Filesystem;

public class DirectoryCache
{
    private readonly ConcurrentDictionary<string, CachedDirectory> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _refreshing = new();
    private readonly int _ttlSeconds;
    private readonly int _maxEntries;

    // Metrics
    private long _hits;
    private long _misses;
    private long _staleHits;
    private long _evictions;

    /// <summary>
    /// Called to trigger a background refresh for a stale entry.
    /// The delegate receives the remote path and should call Set() with the result.
    /// </summary>
    public Func<string, Task>? BackgroundRefresh { get; set; }

    public DirectoryCache(int ttlSeconds = 30, int maxEntries = 500)
    {
        _ttlSeconds = ttlSeconds;
        _maxEntries = maxEntries;
    }

    public bool TryGet(string remotePath, out FtpListItem[] items)
    {
        var key = NormalizePath(remotePath);
        if (_cache.TryGetValue(key, out var cached))
        {
            if (!cached.IsExpired(_ttlSeconds))
            {
                Interlocked.Increment(ref _hits);
                items = cached.Items;
                return true;
            }

            // Stale-while-revalidate: return expired data immediately, trigger async refresh
            if (BackgroundRefresh != null && _refreshing.TryAdd(key, 0))
            {
                Interlocked.Increment(ref _staleHits);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await BackgroundRefresh(remotePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Background cache refresh failed for {Path}", remotePath);
                    }
                    finally
                    {
                        _refreshing.TryRemove(key, out _);
                    }
                });
                items = cached.Items;
                return true;
            }

            // Already refreshing — serve stale
            if (_refreshing.ContainsKey(key))
            {
                Interlocked.Increment(ref _staleHits);
                items = cached.Items;
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        items = [];
        return false;
    }

    public void Set(string remotePath, FtpListItem[] items)
    {
        var key = NormalizePath(remotePath);

        // Evict if over capacity
        if (_cache.Count >= _maxEntries)
            EvictOldest();

        _cache[key] = new CachedDirectory(items);
    }

    public void Invalidate(string remotePath)
    {
        var key = NormalizePath(remotePath);
        _cache.TryRemove(key, out _);
        Log.Debug("Cache invalidated: {Path}", key);
    }

    public void InvalidateParent(string remotePath)
    {
        var parent = GetParentPath(remotePath);
        Invalidate(parent);
    }

    public void Clear()
    {
        _cache.Clear();
        Log.Information("Directory cache cleared");
    }

    public FtpListItem? FindItem(string remotePath)
    {
        var parent = GetParentPath(remotePath);
        var name = GetFileName(remotePath);

        if (!TryGet(parent, out var items))
            return null;

        return items.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns current cache metrics: (hits, misses, staleHits, evictions).
    /// </summary>
    public (long Hits, long Misses, long StaleHits, long Evictions) GetMetrics() =>
        (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses),
         Interlocked.Read(ref _staleHits), Interlocked.Read(ref _evictions));

    public void LogMetrics()
    {
        var (hits, misses, staleHits, evictions) = GetMetrics();
        var total = hits + misses + staleHits;
        var hitRate = total > 0 ? (double)(hits + staleHits) / total * 100 : 0;
        Log.Debug("Cache metrics: {Hits} hits, {Misses} misses, {StaleHits} stale-served, " +
                  "{Evictions} evictions, {HitRate:F1}% hit rate, {Count} entries",
            hits, misses, staleHits, evictions, hitRate, _cache.Count);
    }

    private void EvictOldest()
    {
        var oldest = _cache
            .OrderBy(kv => kv.Value.CachedAt)
            .Take(_cache.Count / 4)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
        {
            _cache.TryRemove(key, out _);
            Interlocked.Increment(ref _evictions);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        return path.TrimEnd('/');
    }

    private static string GetParentPath(string path)
    {
        path = NormalizePath(path);
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private static string GetFileName(string path)
    {
        path = NormalizePath(path);
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    private class CachedDirectory
    {
        public FtpListItem[] Items { get; }
        public DateTime CachedAt { get; }

        public CachedDirectory(FtpListItem[] items)
        {
            Items = items;
            CachedAt = DateTime.UtcNow;
        }

        public bool IsExpired(int ttlSeconds) =>
            (DateTime.UtcNow - CachedAt).TotalSeconds > ttlSeconds;
    }
}
