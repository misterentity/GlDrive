using System.IO;
using System.Text.Json;
using GlDrive.Downloads;

namespace GlDrive.Library;

/// <summary>
/// Fresh ownership check for a destructive local import. Uncertain queue data refuses
/// only this import; it never changes the queue or inhibits downloads and extraction.
/// </summary>
internal sealed class LibraryImportOwnership
{
    private readonly string _directory;
    private readonly Func<IReadOnlyList<(string Path, IReadOnlyList<DownloadItem> Items)>> _live;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal LibraryImportOwnership(string directory,
        Func<IReadOnlyList<(string Path, IReadOnlyList<DownloadItem> Items)>>? live = null)
    {
        _directory = directory;
        _live = live ?? DownloadStore.OpenSnapshots;
    }

    internal void EnsureUnowned(string source)
    {
        try
        {
            var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, items) in _live())
            {
                // OpenSnapshots is newest first; a replacement store supersedes its predecessor.
                if (!livePaths.Add(Path.GetFullPath(path))) continue;
                Check(items, source);
            }
            string[] files;
            try { files = Directory.GetFiles(_directory, "downloads-*.json"); }
            catch (DirectoryNotFoundException) { return; }
            foreach (var path in files)
            {
                // Even a live empty snapshot may have resulted from an unreadable initial load.
                // Parse persisted data afresh, then let a valid current snapshot supersede its rows.
                var items = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(path), Json)
                    ?? throw new IOException("A download queue has no readable ownership data.");
                Validate(items);
                if (!livePaths.Contains(Path.GetFullPath(path))) Check(items, source);
            }
        }
        catch (Exception ex) when (ex is JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException("Download ownership could not be verified; the staged file was retained.", ex);
        }
    }

    private static void Check(IReadOnlyList<DownloadItem> items, string source)
    {
        Validate(items);
        if (items.Any(item => DownloadExtractionGuard.Blocks(item, source)))
            throw new IOException("A queued, active, extracting or paused download still owns this staged file.");
    }

    private static void Validate(IReadOnlyList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            if (item == null || !Enum.IsDefined(item.Status))
                throw new IOException("A download queue contains uncertain ownership data.");
            if (item.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused)) continue;
            if (string.IsNullOrWhiteSpace(item.LocalPath) || !Path.IsPathFullyQualified(item.LocalPath))
                throw new IOException("An active download has an uncertain local path.");
            _ = Path.GetFullPath(item.LocalPath);
        }
    }
}
