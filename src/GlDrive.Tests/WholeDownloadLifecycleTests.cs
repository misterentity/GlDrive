using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class WholeDownloadLifecycleTests
{
    [Fact]
    public async Task Completion_waits_for_backend_disposal_and_releases_disk_reservation()
    {
        var disk = new DiskReservation(0, _ => 1000);
        await using var h = new Harness(disk: disk);
        var prepared = h.Source.Add(holdDisposal: true);
        var completed = h.WhenStatus(DownloadStatus.Completed);
        h.Manager.Start();
        await prepared.DisposeEntered.Task.WaitAsync(Limit);
        Assert.Equal(DownloadStatus.Downloading, h.Item.Status);
        Assert.Null(h.Item.CompletedAt);
        Assert.False(completed.IsCompleted);
        Assert.Equal(100, disk.ReservedOn(h.Item.LocalPath));
        prepared.ReleaseDisposal.TrySetResult();
        await completed.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.True(prepared.Disposed);
        Assert.Equal(100, h.Item.DownloadedBytes);
        Assert.Equal(0, disk.ReservedOn(h.Item.LocalPath));
        Assert.Equal(1, prepared.ExecuteCalls);
        Assert.Equal(1, prepared.DisposeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Immediate_resume_or_retry_waits_until_cancelled_writer_closes(bool pause)
    {
        await using var h = new Harness();
        var first = h.Source.Add(holdExecution: true, holdDisposal: true);
        var second = h.Source.Add();
        var completed = h.WhenStatus(DownloadStatus.Completed);
        h.Manager.Start();
        await first.ExecuteEntered.Task.WaitAsync(Limit);
        Assert.True(first.WriterOpen);
        if (pause) h.Manager.Pause(h.Item.Id); else h.Manager.Cancel(h.Item.Id);
        await first.DisposeEntered.Task.WaitAsync(Limit);
        if (pause) h.Manager.Resume(h.Item.Id); else h.Manager.Retry(h.Item.Id);
        Assert.Equal(pause ? DownloadStatus.Paused : DownloadStatus.Cancelled, h.Item.Status);
        Assert.Equal(1, h.Source.PrepareCalls);
        Assert.False(second.ExecuteEntered.Task.IsCompleted);
        Assert.True(first.WriterOpen);
        Assert.Throws<IOException>(() => File.Open(first.PayloadPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        first.ReleaseDisposal.TrySetResult();
        await completed.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(2, h.Source.PrepareCalls);
        Assert.True(first.Disposed);
        Assert.Equal(1, h.Source.MaxWriters);
        Assert.Equal(0, h.Source.Writers);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(0, h.Item.RetryCount);
    }

    [Fact]
    public async Task Retry_requested_from_failure_notification_runs_after_worker_cleanup()
    {
        await using var h = new Harness();
        var first = h.Source.Add();
        first.ExecuteFailure = new DownloadBlockedException("approval required", canSkipFiles: true);
        var second = h.Source.Add();
        var completed = h.WhenStatus(DownloadStatus.Completed);
        var retried = false;
        h.Manager.DownloadStatusChanged += item =>
        {
            if (item.Status != DownloadStatus.Failed || retried) return;
            retried = true;
            Assert.True(first.Disposed);
            Assert.True(item.RequiresContentApproval);
            item.SkipBlockedTorrentFiles = true;
            h.Manager.Retry(item.Id);
        };
        h.Manager.Start();
        await completed.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.True(retried);
        Assert.Equal(2, h.Source.PrepareCalls);
        Assert.Equal(1, second.ExecuteCalls);
        Assert.False(h.Item.RequiresContentApproval);
        Assert.Equal(0, h.Item.RetryCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Reenqueue_same_content_is_rejected_while_cancelled_or_failed_owner_drains(bool cancel, bool removeHistory)
    {
        var disk = cancel ? null : new DiskReservation(0, _ => 50);
        await using var h = new Harness(disk: disk);
        var first = h.Source.Add(holdExecution: cancel, holdDisposal: true);
        h.Source.Add();
        h.Manager.Start();
        if (cancel)
        {
            await first.ExecuteEntered.Task.WaitAsync(Limit);
            h.Manager.Cancel(h.Item.Id);
        }
        await first.DisposeEntered.Task.WaitAsync(Limit);
        Assert.Equal(cancel ? DownloadStatus.Cancelled : DownloadStatus.Failed, h.Item.Status);
        if (removeHistory) h.Store.Remove(h.Item.Id);
        var replacement = new DownloadItem { SourceKind = DownloadSourceKind.Torrent,
            RemotePath = h.Item.RemotePath, LocalPath = h.Item.LocalPath, TorrentMagnet = h.Item.TorrentMagnet };
        Assert.False(h.Manager.Enqueue(replacement));
        Assert.Same(h.Item, h.Manager.FindPendingOrActive(h.Item.RemotePath));
        Assert.Equal(removeHistory ? 0 : 1, h.Store.Items.Count);
        Assert.Equal(1, h.Source.PrepareCalls);
        first.ReleaseDisposal.TrySetResult();
        await h.WaitIdle();
        Assert.Null(h.Manager.FindPendingOrActive(h.Item.RemotePath));
        Assert.True(h.Manager.Enqueue(replacement));
        Assert.Equal(removeHistory ? 1 : 2, h.Store.Items.Count);
    }

    [Fact]
    public async Task Shutdown_keeps_removed_items_owned_until_their_payload_writer_closes()
    {
        await using var h = new Harness();
        var first = h.Source.Add(holdExecution: true, holdDisposal: true);
        h.Manager.Start();
        await first.ExecuteEntered.Task.WaitAsync(Limit);
        h.Manager.Stop();
        await first.DisposeEntered.Task.WaitAsync(Limit);
        h.Store.Remove(h.Item.Id);
        var replacement = new DownloadItem { SourceKind = DownloadSourceKind.Torrent,
            RemotePath = h.Item.RemotePath, LocalPath = h.Item.LocalPath };
        Assert.False(h.Manager.Enqueue(replacement));
        Assert.True(first.WriterOpen);
        first.ReleaseDisposal.TrySetResult();
        await h.WaitIdle();
        Assert.True(h.Manager.Enqueue(replacement));
    }

    [Fact]
    public async Task Paused_queue_survives_store_reload_and_does_not_start_until_resumed()
    {
        await using var h = new Harness();
        h.Source.Add();
        h.Manager.Pause(h.Item.Id);
        using (var saved = JsonDocument.Parse(File.ReadAllText(h.StorePath)))
            Assert.Equal((int)DownloadStatus.Paused, saved.RootElement[0].GetProperty("status").GetInt32());
        await h.Reload();
        Assert.Equal(DownloadStatus.Paused, h.Item.Status);
        h.Manager.Start();
        Assert.Equal(0, h.Source.PrepareCalls);
        var completed = h.WhenStatus(DownloadStatus.Completed);
        h.Manager.Resume(h.Item.Id);
        await completed.WaitAsync(Limit);
        Assert.Equal(1, h.Source.PrepareCalls);
    }

    [Fact]
    public async Task Active_pause_remains_paused_across_shutdown_and_restart()
    {
        await using var h = new Harness();
        var prepared = h.Source.Add(holdExecution: true);
        h.Manager.Start();
        await prepared.ExecuteEntered.Task.WaitAsync(Limit);
        h.Manager.Pause(h.Item.Id);
        await h.Manager.StopAsync();
        await h.Reload();
        Assert.Equal(DownloadStatus.Paused, h.Item.Status);
        Assert.Null(h.Item.CompletedAt);
        h.Manager.Start();
        Assert.Equal(1, h.Source.PrepareCalls);
    }

    [Fact]
    public async Task Pause_accepted_during_disposal_is_not_replaced_by_extraction_or_cancellation()
    {
        await using var h = new Harness(autoExtract: true);
        Directory.CreateDirectory(h.Item.LocalPath);
        var prepared = h.Source.Add(holdDisposal: true);
        var extracting = false;
        h.Manager.DownloadStatusChanged += item => extracting |= item.Status == DownloadStatus.Extracting;
        h.Manager.Start();
        await prepared.DisposeEntered.Task.WaitAsync(Limit);
        h.Manager.Pause(h.Item.Id);
        prepared.ReleaseDisposal.TrySetResult();
        await h.WaitIdle();
        Assert.Equal(DownloadStatus.Paused, h.Item.Status);
        Assert.False(extracting);
        Assert.Null(h.Item.CompletedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_or_pause_during_metadata_never_starts_payload_or_spends_retry(bool pause)
    {
        await using var h = new Harness(maxRetries: 3);
        var metadataEntered = Signal();
        var metadataCancelled = Signal();
        h.Source.PrepareOverride = async (_, ct) =>
        {
            metadataEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { metadataCancelled.TrySetResult(); }
            throw new InvalidOperationException("unreachable");
        };
        h.Manager.Start();
        await metadataEntered.Task.WaitAsync(Limit);
        if (pause) h.Manager.Pause(h.Item.Id); else h.Manager.Cancel(h.Item.Id);
        await metadataCancelled.Task.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(pause ? DownloadStatus.Paused : DownloadStatus.Cancelled, h.Item.Status);
        Assert.Empty(h.Source.Prepared);
        Assert.Equal(0, h.Item.RetryCount);
        Assert.Null(h.Item.CompletedAt);
    }

    [Fact]
    public async Task Insufficient_disk_disposes_preparation_without_starting_payload()
    {
        var disk = new DiskReservation(0, _ => 50);
        await using var h = new Harness(disk: disk);
        var prepared = h.Source.Add();
        var failed = h.WhenStatus(DownloadStatus.Failed);
        h.Manager.Start();
        await failed.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(0, prepared.ExecuteCalls);
        Assert.Equal(1, prepared.DisposeCalls);
        Assert.Equal(0, disk.ReservedOn(h.Item.LocalPath));
        Assert.Contains("Insufficient disk space", h.Item.ErrorMessage);
    }

    [Fact]
    public async Task Persisted_progress_does_not_waive_disk_reservation_before_resume_verification()
    {
        var disk = new DiskReservation(0, _ => 50);
        await using var h = new Harness(disk: disk);
        h.Item.DownloadedBytes = h.Item.TotalBytes = 100;
        var prepared = h.Source.Add();
        var settled = h.WhenStatus(DownloadStatus.Failed, DownloadStatus.Completed);
        h.Manager.Start();
        await settled.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(0, prepared.ExecuteCalls);
        Assert.False(Directory.Exists(h.Item.LocalPath));
        Assert.Equal(0, disk.ReservedOn(h.Item.LocalPath));
    }

    [Fact]
    public async Task Disk_is_reserved_before_execute_and_held_through_cancel_cleanup()
    {
        var disk = new DiskReservation(0, _ => 1000);
        await using var h = new Harness(disk: disk);
        var prepared = h.Source.Add(holdExecution: true, holdDisposal: true);
        prepared.OnExecute = () => Assert.Equal(100, disk.ReservedOn(h.Item.LocalPath));
        h.Manager.Start();
        await prepared.ExecuteEntered.Task.WaitAsync(Limit);
        h.Manager.Cancel(h.Item.Id);
        await prepared.DisposeEntered.Task.WaitAsync(Limit);
        Assert.Equal(100, disk.ReservedOn(h.Item.LocalPath));
        prepared.ReleaseDisposal.TrySetResult();
        await h.WaitIdle();
        Assert.Equal(0, disk.ReservedOn(h.Item.LocalPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Permanent_metadata_refusal_never_auto_retries(bool canSkip)
    {
        await using var h = new Harness(maxRetries: 5);
        h.Source.PrepareOverride = (_, _) => throw new DownloadBlockedException("blocked content", canSkip);
        var failed = h.WhenStatus(DownloadStatus.Failed);
        h.Manager.Start();
        await failed.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(1, h.Source.PrepareCalls);
        Assert.Equal(0, h.Item.RetryCount);
        Assert.Equal(canSkip, h.Item.RequiresContentApproval);
        Assert.Equal("blocked content", h.Item.ErrorMessage);
        var retryTasks = (List<Task>)Field("_retryTasks").GetValue(h.Manager)!;
        lock (Field("_retryLock").GetValue(h.Manager)!) Assert.Empty(retryTasks);
    }

    [Fact]
    public async Task Late_progress_cannot_overwrite_completed_totals_or_status()
    {
        await using var h = new Harness();
        var prepared = h.Source.Add();
        var completed = h.WhenStatus(DownloadStatus.Completed);
        h.Manager.Start();
        await completed.WaitAsync(Limit);
        await h.WaitIdle();
        // Drain through the same queued Progress<T> callback by invoking its protected
        // delivery method; this avoids relying on a thread-pool sleep for ordering.
        var progress = Assert.IsType<Progress<DownloadProgress>>(prepared.Progress);
        typeof(Progress<DownloadProgress>).GetMethod("InvokeHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(progress, [new DownloadProgress(1, 999, 500, StatusDetail: "late metadata")]);
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(100, h.Item.TotalBytes);
        Assert.Equal(100, h.Item.DownloadedBytes);
        Assert.Null(h.Item.StatusDetail);
    }

    [Fact]
    public async Task Legacy_ftp_queue_defaults_to_ftp_and_retains_source_completion_checks()
    {
        await using var h = new Harness();
        await h.Manager.StopAsync();
        h.Manager.Dispose();
        h.Store.Flush();
        var json = "[{\"id\":\"legacy\",\"remotePath\":\"/release\",\"localPath\":" +
            JsonSerializer.Serialize(h.Item.LocalPath) + ",\"status\":0}]";
        File.WriteAllText(h.StorePath, json);
        h.Store = h.CreateStore();
        h.Store.Load();
        h.Item = Assert.Single(h.Store.Items);
        Assert.Equal(DownloadSourceKind.Ftp, h.Item.SourceKind);
        Assert.False(h.Item.SkipBlockedTorrentFiles);
        Assert.Null(h.Item.TorrentMagnet);
        var ftp = new LegacySource();
        h.Manager = new DownloadManager(h.Store, ftp, new DownloadConfig { MaxConcurrentDownloads = 1, AutoExtract = false });
        var waiting = Signal();
        h.Manager.DownloadStatusChanged += item =>
        {
            if (item.Status == DownloadStatus.Queued && item.SourceCompletionPending) waiting.TrySetResult();
        };
        h.Manager.Start();
        await waiting.Task.WaitAsync(Limit);
        await h.WaitIdle();
        Assert.Equal(1, ftp.Plans);
        Assert.Equal(0, ftp.Downloads);
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.StartsWith("Waiting for release completion:", h.Item.ErrorMessage);
        Assert.Equal(0, h.Item.RetryCount);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FieldInfo Field(string name) => typeof(DownloadManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-whole-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly DownloadConfig _config;
        private readonly DiskReservation? _disk;
        public string StorePath => Path.Combine(_root, "queue.json");
        public DownloadStore Store { get; set; }
        public DownloadManager Manager { get; set; }
        public DownloadItem Item { get; set; }
        public FakeSource Source { get; } = new();

        public Harness(DiskReservation? disk = null, int maxRetries = 0, bool autoExtract = false)
        {
            Directory.CreateDirectory(_root);
            _disk = disk;
            _config = new DownloadConfig { MaxConcurrentDownloads = 2, AutoExtract = autoExtract, MaxRetries = maxRetries, RetryDelaySeconds = 0 };
            Store = CreateStore();
            Manager = new DownloadManager(Store, Source, _config, disk);
            Item = new DownloadItem { SourceKind = DownloadSourceKind.Torrent, RemotePath = "fixture-hash",
                TorrentMagnet = "fixture-magnet", LocalPath = Path.Combine(_root, "payload"), ReleaseName = "fixture" };
            Store.Add(Item);
        }

        public DownloadStore CreateStore()
        {
            var store = DownloadStore.ForFile(StorePath);
            return store;
        }

        public Task WhenStatus(params DownloadStatus[] statuses)
        {
            var done = Signal();
            Manager.DownloadStatusChanged += item => { if (statuses.Contains(item.Status)) done.TrySetResult(); };
            return done.Task;
        }

        public async Task WaitIdle()
        {
            using var deadline = new CancellationTokenSource(Limit);
            var active = (Dictionary<string, CancellationTokenSource>)Field("_activeCts").GetValue(Manager)!;
            while (true)
            {
                lock (active) if (active.Count == 0) break;
                await Task.Delay(5, deadline.Token);
            }
            // Join the processor too, including any restart continuation it owns.
            await Manager.StopAsync();
        }

        public async Task Reload()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Store = CreateStore();
            Store.Load();
            Item = Assert.Single(Store.Items);
            Manager = new DownloadManager(Store, Source, _config, _disk);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var prepared in Source.Prepared) prepared.ReleaseDisposal.TrySetResult();
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeSource : IWholeDownloadSource
    {
        public List<Prepared> Prepared { get; } = [];
        public Func<DownloadItem, CancellationToken, Task<IPreparedDownload>>? PrepareOverride { get; set; }
        public int PrepareCalls;
        public int Writers;
        public int MaxWriters;

        public Prepared Add(bool holdExecution = false, bool holdDisposal = false)
        {
            var prepared = new Prepared(this, holdExecution, holdDisposal);
            Prepared.Add(prepared);
            return prepared;
        }

        public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref PrepareCalls);
            if (PrepareOverride != null) return PrepareOverride(item, ct);
            var prepared = Prepared[call - 1];
            prepared.PayloadPath = Path.Combine(item.LocalPath, "payload.bin");
            return Task.FromResult<IPreparedDownload>(prepared);
        }
    }

    private sealed class Prepared(FakeSource owner, bool holdExecution, bool holdDisposal) : IPreparedDownload
    {
        public long TotalBytes => 100;
        public IReadOnlyList<string> Directories => [];
        public TaskCompletionSource ExecuteEntered { get; } = Signal();
        public TaskCompletionSource DisposeEntered { get; } = Signal();
        public TaskCompletionSource ReleaseDisposal { get; } = Signal();
        public IProgress<DownloadProgress>? Progress { get; private set; }
        public Exception? ExecuteFailure { get; set; }
        public Action? OnExecute { get; set; }
        public string? PayloadPath { get; set; }
        private FileStream? _writer;
        public int ExecuteCalls;
        public int DisposeCalls;
        public bool WriterOpen;
        public bool Disposed;

        public async Task ExecuteAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref ExecuteCalls);
            Progress = progress;
            OnExecute?.Invoke();
            Directory.CreateDirectory(Path.GetDirectoryName(PayloadPath!)!);
            _writer = File.Open(PayloadPath!, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            WriterOpen = true;
            var writers = Interlocked.Increment(ref owner.Writers);
            owner.MaxWriters = Math.Max(owner.MaxWriters, writers);
            ExecuteEntered.TrySetResult();
            if (ExecuteFailure != null) throw ExecuteFailure;
            if (holdExecution) await Task.Delay(Timeout.Infinite, ct);
            CloseWriter();
        }

        private void CloseWriter()
        {
            if (!WriterOpen) return;
            _writer!.Dispose();
            _writer = null;
            WriterOpen = false;
            Interlocked.Decrement(ref owner.Writers);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            DisposeEntered.TrySetResult();
            if (holdDisposal) await ReleaseDisposal.Task;
            CloseWriter();
            Disposed = true;
        }
    }

    private sealed class LegacySource : IDownloadSource
    {
        public int Plans;
        public int Downloads;
        public bool IsInCooldown => false;
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            Plans++;
            return Task.FromResult(new DownloadTreePlan(true, [], [localPath]) { IncompleteReasons = ["disc.rar-missing"] });
        }
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct)
        { Downloads++; return Task.CompletedTask; }
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) => throw new InvalidOperationException("Unexpected manifest fetch");
    }
}
