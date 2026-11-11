using System.Collections.Concurrent;
using System.IO;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadGrowthDrainTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-growth-drain-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GrowthWaitsForNativePeerReadBeforeDisposalAndPreservesOriginalFailure()
    {
        var peerFile = FilePlan("peer.r00", 128 * 1024);
        var growingFile = FilePlan("growing.r01", 128 * 1024);
        var source = new Dictionary<string, byte[]>
        {
            [peerFile.Key] = Bytes((int)peerFile.Size, 11),
            [growingFile.Key] = Bytes((int)growingFile.Size, 13),
        };
        var growth = new RemoteFileSizeExceededException(growingFile.Key, growingFile.Size);
        var offender = new SegmentedTransferTests.FakeLane(source, "growing", canStopEarly: false)
        {
            FinishFault = eof => eof ? growth : null,
        };
        var peer = new NativeReadLane(source[peerFile.Key]);
        var completed = new ConcurrentQueue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transfer = new SegmentedTransfer([peerFile, growingFile], Options(file => completed.Enqueue(file.Key)));
        var run = transfer.RunAsync([peer], new AfterReadStarts(peer.ReadEntered.Task, offender), null, deadline.Token);
        try
        {
            await peer.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted, "the transfer must drain an in-flight native read before returning");
            Assert.Equal(0, peer.ReaderDisposeCount);
            Assert.Equal(0, peer.LaneDisposeCount);
            Assert.False(File.Exists(growingFile.LocalPath));
            Assert.DoesNotContain(growingFile.Key, completed);

            peer.ReleaseRead.TrySetResult();
            var actual = await Assert.ThrowsAsync<RemoteFileSizeExceededException>(() => run);
            Assert.Same(growth, actual);
            Assert.Same(growth, Assert.Single(offender.Faults));
            Assert.Single(offender.Opens);
            Assert.Equal(1, offender.DisposeCount);
            Assert.Equal(1, peer.ReaderDisposeCount);
            Assert.Equal(1, peer.LaneDisposeCount);
            Assert.False(peer.DisposedDuringRead);
            Assert.All(peer.Faults, error => Assert.IsAssignableFrom<OperationCanceledException>(error));
            Assert.False(File.Exists(growingFile.LocalPath));
            await using (var staged = StagedFile.Open(growingFile))
                Assert.Empty(staged.Done.Ranges);

            var refreshed = growingFile with { Size = 192 * 1024, Validator = "grown" };
            source[refreshed.Key] = Bytes((int)refreshed.Size, 17);
            var recovery = new SegmentedTransferTests.FakeLane(source, "recovery", canStopEarly: false);
            await new SegmentedTransfer([peerFile, refreshed], Options())
                .RunAsync([recovery], null, null, deadline.Token);
            Assert.Equal(source[peerFile.Key], File.ReadAllBytes(peerFile.LocalPath));
            Assert.Equal(source[refreshed.Key], File.ReadAllBytes(refreshed.LocalPath));
            Assert.False(File.Exists(refreshed.LocalPath + StagedFile.SidecarSuffix));
            Assert.False(File.Exists(refreshed.LocalPath + StagedFile.StagingSuffix));
        }
        finally
        {
            peer.ReleaseRead.TrySetResult();
            deadline.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task SecondOverflowObservedAfterCohortCancellationStillInvalidatesItsStagedBytes()
    {
        var lateFile = FilePlan("late.r00", 128 * 1024);
        var firstFile = FilePlan("first.r01", 128 * 1024);
        var source = new Dictionary<string, byte[]>
        {
            [lateFile.Key] = Bytes((int)lateFile.Size, 23),
            [firstFile.Key] = Bytes((int)firstFile.Size, 29),
        };
        var lateFailure = new RemoteFileSizeExceededException(lateFile.Key, lateFile.Size);
        var firstFailure = new RemoteFileSizeExceededException(firstFile.Key, firstFile.Size);
        var late = new DelayedOverflowLane(source, lateFailure);
        var first = new SegmentedTransferTests.FakeLane(source, "first failure", canStopEarly: false)
        {
            FinishFault = eof => eof ? firstFailure : null,
        };
        var completed = new ConcurrentQueue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new SegmentedTransfer([lateFile, firstFile], Options(file => completed.Enqueue(file.Key)))
            .RunAsync([late], new AfterReadStarts(late.FinishEntered.Task, first), null, deadline.Token);
        try
        {
            await late.FinishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await late.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            late.ReleaseFinish.TrySetResult();

            var actual = await Assert.ThrowsAsync<RemoteFileSizeExceededException>(() => run);
            Assert.Same(firstFailure, actual);
            Assert.Empty(completed);
            foreach (var file in new[] { lateFile, firstFile })
            {
                Assert.False(File.Exists(file.LocalPath));
                await using var staged = StagedFile.Open(file);
                Assert.Empty(staged.Done.Ranges);
            }
            Assert.Single(late.Inner.Opens);
            Assert.Single(first.Opens);
            Assert.Equal(1, late.Inner.DisposeCount);
            Assert.Equal(1, first.DisposeCount);

            // Even if the next server listing still has the old size (for example,
            // REST was ignored), neither rejected file may resume suspect ranges.
            var recovery = new SegmentedTransferTests.FakeLane(source, "recovery", canStopEarly: false);
            await new SegmentedTransfer([lateFile, firstFile], Options())
                .RunAsync([recovery], null, null, deadline.Token);
            Assert.All(recovery.Opens, opened => Assert.Equal(0, opened.Offset));
            Assert.Equal(source[lateFile.Key], File.ReadAllBytes(lateFile.LocalPath));
            Assert.Equal(source[firstFile.Key], File.ReadAllBytes(firstFile.LocalPath));
        }
        finally
        {
            late.ReleaseFinish.TrySetResult();
            deadline.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private TransferFile FilePlan(string name, int size) => new("/release/" + name, Path.Combine(_directory, name), size, "original");

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static SegmentedTransferOptions Options(Action<TransferFile>? completed = null) => new()
    {
        BufferSize = 8 * 1024,
        MinSplitBytes = 16 * 1024,
        MaxLanes = 2,
        LaneRetryDelay = TimeSpan.FromMilliseconds(1),
        ExtraLanePollInterval = TimeSpan.FromMilliseconds(10),
        ProgressInterval = TimeSpan.FromMilliseconds(10),
        OnFileCompleted = completed,
    };

    private sealed class AfterReadStarts(Task readEntered, ISegmentLane lane) : ILaneSource
    {
        private ISegmentLane? _available = lane;
        public Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult(readEntered.IsCompleted ? Interlocked.Exchange(ref _available, null) : null);
    }

    private sealed class DelayedOverflowLane(Dictionary<string, byte[]> source, Exception failure) : ISegmentLane
    {
        public SegmentedTransferTests.FakeLane Inner { get; } = new(source, "late overflow", canStopEarly: false);
        public string Label => Inner.Label;
        public string? NetworkId => Inner.NetworkId;
        public bool CanStopEarly => false;
        public bool ShouldYield => false;
        public TaskCompletionSource FinishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct) =>
            new Reader(this, await Inner.OpenAsync(file, offset, end, ct), failure);

        public void Fault(Exception error) => Inner.Fault(error);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();

        private sealed class Reader(DelayedOverflowLane lane, ISegmentReader inner, Exception failure) : ISegmentReader
        {
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => inner.ReadAsync(buffer, ct);

            public async Task FinishAsync(bool reachedEof, CancellationToken ct)
            {
                if (!reachedEof) { await inner.FinishAsync(false, ct); return; }
                lane.FinishEntered.TrySetResult();
                using var registration = ct.Register(() => lane.CancellationObserved.TrySetResult());
                await lane.ReleaseFinish.Task;
                throw failure; // The pending EOF read returned extra data after its peer failed.
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    // Models the native stream's lifetime: cancellation cannot interrupt an already
    // running recv, so no reader/session cleanup may happen until that recv returns.
    private sealed class NativeReadLane(byte[] data) : ISegmentLane
    {
        public string Label => "native peer";
        public string? NetworkId => null;
        public bool CanStopEarly => false;
        public bool ShouldYield => false;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Exception> Faults { get; } = new();
        public int ReaderDisposeCount;
        public int LaneDisposeCount;
        public bool DisposedDuringRead;
        private int _reading;

        public Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct) =>
            Task.FromResult<ISegmentReader>(new Reader(this, data, (int)offset));

        public void Fault(Exception ex) => Faults.Enqueue(ex);

        public ValueTask DisposeAsync()
        {
            DisposedDuringRead |= Volatile.Read(ref _reading) != 0;
            Interlocked.Increment(ref LaneDisposeCount);
            return ValueTask.CompletedTask;
        }

        private sealed class Reader(NativeReadLane lane, byte[] data, int position) : ISegmentReader
        {
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                Interlocked.Exchange(ref lane._reading, 1);
                lane.ReadEntered.TrySetResult();
                using var registration = ct.Register(() => lane.CancellationObserved.TrySetResult());
                try
                {
                    await lane.ReleaseRead.Task; // Deliberately does not observe cancellation.
                    var count = Math.Min(buffer.Length, data.Length - position);
                    data.AsMemory(position, count).CopyTo(buffer);
                    position += count;
                    return count;
                }
                finally { Volatile.Write(ref lane._reading, 0); }
            }

            public Task FinishAsync(bool reachedEof, CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                lane.DisposedDuringRead |= Volatile.Read(ref lane._reading) != 0;
                Interlocked.Increment(ref lane.ReaderDisposeCount);
                return ValueTask.CompletedTask;
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
