using System.IO;
using System.IO.Hashing;
using System.Reflection;
using System.Text;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadSourceCompletionTests
{
    [Theory]
    [InlineData("-MISSING-disc.rar", true)]
    [InlineData("disc.r00-missing", true)]
    [InlineData("disc.r00.missing", true)]
    [InlineData("[ Incomplete ]", true)]
    [InlineData("[ In-complete ]", true)]
    [InlineData("[ In_complete ]", true)]
    [InlineData("[##] - 20% Complete - [site]", true)]
    [InlineData("[##] - 20% - Complete - [site]", true)]
    [InlineData("[####] - 100% Complete - [site]", false)]
    public async Task Markers_are_recorded_then_excluded_from_every_directory(string marker, bool incomplete)
    {
        var root = Path.Combine(Path.GetTempPath(), "source-tree", "release");
        var plan = await DownloadTree.BuildAsync("/release", root,
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false),
            (path, _) => Task.FromResult(path == "/release"
                ? new[] { Entry("CD1", directory: true), Entry(".message", size: 20), Entry("film.imdb.nfo", size: 20) }
                : new[] { Entry(marker, directory: true), Entry("[Group] Episode.mkv", size: 20) }), default);
        Assert.Equal(incomplete, plan.IncompleteReasons.Count > 0);
        Assert.Equal("/release/CD1/[Group] Episode.mkv", Assert.Single(plan.Files).RemotePath);
        Assert.Equal(2, plan.Directories.Count);
    }

    [Fact]
    public async Task Missing_volume_waits_then_restart_refreshes_same_size_manifest_and_preserves_good_bytes()
    {
        await using var h = new Harness();
        h.Source.Files["CD1/disc.rar"] = Bytes("good volume");
        h.Source.Files["CD1/disc.sfv"] = Sfv(("disc.rar", "good volume"), ("disc.r00", "arrived"));
        await h.RunAsync();
        AssertWaiting(h);
        Assert.Equal(Bytes("good volume"), File.ReadAllBytes(Path.Combine(h.Item.LocalPath, "CD1", "disc.rar")));
        Assert.Equal(1, h.Source.Fetches["CD1/disc.rar"]);

        // The new manifest is exactly the same size but changes the required name.
        h.Source.Files["CD1/disc.sfv"] = Sfv(("disc.rar", "good volume"), ("disc.r01", "arrived"));
        h.Source.Files["CD1/disc.r01"] = Bytes("arrived");
        await h.ReloadAsync();
        Assert.True(h.Item.SourceCompletionPending);
        Assert.StartsWith("Waiting for release completion:", h.Item.ErrorMessage);
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Null(h.Item.ErrorMessage);
        Assert.False(h.Item.SourceCompletionPending);
        Assert.Equal(1, h.Source.Fetches["CD1/disc.rar"]);
        Assert.Equal(1, h.Source.Fetches["CD1/disc.r01"]);
        Assert.Equal(2, h.Source.ManifestRefreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nfo_guard_waits_without_spending_retries_and_resumes_when_nfo_arrives(bool cancel)
    {
        await using var h = new Harness(new DownloadConfig { SkipIncompleteReleases = true });
        h.Source.Files["movie.rar"] = Bytes("movie");
        await h.RunAsync();
        AssertWaiting(h, sourceEvidence: false);
        Assert.Contains(".nfo", h.Item.ErrorMessage);
        Assert.Empty(h.Source.Fetches);
        if (cancel)
        {
            h.Manager.Cancel(h.Item.Id);
            h.Source.Files["movie.nfo"] = Bytes("info");
            h.Manager.Start();
            await h.Manager.StopAsync();
            Assert.Equal(DownloadStatus.Cancelled, h.Item.Status);
            Assert.Null(h.Item.ErrorMessage);
            Assert.Empty(h.Source.Fetches);
        }
        else
        {
            h.Source.Files["movie.nfo"] = Bytes("info");
            await h.RunAsync();
            Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        }
    }

    [Fact]
    public async Task Explicit_marker_waits_before_transfer_then_requires_late_sfv_for_archive()
    {
        await using var h = new Harness();
        h.Source.Files["disc.rar"] = Bytes("archive");
        h.Source.Markers = ["disc.r00-missing"];
        await h.RunAsync();
        AssertWaiting(h);
        Assert.Empty(h.Source.Fetches);
        h.Source.Markers = [];
        await h.RunAsync();
        AssertWaiting(h);
        Assert.Contains("SFV manifest", h.Item.ErrorMessage);
        h.Source.Files["disc.sfv"] = Sfv(("disc.rar", "archive"));
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_archive_without_sfv_still_completes_with_nfo_guard(bool guard)
    {
        await using var h = new Harness(new DownloadConfig { SkipIncompleteReleases = guard });
        h.Source.Files["disc.rar"] = Bytes("archive");
        h.Source.Files["disc.nfo"] = Bytes("info");
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
    }

    [Fact]
    public async Task Auxiliary_archive_need_not_be_declared_by_primary_manifest()
    {
        await using var h = new Harness(new DownloadConfig { SkipIncompleteReleases = true });
        h.Source.Files["disc.nfo"] = Bytes("info");
        h.Source.Files["disc.rar"] = Bytes("archive");
        h.Source.Files["disc.sfv"] = Sfv(("disc.rar", "archive"));
        h.Source.Files["Sample/sample.rar"] = Bytes("sample");
        h.Source.Markers = ["[ Incomplete ]"];
        await h.RunAsync();
        AssertWaiting(h);
        h.Source.Markers = [];
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
    }

    [Fact]
    public async Task Positive_completion_marker_allows_previously_incomplete_archive_without_sfv()
    {
        await using var h = new Harness();
        h.Source.Files["disc.rar"] = Bytes("archive");
        h.Source.Markers = ["[ Incomplete ]"];
        await h.RunAsync();
        AssertWaiting(h);
        h.Source.Markers = [];
        h.Source.HasCompletionMarker = true;
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
    }

    [Theory]
    [InlineData("[####] - 100% Complete - [site]")]
    [InlineData("[ Complete ]")]
    public async Task Completion_marker_covers_only_its_archive_subtree(string marker)
    {
        var root = Path.Combine(Path.GetTempPath(), "source-complete-tree", "release");
        var secondComplete = false;
        Task<DownloadTreePlan> Plan() => DownloadTree.BuildAsync("/release", root,
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false),
            (path, _) => Task.FromResult(path == "/release"
                ? new[] { Entry("CD1", directory: true), Entry("CD2", directory: true) }
                : path.EndsWith("CD1") || secondComplete
                    ? new[] { Entry("disc.rar", size: 20), Entry(marker) }
                    : new[] { Entry("disc.rar", size: 20) }), default);
        Assert.False((await Plan()).HasCompletionMarker);
        secondComplete = true;
        Assert.True((await Plan()).HasCompletionMarker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sfv_missing_files_wait_even_when_checksum_verification_is_disabled(bool verify)
    {
        await using var h = new Harness(new DownloadConfig { VerifySfv = verify });
        h.Source.Files["disc.sfv"] = Sfv(("disc.rar", "absent"));
        await h.RunAsync();
        AssertWaiting(h);
        Assert.Contains("missing", h.Item.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_checksum_or_manifest_errors_fail_even_if_another_volume_is_missing(bool malformed)
    {
        await using var h = new Harness();
        h.Source.Files["disc.rar"] = Bytes("wrong");
        h.Source.Files["disc.sfv"] = malformed ? Bytes("not a valid SFV line")
            : Sfv(("disc.rar", "right"), ("disc.r00", "missing"));
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Contains(malformed ? "invalid SFV entry" : "CRC mismatch", h.Item.ErrorMessage);
        Assert.Equal(0, h.Item.RetryCount);
        Assert.False(h.Item.SourceCompletionPending);
    }

    [Fact]
    public async Task Source_growth_after_download_is_waiting_and_not_a_false_crc_failure()
    {
        await using var h = new Harness();
        h.Source.Files["disc.rar"] = Bytes("pre");
        h.Source.Files["disc.sfv"] = Sfv(("disc.rar", "prefix"));
        h.Source.AfterDownload = () => h.Source.Files["disc.rar"] = Bytes("prefix");
        await h.RunAsync();
        AssertWaiting(h);
        Assert.Contains("changed during download", h.Item.ErrorMessage);
        h.Source.AfterDownload = null;
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(Bytes("prefix"), File.ReadAllBytes(Path.Combine(h.Item.LocalPath, "disc.rar")));
    }

    [Fact]
    public async Task Newly_arrived_payload_and_marker_are_detected_before_completion()
    {
        await using var h = new Harness();
        h.Source.Files["one.mkv"] = Bytes("one");
        h.Source.AfterDownload = () =>
        {
            h.Source.Files["two.mkv"] = Bytes("two");
            h.Source.Markers = ["[ Incomplete ]"];
        };
        await h.RunAsync();
        AssertWaiting(h);
        h.Source.AfterDownload = null;
        h.Source.Markers = [];
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["one.mkv"]);
        Assert.Equal(1, h.Source.Fetches["two.mkv"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_accepted_during_listing_cannot_be_replaced_with_waiting(bool ioFailure)
    {
        await using var h = new Harness();
        h.Source.Markers = ["[ Incomplete ]"];
        h.Source.AfterPlan = () =>
        {
            h.Manager.Cancel(h.Item.Id);
            if (ioFailure) throw new IOException("Listing interrupted by cancellation");
        };
        await h.RunAsync();
        Assert.Equal(DownloadStatus.Cancelled, h.Item.Status);
        Assert.Null(h.Item.ErrorMessage);
        Assert.False(h.Item.SourceCompletionPending);
        Assert.Throws<OperationCanceledException>(() => h.Manager.ParkForSource(h.Item, "late result", default));
        Assert.Empty(h.Source.Fetches);
    }

    [Fact]
    public async Task Retry_task_does_not_queue_cancelled_or_removed_waiting_items()
    {
        await using var h = new Harness();
        h.Source.Markers = ["[ Incomplete ]"];
        await h.RunAsync();
        h.Manager.Cancel(h.Item.Id);
        h.Store.Remove(h.Item.Id);
        var ctsField = typeof(DownloadManager).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((CancellationTokenSource?)ctsField.GetValue(h.Manager))?.Dispose();
        var source = new CancellationTokenSource(); // Manager owns/disposes the replacement.
        ctsField.SetValue(h.Manager, source);
        typeof(DownloadManager).GetMethod("ScheduleRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(h.Manager, [h.Item, 0]);
        var tasks = (List<Task>)typeof(DownloadManager).GetField("_retryTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Manager)!;
        var retryLock = typeof(DownloadManager).GetField("_retryLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Manager)!;
        Task[] retries;
        lock (retryLock) retries = tasks.ToArray();
        await Task.WhenAll(retries);
        var pending = (List<DownloadItem>)typeof(DownloadManager).GetField("_pendingQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Manager)!;
        Assert.Empty(pending);
        Assert.Null(h.Store.GetById(h.Item.Id));
    }

    [Fact]
    public void Source_polling_interval_is_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ReleaseSourceState.RecheckDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), ReleaseSourceState.RecheckDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5), ReleaseSourceState.RecheckDelay(int.MaxValue));
    }

    private static void AssertWaiting(Harness h, bool sourceEvidence = true)
    {
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(sourceEvidence, h.Item.SourceCompletionPending);
        Assert.StartsWith("Waiting for release completion:", h.Item.ErrorMessage);
        Assert.Equal(0, h.Item.RetryCount);
        Assert.Null(h.Item.CompletedAt);
        var slots = (LiveConcurrencyGate)typeof(DownloadManager).GetField("_concurrency", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Manager)!;
        Assert.Equal(1, slots.CurrentCount);
    }

    private static FtpListItem Entry(string name, bool directory = false, long size = 0) =>
        new() { Name = name, Type = directory ? FtpObjectType.Directory : FtpObjectType.File, Size = size };
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Sfv(params (string Name, string Contents)[] entries) => Bytes(string.Join("\n",
        entries.Select(e => $"{e.Name} {Crc32.HashToUInt32(Bytes(e.Contents)):X8}")) + "\n");

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-source-completion-" + Guid.NewGuid().ToString("N"));
        private readonly DownloadConfig _config;
        public DownloadStore Store { get; private set; }
        public DownloadManager Manager { get; private set; }
        public DownloadItem Item { get; private set; }
        public FakeSource Source { get; } = new();

        public Harness(DownloadConfig? config = null)
        {
            Directory.CreateDirectory(_root);
            _config = config ?? new DownloadConfig();
            _config.MaxConcurrentDownloads = 1;
            _config.AutoExtract = false;
            _config.MaxRetries = 0;
            Store = CreateStore();
            Manager = new DownloadManager(Store, Source, _config);
            Item = new DownloadItem { RemotePath = "/release", LocalPath = Path.Combine(_root, "release") };
            Store.Add(Item);
        }

        private DownloadStore CreateStore()
        {
            var store = DownloadStore.ForFile(Path.Combine(_root, "downloads.json"));
            return store;
        }

        public async Task RunAsync()
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(DownloadItem item)
            {
                if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled ||
                    (item.Status == DownloadStatus.Queued && item.ErrorMessage?.StartsWith("Waiting for release completion:") == true))
                    settled.TrySetResult();
            }
            Manager.DownloadStatusChanged += Changed;
            try
            {
                Manager.Start();
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await Manager.StopAsync();
                Manager.DownloadStatusChanged -= Changed;
            }
        }

        public async Task ReloadAsync()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Store = CreateStore();
            Store.Load();
            Item = Assert.Single(Store.Items);
            Manager = new DownloadManager(Store, Source, _config);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSource : IDownloadSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Fetches { get; } = new(StringComparer.Ordinal);
        public string[] Markers { get; set; } = [];
        public bool HasCompletionMarker { get; set; }
        public Action? AfterDownload { get; set; }
        public Action? AfterPlan { get; set; }
        public int ManifestRefreshes { get; private set; }
        public bool IsInCooldown => false;

        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            var files = Files.Select(f => new FtpDownloadFile(remotePath + "/" + f.Key,
                Path.Combine(localPath, f.Key.Replace('/', Path.DirectorySeparatorChar)), f.Value.LongLength)).ToArray();
            var dirs = files.Select(f => Path.GetDirectoryName(f.LocalPath)!).Append(localPath).Distinct().ToArray();
            var plan = new DownloadTreePlan(true, files, dirs)
            {
                IncompleteReasons = Markers,
                HasCompletionMarker = HasCompletionMarker
            };
            AfterPlan?.Invoke();
            return Task.FromResult(plan);
        }

        public async Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(file.LocalPath) && new FileInfo(file.LocalPath).Length == file.Size) continue;
                var key = file.RemotePath["/release/".Length..];
                Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
                await File.WriteAllBytesAsync(file.LocalPath, Files[key], ct);
                Fetches[key] = Fetches.GetValueOrDefault(key) + 1;
            }
            AfterDownload?.Invoke();
        }

        public async Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct)
        {
            ManifestRefreshes++;
            Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
            await File.WriteAllBytesAsync(file.LocalPath, Files[file.RemotePath["/release/".Length..]], ct);
        }
    }
}
