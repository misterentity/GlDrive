using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadQueueCancellationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completion_and_cancellation_preserve_the_first_terminal_transition(bool cancellationWins)
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-completion-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "downloads.json");
        var store = DownloadStore.ForFile(path);
        using var manager = new DownloadManager(store, null!, null!, new DownloadConfig());
        var item = new DownloadItem { Status = DownloadStatus.Downloading, LocalPath = root, RemotePath = "/empty" };
        var notifications = new List<DownloadStatus>();
        manager.DownloadStatusChanged += changed => notifications.Add(changed.Status);
        try
        {
            store.Add(item);
            if (cancellationWins)
            {
                manager.Cancel(item.Id);
                // Even a completed operation that did not observe the token must
                // not overwrite the cancellation accepted immediately before it.
                Assert.Throws<OperationCanceledException>(() => manager.CompleteItem(item, default));
                Assert.Null(item.CompletedAt);
            }
            else
            {
                manager.CompleteItem(item, default);
                manager.Cancel(item.Id);
                Assert.NotNull(item.CompletedAt);
            }
            var expected = cancellationWins ? DownloadStatus.Cancelled : DownloadStatus.Completed;
            Assert.Equal(expected, item.Status);
            Assert.Equal([expected], notifications);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal((int)expected, saved.RootElement[0].GetProperty("status").GetInt32());
        }
        finally
        {
            manager.Dispose();
            store.Flush();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cancelled_token_prevents_completion_without_a_status_notification()
    {
        var store = DownloadStore.ForFile(Path.Combine(Path.GetTempPath(), "gldrive-unused-" + Guid.NewGuid().ToString("N") + ".json"));
        // No writes are necessary: cancellation must be observed before Update.
        // Close the isolated store so teardown cannot persist any test file.
        store.Flush();
        using var manager = new DownloadManager(store, null!, null!, new DownloadConfig());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var item = new DownloadItem { Status = DownloadStatus.Downloading };
        var notifications = 0;
        manager.DownloadStatusChanged += _ => notifications++;
        Assert.Throws<OperationCanceledException>(() => manager.CompleteItem(item, cts.Token));
        Assert.Equal(0, notifications);
        Assert.Null(item.CompletedAt);
        Assert.Equal(DownloadStatus.Downloading, item.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_waiting_for_slot_is_honoured_and_slot_is_released(bool remove)
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-queue-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = DownloadStore.ForFile(Path.Combine(root, "downloads.json"));
        await using var pool = new FtpConnectionPool(null!);
        // Only IsPoolInCooldown is needed. Avoid initializing global network-usage services.
        var downloader = (StreamingDownloader)RuntimeHelpers.GetUninitializedObject(typeof(StreamingDownloader));
        typeof(StreamingDownloader).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(downloader, pool);
        var manager = new DownloadManager(store, null!, downloader,
            new DownloadConfig { MaxConcurrentDownloads = 1, MaxRetries = 0 });
        var slot = (SemaphoreSlim)typeof(DownloadManager)
            .GetField("_concurrency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var item = new DownloadItem { LocalPath = Path.Combine(root, "release"), RemotePath = "/release" };
        var absentDrive = "ZYXWVUTQ".Select(c => c + @":\").First(r => !Directory.Exists(r));
        var sentinel = new DownloadItem { LocalPath = Path.Combine(absentDrive, "release"), RemotePath = "/sentinel" };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCancelledItem = false;
        manager.DownloadStatusChanged += changed =>
        {
            if (changed.Id == item.Id && changed.Status == DownloadStatus.Downloading) startedCancelledItem = true;
            if (changed.Id == sentinel.Id && changed.ErrorMessage != null) settled.TrySetResult();
        };

        try
        {
            store.Add(item);
            store.Add(sentinel);
            slot.Wait();
            manager.Start(); // deterministically pauses the processor at the occupied slot
            manager.Cancel(item.Id);
            if (remove) store.Remove(item.Id);
            slot.Release();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.StopAsync();

            Assert.False(startedCancelledItem);
            Assert.Equal(DownloadStatus.Cancelled, item.Status);
            Assert.Equal(1, slot.CurrentCount);
            Assert.Equal(DownloadStatus.Queued, sentinel.Status);
        }
        finally
        {
            await manager.StopAsync();
            manager.Dispose();
            store.Flush();
            Directory.Delete(root, recursive: true);
        }
    }
}
