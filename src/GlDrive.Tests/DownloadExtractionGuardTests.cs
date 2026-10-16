using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadExtractionGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-extract-guard-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DownloadStatus.Queued, true)]
    [InlineData(DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Extracting, true)]
    [InlineData(DownloadStatus.Completed, false)]
    [InlineData(DownloadStatus.Failed, false)]
    [InlineData(DownloadStatus.Cancelled, false)]
    public void Managed_download_holds_watcher_until_ownership_ends(DownloadStatus status, bool expected)
    {
        var item = new DownloadItem { Status = status, LocalPath = Path.Combine(_root, "release"), SourceCompletionPending = true };
        Assert.Equal(expected, DownloadExtractionGuard.Blocks(item, Path.Combine(item.LocalPath, "CD1", "disc.rar")));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_bounded_by_destination_path()
    {
        var item = new DownloadItem { LocalPath = Path.Combine(_root, "Release") };
        Assert.True(DownloadExtractionGuard.Blocks(item, Path.Combine(_root, "release", "disc.rar").ToUpperInvariant()));
        Assert.False(DownloadExtractionGuard.Blocks(item, Path.Combine(_root, "Release.Other", "disc.rar")));
        Assert.False(DownloadExtractionGuard.Blocks(item, Path.Combine(_root, "Release", "..", "standalone", "disc.rar")));
    }

    [Fact]
    public void Single_file_destinations_are_protected_too()
    {
        var item = new DownloadItem { LocalPath = Path.Combine(_root, "disc.rar") };
        Assert.True(DownloadExtractionGuard.Blocks(item, item.LocalPath));
        Assert.False(DownloadExtractionGuard.Blocks(item, Path.Combine(_root, "other.rar")));
    }

    [Fact]
    public void Persisted_waiting_queue_protects_archives_before_any_server_mounts()
    {
        var item = Item();
        WriteQueue(item);
        var guard = new DownloadExtractionGuard(_root, () => []);
        Assert.Equal(item.LocalPath, guard.FindOwner(ArchivePath)?.LocalPath);
        Assert.Null(guard.FindOwner(Path.Combine(_root, "standalone", "disc.rar")));
    }

    [Fact]
    public void Live_completion_overrides_persisted_wait_immediately_without_a_disk_flush()
    {
        var item = Item();
        var path = WriteQueue(item);
        var guard = new DownloadExtractionGuard(_root, () => [(path, new[] { item })]);
        Assert.Same(item, guard.FindOwner(ArchivePath));
        item.Status = DownloadStatus.Completed;
        Assert.Null(guard.FindOwner(ArchivePath));
        item.Status = DownloadStatus.Extracting;
        Assert.Same(item, guard.FindOwner(ArchivePath));
        item.Status = DownloadStatus.Cancelled;
        Assert.Null(guard.FindOwner(ArchivePath));
    }

    [Fact]
    public void Repeated_watch_events_use_cached_unmounted_queue_until_refresh_interval()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var item = Item();
        var path = WriteQueue(item);
        var guard = new DownloadExtractionGuard(_root, () => [], () => now);
        Assert.NotNull(guard.FindOwner(ArchivePath));
        item.Status = DownloadStatus.Cancelled;
        WriteQueue(item);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(guard.FindOwner(ArchivePath));
        now = now.AddSeconds(31);
        Assert.Null(guard.FindOwner(ArchivePath));
    }

    [Fact]
    public void Registry_uses_current_store_path_and_newest_replacement_then_drops_closed_stores()
    {
        var path = Path.Combine(_root, "downloads-test.json");
        var old = Store(path);
        var current = Store(path);
        try
        {
            var oldItem = Item();
            old.Add(oldItem);
            var newItem = Item();
            newItem.Status = DownloadStatus.Completed;
            current.Add(newItem);
            var guard = new DownloadExtractionGuard(_root);
            Assert.Null(guard.FindOwner(ArchivePath));
            old.Flush();
            newItem.Status = DownloadStatus.Downloading;
            current.Update(newItem);
            Assert.Same(newItem, guard.FindOwner(ArchivePath));
            current.Flush();
            Assert.DoesNotContain(DownloadStore.OpenSnapshots(), snapshot => snapshot.Path == path);
        }
        finally { old.Flush(); current.Flush(); }
    }

    private string ArchivePath => Path.Combine(_root, "release", "disc.rar");
    private DownloadItem Item() => new() { LocalPath = Path.Combine(_root, "release"), SourceCompletionPending = true };
    private string WriteQueue(DownloadItem item)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "downloads-test.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { item }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return path;
    }
    private static DownloadStore Store(string path)
    {
        var store = DownloadStore.ForFile(path);
        return store;
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
