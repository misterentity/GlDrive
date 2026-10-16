using System.IO;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class DownloadStoreLocationTests
{
    [Fact]
    public void Explicit_file_location_preserves_queue_and_live_registry_lifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-store-location-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "nested", "queue.json");
        var store = DownloadStore.ForFile(path);
        DownloadStore? reloaded = null;
        try
        {
            Assert.DoesNotContain(DownloadStore.OpenSnapshots(), s => s.Path == path);
            store.Load();
            var item = new DownloadItem { Status = DownloadStatus.Paused, RemotePath = "/paused" };
            store.Add(item);
            Assert.Contains(DownloadStore.OpenSnapshots(), s => s.Path == path && s.Items.Single().Id == item.Id);
            store.Flush();
            Assert.DoesNotContain(DownloadStore.OpenSnapshots(), s => s.Path == path);
            reloaded = DownloadStore.ForFile(path);
            reloaded.Load();
            var saved = Assert.Single(reloaded.Items);
            Assert.Equal(item.Id, saved.Id);
            Assert.Equal(DownloadStatus.Paused, saved.Status);
        }
        finally
        {
            store.Flush();
            reloaded?.Flush();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
