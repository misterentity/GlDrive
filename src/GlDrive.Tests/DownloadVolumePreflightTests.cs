using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadVolumePreflightTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Absent_volume_parks_before_ftp_access_even_when_retry_budget_is_exhausted(int retries)
    {
        var id = "volume-preflight-" + Guid.NewGuid().ToString("N");
        var storePath = Path.Combine(ConfigManager.AppDataPath, $"downloads-{id}.json");
        var root = "ZYXWVUTQ".Select(c => c + @":\").First(r => !Directory.Exists(r));
        var store = new DownloadStore(id);
        // Any attempt to use FTP throws. An absent destination must be detected first,
        // regardless of whether the network is available (the September 17 regression).
        await using var pool = new FtpConnectionPool(null!);
        using var manager = new DownloadManager(store, null!, new StreamingDownloader(pool),
            new DownloadConfig { MaxRetries = 3 });
        var item = new DownloadItem
        {
            ReleaseName = "volume-preflight",
            RemotePath = "/release",
            LocalPath = Path.Combine(root, "Downloads", "release"),
            RetryCount = retries
        };
        try
        {
            store.Add(item);
            // A restart must recheck and park again without losing persisted queue state.
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(DownloadItem changed)
                {
                    if (changed.Id == item.Id && changed.ErrorMessage != null)
                        settled.TrySetResult();
                }
                item.ErrorMessage = null;
                manager.DownloadStatusChanged += Changed;
                manager.Start();
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await manager.StopAsync();
                manager.DownloadStatusChanged -= Changed;

                Assert.Equal(DownloadStatus.Queued, item.Status);
                Assert.Equal(retries, item.RetryCount);
                Assert.Contains($"Waiting for drive {root}", item.ErrorMessage);
                var persisted = new DownloadStore(id);
                try
                {
                    persisted.Load();
                    var saved = Assert.Single(persisted.Items);
                    Assert.Equal(DownloadStatus.Queued, saved.Status);
                    Assert.Equal(retries, saved.RetryCount);
                }
                finally { persisted.Flush(); }
            }
        }
        finally
        {
            await manager.StopAsync();
            manager.Dispose();
            File.Delete(storePath);
        }
    }
}
