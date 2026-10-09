using System.Collections.Concurrent;
using FluentFTP;
using Serilog;

namespace GlDrive.Filesystem;

public class DirectoryCache
{
    private readonly ConcurrentDictionary<string, CachedDirectory> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _refreshing = new();
    // Serializes the capacity check + evict + insert in Set() so concurrent writers
    // can't each pass the `Count < max` gate and overgrow the cache past _maxEntries.
    private readonly object _setLock = new();
    private readonly int _ttlSeconds;
    private readonly int _maxEntries;

    // A LIST that started before a DELE/RNTO/STOR can finish after that change invalidated the
    // directory; storing it would resurrect the old state for a whole TTL (release smoke
    // "FAIL: WinFsp delete", 2026-10-07/09). Each invalidation stamps an epoch; Set refuses a
    // listing fetched before the latest one. Guarded by _setLock.
    internal const int MaxTrackedInvalidations = 4096;
    private readonly Dictionary<string, long> _invalidatedAt = new();
    private long _epoch;
    private long _allInvalidatedAt;

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
        _ttlSeconds = Math.Max(0, ttlSeconds);
        _maxEntries = Math.Max(1, maxEntries);
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

            // Without a refresher, a stale hit would prevent the caller from ever
            // fetching a fresh listing. Capture the delegate before scheduling it.
            var refresh = BackgroundRefresh;
            if (refresh == null)
            {
                Interlocked.Increment(ref _misses);
                items = [];
                return false;
            }

            // Stale-while-revalidate: return expired data immediately, trigger async refresh
            if (_refreshing.TryAdd(key, 0))
            {
                Interlocked.Increment(ref _staleHits);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await refresh(remotePath);
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

    /// <summary>Call before fetching a listing; pass the result to <see cref="Set(string, FtpListItem[], long)"/>.</summary>
    public long BeginFetch() => Interlocked.Read(ref _epoch);

    internal int TrackedInvalidations { get { lock (_setLock) return _invalidatedAt.Count; } }

    /// <summary>Stores a listing unless the directory was invalidated after <paramref name="fetchEpoch"/>.</summary>
    public bool Set(string remotePath, FtpListItem[] items, long fetchEpoch)
    {
        var key = NormalizePath(remotePath);
        lock (_setLock)
        {
            if (_allInvalidatedAt > fetchEpoch
                || (_invalidatedAt.TryGetValue(key, out var at) && at > fetchEpoch))
            {
                Log.Debug("Cache: discarded listing of {Path} fetched before its invalidation", key);
                return false;
            }
            Set(remotePath, items);
            return true;
        }
    }

    public void Set(string remotePath, FtpListItem[] items)
    {
        var key = NormalizePath(remotePath);

        // Under _setLock so the count-check, eviction, and insert are one atomic step —
        // ConcurrentDictionary.Count is consistent inside the lock and only one writer
        // evicts/inserts at a time, so the cache never overgrows past _maxEntries.
        // (Reads via TryGet/FindItem stay lock-free; this only orders writes.)
        lock (_setLock)
        {
            // Evict if over capacity (leave room for the entry we're about to add)
            if (!_cache.ContainsKey(key) && _cache.Count >= _maxEntries)
                EvictOldest();

            _cache[key] = new CachedDirectory(items);
        }
    }

    public void Invalidate(string remotePath)
    {
        var key = NormalizePath(remotePath);
        InvalidateKey(key);
        Log.Debug("Cache invalidated: {Path}", key);
    }

    public void InvalidateParent(string remotePath)
    {
        var normalized = NormalizePath(remotePath);
        var idx = normalized.LastIndexOf('/');
        InvalidateKey(idx <= 0 ? "/" : normalized[..idx]);
    }

    public void Clear()
    {
        lock (_setLock)
        {
            _allInvalidatedAt = Interlocked.Increment(ref _epoch);
            _invalidatedAt.Clear();
            _cache.Clear();
        }
        Log.Information("Directory cache cleared");
    }

    private void InvalidateKey(string key)
    {
        lock (_setLock)
        {
            var at = Interlocked.Increment(ref _epoch);
            if (_invalidatedAt.Count >= MaxTrackedInvalidations && !_invalidatedAt.ContainsKey(key))
            {
                // Collapse to one global stamp: conservative, it only skips caching for fetches in flight.
                _allInvalidatedAt = at;
                _invalidatedAt.Clear();
            }
            _invalidatedAt[key] = at;
            _cache.TryRemove(key, out _);
        }
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
        // Evict the oldest quarter. Always remove at least one entry, including
        // when the configured capacity is smaller than four.
        var entries = _cache.ToArray();
        Array.Sort(entries, (a, b) => a.Value.CachedAt.CompareTo(b.Value.CachedAt));
        var toRemove = Math.Min(entries.Length, Math.Max(1, entries.Length / 4));
        for (int i = 0; i < toRemove; i++)
        {
            if (_cache.TryRemove(entries[i].Key, out _))
                Interlocked.Increment(ref _evictions);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private class CachedDirectory
    {
        public FtpListItem[] Items { get; }
        public DateTime CachedAt { get; }

        // Thread-safe lazy init: `??=` was a non-atomic race — two concurrent FindByName
        // calls could each build (and discard) a dictionary. ExecutionAndPublication
        // guarantees the factory runs once. Keep StringComparer.Ordinal: filesystem
        // lookups are case-sensitive (FTP paths) and must not collapse case variants.
        private readonly Lazy<Dictionary<string, FtpListItem>> _nameLookup;

        public CachedDirectory(FtpListItem[] items)
        {
            Items = items;
            CachedAt = DateTime.UtcNow;
            _nameLookup = new Lazy<Dictionary<string, FtpListItem>>(
                () =>
                {
                    var lookup = new Dictionary<string, FtpListItem>(StringComparer.Ordinal);
                    foreach (var item in Items)
                        lookup.TryAdd(item.Name, item);
                    return lookup;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public bool IsExpired(int ttlSeconds) =>
            ttlSeconds <= 0 || (DateTime.UtcNow - CachedAt).TotalSeconds >= ttlSeconds;

        public FtpListItem? FindByName(string name)
        {
            return _nameLookup.Value.GetValueOrDefault(name);
        }
    }
}
