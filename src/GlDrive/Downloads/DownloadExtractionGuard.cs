using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>Watch-folder extraction must not bypass a managed download's completion checks.</summary>
internal sealed class DownloadExtractionGuard
{
    private readonly string _directory;
    private readonly Func<IReadOnlyList<(string Path, IReadOnlyList<DownloadItem> Items)>> _live;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedQueue> _persisted = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextRefresh;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal DownloadExtractionGuard(string directory,
        Func<IReadOnlyList<(string Path, IReadOnlyList<DownloadItem> Items)>>? live = null,
        Func<DateTime>? now = null)
    {
        _directory = directory;
        _live = live ?? DownloadStore.OpenSnapshots;
        _now = now ?? (() => DateTime.UtcNow);
    }

    internal DownloadItem? FindOwner(string archivePath)
    {
        var live = _live();
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, items) in live)
        {
            // OpenSnapshots returns newest stores first. A replacement mount's
            // queue wins while an old store is still finishing shutdown.
            if (!livePaths.Add(path)) continue;
            var owner = items.FirstOrDefault(item => Blocks(item, archivePath));
            if (owner != null) return owner;
        }

        lock (_lock)
        {
            if (_now() >= _nextRefresh)
            {
                _nextRefresh = _now().AddSeconds(30);
                RefreshPersisted();
            }
            foreach (var (path, queue) in _persisted)
            {
                if (livePaths.Contains(path)) continue;
                var owner = queue.Items.FirstOrDefault(item => Blocks(item, archivePath));
                if (owner != null) return owner;
            }
        }
        return null;
    }

    internal static bool Blocks(DownloadItem item, string archivePath)
    {
        if (item.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused) ||
            string.IsNullOrWhiteSpace(item.LocalPath)) return false;
        try
        {
            var destination = Path.GetFullPath(item.LocalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var archive = Path.GetFullPath(archivePath);
            return archive.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                archive.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private void RefreshPersisted()
    {
        try
        {
            if (!Directory.Exists(_directory)) { _persisted.Clear(); return; }
            var paths = Directory.GetFiles(_directory, "downloads-*.json");
            var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in _persisted.Keys.Where(p => !present.Contains(p)).ToList())
                _persisted.Remove(removed);
            foreach (var path in paths)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (_persisted.TryGetValue(path, out var prior) &&
                        prior.Modified == info.LastWriteTimeUtc && prior.Length == info.Length) continue;
                    var items = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(path), JsonOptions) ?? [];
                    _persisted[path] = new CachedQueue(info.LastWriteTimeUtc, info.Length, items);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    Log.Debug(ex, "Extractor: could not refresh managed download queue {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Extractor: could not discover managed download queues");
        }
    }

    private sealed record CachedQueue(DateTime Modified, long Length, IReadOnlyList<DownloadItem> Items);
}
