using System.Collections.Concurrent;
using FluentFTP;
using Serilog;

namespace GlDrive.Filesystem;

public class DirectoryCache
{
    private readonly ConcurrentDictionary<string, CachedDirectory> _cache = new();
    private readonly int _ttlSeconds;
    private readonly int _maxEntries;

    public DirectoryCache(int ttlSeconds = 30, int maxEntries = 500)
    {
        _ttlSeconds = ttlSeconds;
        _maxEntries = maxEntries;
    }

    public bool TryGet(string remotePath, out FtpListItem[] items)
    {
        var key = NormalizePath(remotePath);
        if (_cache.TryGetValue(key, out var cached) && !cached.IsExpired(_ttlSeconds))
        {
            items = cached.Items;
            return true;
        }
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

    private void EvictOldest()
    {
        var oldest = _cache
            .OrderBy(kv => kv.Value.CachedAt)
            .Take(_cache.Count / 4)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
            _cache.TryRemove(key, out _);
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
