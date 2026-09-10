using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class DownloadShutdownReliabilityTests
{
    [Fact]
    public async Task Manager_restarts_only_after_workers_stop_and_cannot_restart_after_disposal()
    {
        var id = "lifecycle-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ConfigManager.AppDataPath, $"downloads-{id}.json");
        var store = new DownloadStore(id);
        // Empty queue: exercise the real lifecycle without contacting any server.
        var manager = new DownloadManager(store, null!, null!, new DownloadConfig());
        try
        {
            manager.Start();
            Assert.Throws<InvalidOperationException>(() => manager.Start());
            await manager.StopAsync();
            manager.Start();
            await manager.StopAsync();
            manager.Dispose();
            manager.Dispose();
            Assert.Throws<ObjectDisposedException>(() => manager.Start());
        }
        finally
        {
            manager.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading, true, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Extracting, true, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Cancelled, true, DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Downloading, false, DownloadStatus.Cancelled)]
    public void Shutdown_preserves_resumable_work_but_user_cancellation_stays_cancelled(
        DownloadStatus current, bool stopping, DownloadStatus expected)
        => Assert.Equal(expected, DownloadManager.StatusAfterCancellation(current, stopping));

    [Fact]
    public void Flush_persists_pending_state_and_late_updates_do_not_touch_disposed_timer()
    {
        var id = "shutdown-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ConfigManager.AppDataPath, $"downloads-{id}.json");
        var store = new DownloadStore(id);
        try
        {
            var item = new DownloadItem();
            store.Add(item);
            item.DownloadedBytes = 123;
            store.Update(item);
            store.Flush();
            store.Flush();
            store.Update(item);
            store.Save();

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(123, doc.RootElement[0].GetProperty("downloadedBytes").GetInt64());
        }
        finally
        {
            store.Flush();
            File.Delete(path);
        }
    }
}
