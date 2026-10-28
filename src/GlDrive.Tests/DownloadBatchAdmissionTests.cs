using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadBatchAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-download-batch-" + Guid.NewGuid().ToString("N"));
    public DownloadBatchAdmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private DownloadItem Item(string id) => new() { Id = id, RemotePath = "/video/" + id, ReleaseName = id, LocalPath = Path.Combine(_root, id) };

    [Fact]
    public void Batch_admission_deduplicates_and_saves_once_outside_worker_and_queue_locks()
    {
        var writes = 0; var heldLock = false; string? saved = null;
        DownloadManager? manager = null;
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), json =>
        {
            writes++; saved = json;
            heldLock |= Monitor.IsEntered(Field<object>(manager!, "_activeCts")) || Monitor.IsEntered(Field<object>(manager!, "_queueLock"));
        });
        using (manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig()))
        {
            var existing = Item("existing"); store.Add(existing); writes = 0;
            var directory = Item("directory"); Directory.CreateDirectory(directory.LocalPath);
            var first = Item("first"); var duplicate = Item("copy"); duplicate.RemotePath = first.RemotePath;
            Assert.Equal(2, manager.EnqueueMany([first, duplicate, Item("second"), Item("existing"), directory]));
            Assert.Equal(1, writes); Assert.False(heldLock);
            Assert.Equal(3, JsonDocument.Parse(saved!).RootElement.GetArrayLength());
            Assert.Equal(["first", "second"], Field<List<DownloadItem>>(manager, "_pendingQueue").Select(item => item.Id));
            Assert.Equal(0, manager.EnqueueMany([first])); Assert.Equal(1, writes);
        }
    }

    [Fact]
    public async Task Concurrent_batches_admit_a_shared_remote_path_only_once()
    {
        var writes = 0;
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ => Interlocked.Increment(ref writes));
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => manager.EnqueueMany([Item("shared")]))));
        Assert.Equal(1, results.Sum()); Assert.Equal(1, writes); Assert.Single(store.Items);
    }

    [Fact]
    public void Disposed_manager_rejects_admission_before_changing_the_closed_store()
    {
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ => { });
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        manager.Dispose();
        Assert.Equal(0, manager.EnqueueMany([Item("late")]));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Disposal_during_save_does_not_throw_after_admission()
    {
        DownloadManager? manager = null;
        var disposing = false;
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ =>
        {
            if (disposing) return;
            disposing = true;
            manager!.Dispose();
        });
        using (manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig()))
        {
            Assert.Equal(1, manager.EnqueueMany([Item("admitted")]));
            Assert.Equal("admitted", Assert.Single(store.Items).Id);
            Assert.Equal(0, manager.EnqueueMany([Item("late")]));
        }
    }

    [Fact]
    public void Failed_batch_save_keeps_admitted_items_in_memory()
    {
        var writes = 0;
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ => { writes++; throw new IOException("Fixture failure"); });
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        Assert.Equal(2, manager.EnqueueMany([Item("first"), Item("second")]));
        Assert.Equal(1, writes); Assert.Equal(2, store.Items.Count);
        Assert.Equal(2, Field<List<DownloadItem>>(manager, "_pendingQueue").Count);
    }

    [Fact]
    public void Cancel_selected_saves_once_preserves_unselected_terminal_rows_and_payloads()
    {
        var writes = 0;
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ => writes++);
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        var queued = Item("queued"); var running = Item("running"); running.Status = DownloadStatus.Downloading;
        var complete = Item("completed"); complete.Status = DownloadStatus.Completed;
        var keep = Item("keep");
        foreach (var item in new[] { queued, running, complete, keep }) store.Add(item);
        var payload = Path.Combine(_root, "payload.bin"); File.WriteAllText(payload, "retained"); running.LocalPath = payload;
        using var cts = new CancellationTokenSource(); Field<Dictionary<string, CancellationTokenSource>>(manager, "_activeCts")[running.Id] = cts;
        writes = 0;
        Assert.Equal(2, manager.CancelMany([queued.Id, running.Id, running.Id, complete.Id, "absent"]));
        Assert.Equal(1, writes); Assert.True(cts.IsCancellationRequested);
        Assert.Equal(DownloadStatus.Queued, keep.Status); Assert.Equal(DownloadStatus.Completed, complete.Status);
        Assert.Equal(4, store.Items.Count); Assert.Equal("retained", File.ReadAllText(payload));
        Field<Dictionary<string, CancellationTokenSource>>(manager, "_activeCts").Clear();
    }

    [Theory]
    [InlineData("source-incomplete")]
    [InlineData("network-allowance")]
    public async Task Retry_reenqueue_replaces_expired_wait_even_behind_another_queued_item(string oldReason)
    {
        var store = new DownloadStore(new FileInfo(Path.Combine(_root, "downloads.json")), _ => { });
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        var first = Item("ahead"); manager.Enqueue(first);
        var retry = Item("retry"); retry.SourceCompletionPending = true; store.Add(retry);
        typeof(DownloadManager).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new CancellationTokenSource());
        var requeued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DownloadStatusChanged += item => { if (item.Id == retry.Id && item.RuntimeWait?.ReasonCode == "download-queue") requeued.TrySetResult(); };
        typeof(DownloadManager).GetMethod("ScheduleRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [retry, 0, oldReason, null]);
        await requeued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var state = DownloadWaiting.Describe(retry)!;
        Assert.Equal("download-queue", state.ReasonCode); Assert.Null(state.NextCheckAtUtc);
        Assert.Equal([first.Id, retry.Id], Field<List<DownloadItem>>(manager, "_pendingQueue").Select(item => item.Id));
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private sealed class UnusedSource : IWholeDownloadSource
    {
        public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Admission tests must not start transfers");
    }
}
