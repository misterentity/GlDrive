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

            // TryAdd failed → already refreshing, serve stale
            Interlocked.Increment(ref _staleHits);
            items = cached.Items;
            return true;
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
        var normalized = NormalizePath(remotePath);
        var idx = normalized.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : normalized[..idx];
        _cache.TryRemove(parent, out _);
    }

    public void Clear()
    {
        _cache.Clear();
        Log.Information("Directory cache cleared");
    }

    public FtpListItem? FindItem(string remotePath)
    {
        var normalized = NormalizePath(remotePath);
        var idx = normalized.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : normalized[..idx];
        var name = idx < 0 ? normalized : normalized[(idx + 1)..];

        if (!_cache.TryGetValue(parent, out var cached) || cached.IsExpired(_ttlSeconds))
            return null;

        Interlocked.Increment(ref _hits);
        return cached.FindByName(name);
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
        // Find the oldest quarter by scanning once (O(n) instead of O(n log n) sort)
        var entries = _cache.ToArray();
        Array.Sort(entries, (a, b) => a.Value.CachedAt.CompareTo(b.Value.CachedAt));
        var toRemove = entries.Length / 4;
        for (int i = 0; i < toRemove; i++)
        {
            _cache.TryRemove(entries[i].Key, out _);
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

    private class CachedDirectory
    {
        public FtpListItem[] Items { get; }
        public DateTime CachedAt { get; }
        private Dictionary<string, FtpListItem>? _nameLookup;

        public CachedDirectory(FtpListItem[] items)
        {
            Items = items;
            CachedAt = DateTime.UtcNow;
        }

        public bool IsExpired(int ttlSeconds) =>
            (DateTime.UtcNow - CachedAt).TotalSeconds > ttlSeconds;

        public FtpListItem? FindByName(string name)
        {
            // Lazy-init dictionary on first lookup
            _nameLookup ??= Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
            return _nameLookup.GetValueOrDefault(name);
        }
    }
}
