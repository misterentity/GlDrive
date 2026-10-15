using System.Collections.Concurrent;
using System.IO;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public sealed class SegmentedTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-segxfer-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _source = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private TransferFile AddFile(string name, int size, bool unknownSize = false)
    {
        var data = new byte[size];
        new Random(name.GetHashCode()).NextBytes(data);
        _source["/rel/" + name] = data;
        return new TransferFile("/rel/" + name, Path.Combine(_dir, name), unknownSize ? -1 : size, $"{size}:v1");
    }

    private static SegmentedTransferOptions Opts(Action<TransferFile>? completed = null, int maxFailures = 3,
        long minSplit = 16 * 1024, long maxTake = 0, Action<string?, long>? onBytes = null) => new()
    {
        BufferSize = 8 * 1024,
        MinSplitBytes = minSplit,
        MaxTakeBytes = maxTake,
        MaxLaneFailures = maxFailures,
        LaneRetryDelay = TimeSpan.FromMilliseconds(1),
        ExtraLanePollInterval = TimeSpan.FromMilliseconds(20),
        ProgressInterval = TimeSpan.FromMilliseconds(20),
        OnFileCompleted = completed,
        OnBytes = onBytes,
    };

    private void AssertOnDisk(params TransferFile[] files)
    {
        foreach (var f in files)
        {
            Assert.Equal(_source[f.Key], File.ReadAllBytes(f.LocalPath));
            Assert.False(File.Exists(f.LocalPath + StagedFile.StagingSuffix));
            Assert.False(File.Exists(f.LocalPath + StagedFile.SidecarSuffix));
        }
    }

    [Fact]
    public async Task MultiFileMultiLane_WritesExactBytes()
    {
        var files = new[] { AddFile("a.r00", 300_000), AddFile("a.nfo", 1), AddFile("empty.txt", 0), AddFile("a.r01", 700_000), AddFile("a.sfv", 77) };
        var completed = new ConcurrentBag<string>();
        var lanes = Enumerable.Range(0, 3).Select(i => new FakeLane(_source, $"lane{i}") { ReadDelayMs = 0 }).ToArray();
        var reports = new ConcurrentQueue<TransferProgress>();

        await new SegmentedTransfer(files, Opts(f => completed.Add(f.Key), maxTake: 128 * 1024))
            .RunAsync(lanes, null, new SyncProgress<TransferProgress>(reports.Enqueue), default);

        AssertOnDisk(files);
        Assert.Equal(files.Select(f => f.Key).Order(), completed.Order());
        Assert.All(lanes, l => Assert.Equal(1, l.DisposeCount));
        Assert.All(lanes, l => Assert.Empty(l.Faults));
        var last = reports.Last();
        Assert.Equal(1_000_078, last.TotalBytes);
        Assert.Equal(1_000_078, last.DoneBytes);
        Assert.Equal(3, last.Lanes.Count);
        Assert.True(last.Lanes.Sum(l => l.Bytes) >= 1_000_078);
    }

    [Fact]
    public async Task AlreadyCompleteFile_IsNotReportedAgain()
    {
        var done = AddFile("done.r00", 1000);
        var todo = AddFile("todo.r01", 1000);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(done.LocalPath, _source[done.Key]);
        var completed = new ConcurrentBag<string>();
        var lane = new FakeLane(_source, "l");

        await new SegmentedTransfer([done, todo], Opts(f => completed.Add(f.Key))).RunAsync([lane], null, null, default);

        Assert.Equal([todo.Key], completed);
        Assert.DoesNotContain(lane.Opens, o => o.Key == done.Key);
        AssertOnDisk(done, todo);
    }

    [Fact]
    public async Task LaneThrowingMidRange_RangeFinishedByOther()
    {
        var f = AddFile("a.r00", 400_000);
        int thrown = 0;
        var bad = new FakeLane(_source, "bad")
        {
            ReadFault = pos => pos >= 50_000 && Interlocked.Exchange(ref thrown, 1) == 0 ? new IOException("reset") : null,
        };
        var good = new FakeLane(_source, "good") { ReadDelayMs = 1 };

        await new SegmentedTransfer([f], Opts()).RunAsync([bad, good], null, null, default);

        AssertOnDisk(f);
        Assert.Single(bad.Faults);
        Assert.IsType<IOException>(bad.Faults.Single());
        Assert.Equal(1, bad.DisposeCount);
    }

    [Fact]
    public async Task PrematureEof_IsLaneFailure()
    {
        var f = AddFile("a.r00", 1_000_000);
        var shortLane = new FakeLane(_source, "short") { TruncateAt = 40_000 };
        var good = new FakeLane(_source, "good") { ReadDelayMs = 3 };

        await new SegmentedTransfer([f], Opts(maxFailures: 1)).RunAsync([shortLane, good], null, null, default);

        AssertOnDisk(f);
        Assert.NotEmpty(shortLane.Faults);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Short_or_unconfirmed_single_lane_download_never_publishes_a_final_file(bool shortData)
    {
        var f = AddFile("incomplete.r00", 100_000);
        var lane = new FakeLane(_source, "standard", canStopEarly: false)
        {
            TruncateAt = shortData ? 10 : long.MaxValue,
            FinishFault = eof => !shortData && eof ? new IOException("RETR failed: 426") : null
        };
        var completed = new List<TransferFile>();

        await Assert.ThrowsAsync<IOException>(() => new SegmentedTransfer([f],
            Opts(completed.Add, maxFailures: 1)).RunAsync([lane], null, null, default));

        Assert.False(File.Exists(f.LocalPath));
        Assert.True(File.Exists(f.LocalPath + StagedFile.StagingSuffix));
        Assert.Empty(completed);
        Assert.Single(lane.Faults);
    }

    [Fact]
    public async Task BackoffException_IsNotCountedAsFailure()
    {
        var f = AddFile("a.r00", 50_000);
        int opens = 0;
        var lane = new FakeLane(_source, "http")
        {
            OpenFault = _ => Interlocked.Increment(ref opens) <= 5
                ? new LaneBackoffException("429", TimeSpan.FromMilliseconds(1)) : null,
        };

        await new SegmentedTransfer([f], Opts(maxFailures: 2)).RunAsync([lane], null, null, default);

        AssertOnDisk(f);
        Assert.Equal(5, lane.Faults.Count);
    }

    [Fact]
    public async Task AllLanesFailing_ThrowsLastLaneException()
    {
        var f = AddFile("a.r00", 50_000);
        int n = 0;
        Exception Boom(TransferFile _) => new IOException("boom " + Interlocked.Increment(ref n));
        var a = new FakeLane(_source, "a") { OpenFault = Boom };
        var b = new FakeLane(_source, "b") { OpenFault = Boom };

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            new SegmentedTransfer([f], Opts(maxFailures: 2)).RunAsync([a, b], null, null, default));

        Assert.StartsWith("boom ", ex.Message);
        Assert.Equal(4, n);
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.True(File.Exists(f.LocalPath + StagedFile.SidecarSuffix)); // kept for resume
    }

    [Fact]
    public async Task NoLanesAtAll_Throws()
    {
        var f = AddFile("a.r00", 10);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SegmentedTransfer([f], Opts()).RunAsync([], null, null, default));
    }

    [Fact]
    public async Task Yield_LaneLeavesMidFileAndOthersFinish()
    {
        var f = AddFile("a.r00", 1_000_000);
        var borrowed = new FakeLane(_source, "race login") { ReadDelayMs = 1 };
        borrowed.YieldWhen = () => Interlocked.Read(ref borrowed.BytesRead) >= 100_000;
        var main = new FakeLane(_source, "main") { ReadDelayMs = 1 };

        await new SegmentedTransfer([f], Opts()).RunAsync([borrowed, main], null, null, default);

        AssertOnDisk(f);
        Assert.Contains(false, borrowed.Finishes);
        Assert.Empty(borrowed.Faults);
        Assert.Equal(1, borrowed.DisposeCount);
        Assert.True(Interlocked.Read(ref borrowed.BytesRead) < 1_000_000);
    }

    [Fact]
    public async Task ExtraLane_JoinsViaLaneSource()
    {
        var f = AddFile("a.r00", 600_000);
        var first = new FakeLane(_source, "main") { ReadDelayMs = 2 };
        var extra = new FakeLane(_source, "race login");
        var source = new FakeLaneSource(extra);

        await new SegmentedTransfer([f], Opts()).RunAsync([first], source, null, default);

        AssertOnDisk(f);
        Assert.NotEmpty(extra.Opens);
        Assert.Equal(1, extra.DisposeCount);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task ExtraLane_ReplacesRetiredLanes()
    {
        var f = AddFile("a.r00", 50_000);
        var dead = new FakeLane(_source, "dead") { OpenFault = _ => new IOException("down") };
        var extra = new FakeLane(_source, "spare");

        await new SegmentedTransfer([f], Opts(maxFailures: 1)).RunAsync([dead], new FakeLaneSource(extra), null, default);

        AssertOnDisk(f);
        Assert.NotEmpty(extra.Opens);
    }

    [Fact]
    public async Task CannotStopEarlyLane_NeverGetsMidFileRangeAndIsNeverSplit()
    {
        var files = new[] { AddFile("a.r00", 400_000), AddFile("a.r01", 400_000), AddFile("a.r02", 400_000) };
        var pasv = new FakeLane(_source, "pasv", canStopEarly: false) { ReadDelayMs = 1 };
        var cpsv1 = new FakeLane(_source, "cpsv1");
        var cpsv2 = new FakeLane(_source, "cpsv2");

        await new SegmentedTransfer(files, Opts(maxTake: 64 * 1024)).RunAsync([pasv, cpsv1, cpsv2], null, null, default);

        AssertOnDisk(files);
        Assert.NotEmpty(pasv.Opens);
        Assert.All(pasv.Opens, o => Assert.Equal(_source[o.Key].Length, o.End));
        Assert.All(pasv.Finishes, eof => Assert.True(eof));
    }

    [Fact]
    public async Task UnknownSize_DownloadedSequentiallyToEof()
    {
        var f = AddFile("listing.bin", 123_457, unknownSize: true);
        var a = new FakeLane(_source, "a");
        var b = new FakeLane(_source, "b");
        var completed = new ConcurrentBag<string>();

        await new SegmentedTransfer([f], Opts(f => completed.Add(f.Key))).RunAsync([a, b], null, null, default);

        AssertOnDisk(f);
        Assert.Single(completed);
        Assert.Equal(1, a.Opens.Count + b.Opens.Count);
    }

    [Fact]
    public async Task Cancellation_FaultsOpenReadersAndThrows()
    {
        var f = AddFile("a.r00", 2_000_000);
        var a = new FakeLane(_source, "a") { ReadDelayMs = 5 };
        var b = new FakeLane(_source, "b") { ReadDelayMs = 5 };
        using var cts = new CancellationTokenSource();
        long seen = 0;
        var opts = Opts(onBytes: (_, n) => { if (Interlocked.Add(ref seen, n) > 100_000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedTransfer([f], opts).RunAsync([a, b], null, null, cts.Token));

        foreach (var lane in new[] { a, b })
        {
            Assert.Equal(1, lane.DisposeCount);
            Assert.All(lane.Faults, e => Assert.IsAssignableFrom<OperationCanceledException>(e));
            Assert.Equal(lane.Opens.Count, lane.Faults.Count + lane.Finishes.Count);
        }
        Assert.NotEmpty(a.Faults.Concat(b.Faults));
        Assert.True(File.Exists(f.LocalPath + StagedFile.SidecarSuffix));
        Assert.False(File.Exists(f.LocalPath));
    }

    [Fact]
    public async Task DiskWriteFailure_PropagatesWithoutLaneRetries()
    {
        var f = AddFile("a.r00", 500_000);
        var diskFull = new IOException("There is not enough space on the disk.");
        var a = new FakeLane(_source, "a") { ReadDelayMs = 1 };
        var b = new FakeLane(_source, "b") { ReadDelayMs = 1 };
        var xfer = new SegmentedTransfer([f], Opts())
        {
            StagedFileOpened = s => s.WriteFault = off => off >= 64 * 1024 ? diskFull : null,
        };

        var ex = await Assert.ThrowsAsync<IOException>(() => xfer.RunAsync([a, b], null, null, default));

        Assert.Same(diskFull, ex);
        Assert.True(a.Opens.Count <= 1 && b.Opens.Count <= 1, "a disk failure must not be retried on another range");
        foreach (var lane in new[] { a, b })
        {
            Assert.Equal(1, lane.DisposeCount);
            Assert.Equal(lane.Opens.Count, lane.Faults.Count + lane.Finishes.Count);
        }
    }

    [Fact]
    public async Task Resume_DoesNotRequestDoneRangesAgain()
    {
        var f = AddFile("a.r00", 1_000_000);
        using (var cts = new CancellationTokenSource())
        {
            long seen = 0;
            var opts = Opts(onBytes: (_, n) => { if (Interlocked.Add(ref seen, n) >= 300_000) cts.Cancel(); });
            var lanes = new[] { new FakeLane(_source, "a"), new FakeLane(_source, "b") };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new SegmentedTransfer([f], opts).RunAsync(lanes, null, null, cts.Token));
        }

        ByteRangeSet savedDone;
        await using (var s = StagedFile.Open(f)) savedDone = s.Done.Clone();
        Assert.True(savedDone.TotalLength >= 200_000, $"only {savedDone.TotalLength} bytes persisted");

        var second = new FakeLane(_source, "c");
        await new SegmentedTransfer([f], Opts()).RunAsync([second], null, null, default);

        AssertOnDisk(f);
        foreach (var o in second.Opens)
            Assert.DoesNotContain(savedDone.Ranges, r => r.Start < o.End && o.Offset < r.End);
        Assert.Equal(1_000_000 - savedDone.TotalLength, Interlocked.Read(ref second.BytesRead));
    }

    // ---- Codex review round 1 regressions ----

    [Fact]
    public async Task FailedCompletion_IsNotCommitted_AndTheFileIsRefetched()
    {
        var f = AddFile("a.r00", 200_000);
        int failures = 0;
        var lane = new FakeLane(_source, "main", canStopEarly: false)
        {
            FinishFault = eof => eof && Interlocked.Increment(ref failures) == 1 ? new IOException("RETR did not complete: 426") : null,
        };
        var completed = new ConcurrentBag<string>();

        await new SegmentedTransfer([f], Opts(f => completed.Add(f.Key))).RunAsync([lane], null, null, default);

        AssertOnDisk(f);
        Assert.Single(completed);
        Assert.Equal(2, lane.Opens.Count);                        // the unconfirmed copy was fetched again
        Assert.Contains(lane.Faults, e => e.Message.Contains("426"));
    }

    [Fact]
    public async Task CompletionPending_OnAnotherLane_BlocksCommit()
    {
        // Lane b holds the tail and its 226 fails: the file must not be published even though
        // lane a finished its half cleanly and every byte is on disk.
        var f = AddFile("a.r00", 1_000_000);
        int failures = 0;
        var a = new FakeLane(_source, "a") { ReadDelayMs = 1 };
        var b = new FakeLane(_source, "b") { ReadDelayMs = 1,
            FinishFault = eof => eof && Interlocked.Increment(ref failures) == 1 ? new IOException("extra bytes") : null };
        var completed = new ConcurrentBag<string>();

        await new SegmentedTransfer([f], Opts(f => completed.Add(f.Key))).RunAsync([a, b], null, null, default);

        AssertOnDisk(f);
        Assert.Single(completed);
    }

    [Fact]
    public async Task RefusedResume_RefetchesWholeFileSequentially()
    {
        var f = AddFile("a.r00", 600_000);
        var refuse = (TransferFile _, long offset) => offset > 0 ? new RangeNotSupportedException($"REST {offset} refused: 502") : null;
        var a = new FakeLane(_source, "a") { ReadDelayMs = 1, OpenFaultAt = refuse };
        var b = new FakeLane(_source, "b") { ReadDelayMs = 1, OpenFaultAt = refuse };

        await new SegmentedTransfer([f], Opts(maxFailures: 50)).RunAsync([a, b], null, null, default);

        AssertOnDisk(f);
    }

    [Fact]
    public async Task Yield_InterruptsAReadThatIsBlocked()
    {
        var f = AddFile("a.r00", 800_000);
        var raceWantsLogin = false;
        var borrowed = new FakeLane(_source, "race login") { BlockReadsAt = 64 * 1024, YieldWhen = () => Volatile.Read(ref raceWantsLogin) };
        // The borrowed lane takes the whole file and then hangs; main steals the tail, and its
        // first bytes are the moment "the race wants its login back".
        var main = new FakeLane(_source, "main") { ReadDelayMs = 1 };
        var opts = new SegmentedTransferOptions
        {
            BufferSize = 8 * 1024, MinSplitBytes = 16 * 1024, LaneRetryDelay = TimeSpan.FromMilliseconds(1),
            OnBytes = (net, _) => { if (net == "net-main") Volatile.Write(ref raceWantsLogin, true); },
        };

        var run = new SegmentedTransfer([f], opts).RunAsync([borrowed, main], null, null, default);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        await run;

        AssertOnDisk(f);
        Assert.NotEmpty(borrowed.Opens);
        Assert.Contains(false, borrowed.Finishes);   // clean early finish, connection reusable
        Assert.Empty(borrowed.Faults);
    }

    [Fact]
    public async Task StalledLane_IsFailed_AndItsRangeFinishedElsewhere()
    {
        var f = AddFile("a.r00", 600_000);
        var stalled = new FakeLane(_source, "stalled") { BlockReadsAt = 32 * 1024 };
        var healthy = new FakeLane(_source, "healthy") { ReadDelayMs = 1 };
        var opts = new SegmentedTransferOptions
        {
            BufferSize = 8 * 1024, MinSplitBytes = 16 * 1024, MaxLaneFailures = 1,
            LaneRetryDelay = TimeSpan.FromMilliseconds(1), StallTimeout = TimeSpan.FromMilliseconds(400),
        };

        await new SegmentedTransfer([f], opts).RunAsync([stalled, healthy], null, null, default);

        AssertOnDisk(f);
        Assert.Contains(stalled.Faults, e => e.Message.StartsWith("No data for"));
    }

    [Fact]
    public async Task CancelledOpen_FaultsTheLane()
    {
        var f = AddFile("a.r00", 100_000);
        var lane = new FakeLane(_source, "main") { BlockOpens = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedTransfer([f], Opts()).RunAsync([lane], null, null, cts.Token));

        Assert.Single(lane.Faults);
        Assert.IsAssignableFrom<OperationCanceledException>(lane.Faults.Single());
    }

    [Fact]
    public async Task Yield_InterruptsBlockedOpen_AndReleasesLaneForWaitingWork()
    {
        var file = AddFile("opening.r00", 100_000);
        var raceWantsLogin = 0;
        var borrowed = new FakeLane(_source, "race login")
        {
            BlockOpens = true,
            YieldWhen = () => Volatile.Read(ref raceWantsLogin) != 0,
        };
        var replacement = new FakeLane(_source, "replacement");
        var source = new GatedLaneSource(() => borrowed.Disposed.Task.IsCompleted, replacement);
        using var cts = new CancellationTokenSource();
        var run = new SegmentedTransfer([file], Opts()).RunAsync([borrowed], source, null, cts.Token);
        try
        {
            await borrowed.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref raceWantsLogin, 1);

            // The overall download stays alive. A failed open must quarantine/dispose
            // the borrowed lane promptly so waiting work can acquire its released slot.
            await borrowed.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(cts.IsCancellationRequested);
            Assert.Single(borrowed.Opens);
            Assert.Single(borrowed.Faults);
            Assert.IsAssignableFrom<OperationCanceledException>(borrowed.Faults.Single());
            Assert.Empty(borrowed.Finishes); // no reader exists to finish cleanly
            Assert.Equal(1, borrowed.DisposeCount);
            Assert.NotEmpty(replacement.Opens);
            AssertOnDisk(file);
        }
        finally
        {
            cts.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Yield_DoesNotInterruptNonStoppableOpen()
    {
        var file = AddFile("native-open.r00", 100_000);
        var raceWantsLogin = 0;
        var native = new FakeLane(_source, "standard PASV", canStopEarly: false)
        {
            BlockOpens = true,
            YieldWhen = () => Volatile.Read(ref raceWantsLogin) != 0,
        };
        using var cts = new CancellationTokenSource();
        var run = new SegmentedTransfer([file], Opts()).RunAsync([native], null, null, cts.Token);
        try
        {
            await native.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref raceWantsLogin, 1);
            await Task.Delay(TimeSpan.FromMilliseconds(750));

            Assert.False(run.IsCompleted);
            Assert.False(native.Disposed.Task.IsCompleted);
            Assert.Empty(native.Faults);
        }
        finally
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Single(native.Faults);
        Assert.Equal(1, native.DisposeCount);
    }

    // ---- Codex review round 2 regressions ----

    [Fact]
    public async Task CancelledDuringEofConfirmation_IsNotPublished_AndResumeRefetchesOnlyTheTail()
    {
        var f = AddFile("a.r00", 300_000);
        using (var cts = new CancellationTokenSource())
        {
            var hanging = new FakeLane(_source, "main", canStopEarly: false) { BlockEofFinish = true };
            var run = new SegmentedTransfer([f], Opts()).RunAsync([hanging], null, null, cts.Token);
            while (!hanging.Finishes.Contains(true)) await Task.Delay(10);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        Assert.False(File.Exists(f.LocalPath), "an unconfirmed file must not be published");

        var second = new FakeLane(_source, "main");
        await new SegmentedTransfer([f], Opts()).RunAsync([second], null, null, default);

        AssertOnDisk(f);
        Assert.All(second.Opens, o => Assert.True(o.Offset > 200_000, $"refetched from {o.Offset}, not just the tail"));
    }

    [Fact]
    public async Task UnknownSize_RefetchedShorter_IsTruncated()
    {
        var f = AddFile("listing.bin", 100_000, unknownSize: true);
        var shorter = new byte[40_000];
        new Random(7).NextBytes(shorter);
        int failures = 0;
        var lane = new FakeLane(_source, "main")
        {
            FinishFault = eof =>
            {
                if (!eof || Interlocked.Increment(ref failures) != 1) return null;
                _source[f.Key] = shorter;                    // the remote changed under us
                return new IOException("RETR did not complete: 426");
            },
        };

        await new SegmentedTransfer([f], Opts()).RunAsync([lane], null, null, default);

        Assert.Equal(shorter, File.ReadAllBytes(f.LocalPath));
    }

    // Progress<T> posts to the sync context / thread pool; this reports inline.
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeLaneSource(params ISegmentLane[] lanes) : ILaneSource
    {
        private readonly ConcurrentQueue<ISegmentLane> _lanes = new(lanes);
        public Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult(_lanes.TryDequeue(out var l) ? l : null);
    }

    private sealed class GatedLaneSource(Func<bool> available, ISegmentLane lane) : ILaneSource
    {
        private ISegmentLane? _lane = lane;
        public Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult(available() ? Interlocked.Exchange(ref _lane, null) : null);
    }

    internal sealed class FakeLane(Dictionary<string, byte[]> source, string label, bool canStopEarly = true) : ISegmentLane
    {
        public string Label => label;
        public string? NetworkId => "net-" + label;
        public bool CanStopEarly => canStopEarly;
        public bool ShouldYield => YieldWhen?.Invoke() ?? false;

        public Func<bool>? YieldWhen { get; set; }
        public Func<TransferFile, Exception?>? OpenFault { get; init; }
        public Func<long, Exception?>? ReadFault { get; init; }
        public Func<TransferFile, long, Exception?>? OpenFaultAt { get; init; }
        public Func<bool, Exception?>? FinishFault { get; init; }
        public long BlockReadsAt { get; set; } = long.MaxValue;   // reads hang (until cancelled) from here
        public bool BlockOpens { get; init; }
        public bool BlockEofFinish { get; init; }                 // the EOF confirmation hangs until cancelled
        public int ReadDelayMs { get; init; }
        public int MaxChunk { get; init; } = 8 * 1024;
        public long TruncateAt { get; init; } = long.MaxValue;

        public readonly ConcurrentQueue<(string Key, long Offset, long End)> Opens = new();
        public readonly ConcurrentQueue<Exception> Faults = new();
        public readonly ConcurrentQueue<bool> Finishes = new();
        public long BytesRead;
        public int DisposeCount;
        public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct)
        {
            Opens.Enqueue((file.Key, offset, end));
            OpenEntered.TrySetResult();
            if (OpenFault?.Invoke(file) is { } ex) throw ex;
            if (OpenFaultAt?.Invoke(file, offset) is { } ex2) throw ex2;
            if (BlockOpens) return Task.Delay(Timeout.Infinite, ct).ContinueWith<ISegmentReader>(_ => throw new OperationCanceledException(ct));
            return Task.FromResult<ISegmentReader>(new Reader(this, source[file.Key], offset));
        }

        public void Fault(Exception ex) => Faults.Enqueue(ex);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private sealed class Reader(FakeLane lane, byte[] data, long position) : ISegmentReader
        {
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                if (lane.ReadDelayMs > 0) await Task.Delay(lane.ReadDelayMs, ct);
                else await Task.Yield();
                if (lane.ReadFault?.Invoke(position) is { } ex) throw ex;
                if (position >= lane.BlockReadsAt) await Task.Delay(Timeout.Infinite, ct);
                long limit = Math.Min(data.Length, lane.TruncateAt);
                int n = (int)Math.Min(Math.Min(buffer.Length, lane.MaxChunk), Math.Max(0, limit - position));
                data.AsMemory((int)position, n).CopyTo(buffer);
                position += n;
                Interlocked.Add(ref lane.BytesRead, n);
                return n;
            }

            public Task FinishAsync(bool reachedEof, CancellationToken ct)
            {
                lane.Finishes.Enqueue(reachedEof);
                if (reachedEof && lane.BlockEofFinish) return Task.Delay(Timeout.Infinite, ct);
                return lane.FinishFault?.Invoke(reachedEof) is { } ex ? Task.FromException(ex) : Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
