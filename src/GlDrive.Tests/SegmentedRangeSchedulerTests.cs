using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class SegmentedRangeSchedulerTests
{
    private const long MiB = 1024 * 1024;

    private static RangeScheduler Make(long[] sizes, long minSplit = 0, long maxTake = 0, ByteRangeSet[]? done = null) =>
        new(sizes, done ?? sizes.Select(_ => new ByteRangeSet()).ToArray(), minSplit, maxTake);

    private static (int, long, long) Shape(RangeScheduler.Assignment a) => (a.FileIndex, a.Start, a.End);

    [Fact]
    public void Take_HandsOutFilesInOrder()
    {
        var s = Make([100, 200, 50], minSplit: 1000);
        Assert.Equal((0, 0L, 100L), Shape(s.Take(true)!));
        Assert.Equal((1, 0L, 200L), Shape(s.Take(true)!));
        Assert.Equal((2, 0L, 50L), Shape(s.Take(true)!));
        Assert.Null(s.Take(true));
    }

    [Fact]
    public void Take_CapsAtMaxTake()
    {
        var s = Make([1000], minSplit: 1000, maxTake: 300);
        Assert.Equal((0, 0L, 300L), Shape(s.Take(true)!));
        Assert.Equal((0, 300L, 600L), Shape(s.Take(true)!));
        Assert.Equal((0, 600L, 900L), Shape(s.Take(true)!));
        Assert.Equal((0, 900L, 1000L), Shape(s.Take(true)!));
        Assert.Null(s.Take(true));
    }

    [Fact]
    public void Take_SkipsDoneRangesAndZeroLengthFiles()
    {
        var done = new[] { new ByteRangeSet([new ByteRange(0, 100)]), new ByteRangeSet(), new ByteRangeSet([new ByteRange(0, 40)]) };
        var s = Make([100, 0, 100], minSplit: 1000, done: done);
        Assert.True(s.IsFileComplete(0));
        Assert.True(s.IsFileComplete(1));
        Assert.Equal(140, s.DoneBytes);
        Assert.Equal((2, 40L, 100L), Shape(s.Take(true)!));
    }

    [Fact]
    public void CannotStopEarly_OnlyGetsRangesEndingAtEof_Uncapped()
    {
        var done = new[] { new ByteRangeSet([new ByteRange(200, 300)]) };
        var s = Make([1000], minSplit: 0, maxTake: 50, done: done);

        var tail = s.Take(false)!;
        Assert.Equal((0, 300L, 1000L), Shape(tail)); // skips the mid-file gap [0,200), ignores maxTake

        Assert.Null(s.Take(false)); // only [0,200) is left and it doesn't reach EOF; tail isn't splittable

        var head = s.Take(true)!;
        Assert.Equal((0, 0L, 50L), Shape(head));
    }

    [Fact]
    public void CannotStopEarly_RangeIsNeverSplit()
    {
        var s = Make([8 * MiB], minSplit: 64 * 1024);
        var a = s.Take(false)!;
        Assert.Equal((0, 0L, 8 * MiB), Shape(a));
        Assert.Null(s.Take(true));
        Assert.Equal(8 * MiB, a.End);
    }

    [Fact]
    public void Steal_SplitsLargestRemainderAtAlignedMidpoint()
    {
        var s = Make([4 * MiB], minSplit: MiB);
        var a = s.Take(true)!;
        s.Advance(a, 100_000);

        var b = s.Take(true)!;
        // remaining 4_094_304 → mid 100_000 + 2_047_152 = 2_147_152 → 64 KiB floor 2_097_152
        Assert.Equal(2_097_152, a.End);
        Assert.Equal(2_097_152, s.CurrentEnd(a));
        Assert.Equal((0, 2_097_152L, 4 * MiB), Shape(b));
        Assert.Equal(0, b.Start % RangeScheduler.SplitAlignment);

        // a has 1_997_152 left (< 2 MiB), b has exactly 2 MiB → b is split.
        var c = s.Take(true)!;
        Assert.Equal(3 * MiB, b.End);
        Assert.Equal((0, 3 * MiB, 4 * MiB), Shape(c));
    }

    [Fact]
    public void Steal_RefusedBelowTwiceMinSplit()
    {
        var s = Make([4 * MiB], minSplit: 3 * MiB);
        var a = s.Take(true)!;
        Assert.Null(s.Take(true));
        Assert.Equal(4 * MiB, a.End);
    }

    [Fact]
    public void Steal_ByCannotStopEarlyLane_OnlyTakesEofTails()
    {
        var s = Make([4 * MiB, 4 * MiB], minSplit: 64 * 1024, maxTake: MiB);
        var a = s.Take(true)!; // file 0 [0,1M) — does not end at EOF
        Assert.Equal((0, 0L, MiB), Shape(a));
        var t = s.Take(false)!; // file 0 [1M,4M) gap reaches EOF
        Assert.Equal((0, MiB, 4 * MiB), Shape(t));
        var f1 = s.Take(false)!;
        Assert.Equal((1, 0L, 4 * MiB), Shape(f1));

        // Nothing left: a (splittable, not at EOF) can't go to a !canStopEarly lane.
        Assert.Null(s.Take(false));
        // A canStopEarly lane can split a.
        var c = s.Take(true)!;
        Assert.Equal((0, 512 * 1024L, MiB), Shape(c));
    }

    [Fact]
    public void Steal_NeverSplitsBelowReservedWrite()
    {
        var s = Make([4 * MiB], minSplit: 64 * 1024);
        var a = s.Take(true)!;
        Assert.Equal(3 * MiB, s.Reserve(a, 3 * MiB)); // a write of 3 MiB in progress
        var b = s.Take(true)!;
        Assert.True(b.Start >= 3 * MiB);
        Assert.Equal(3 * MiB, s.Advance(a, 3 * MiB));
    }

    [Fact]
    public void Release_ReExposesRemainder()
    {
        var s = Make([1000], minSplit: 10_000);
        var a = s.Take(true)!;
        s.Advance(a, 300);
        Assert.Null(s.Take(true));
        s.Release(a);
        s.Release(a); // idempotent
        Assert.Equal((0, 300L, 1000L), Shape(s.Take(true)!));
        Assert.Equal(300, s.DoneBytes);
        Assert.False(s.AllComplete);
    }

    [Fact]
    public void Advance_ClampsToShrunkEnd()
    {
        var s = Make([4 * MiB], minSplit: 64 * 1024);
        var a = s.Take(true)!;
        var b = s.Take(true)!; // a shrinks to 2 MiB
        Assert.Equal(2 * MiB, s.Advance(a, 3 * MiB));
        Assert.Equal(2 * MiB, a.Position);
        Assert.Equal(0, s.Reserve(a, 100));
        s.Advance(b, 2 * MiB);
        Assert.True(s.AllComplete);
        Assert.Equal(4 * MiB, s.DoneBytes);
    }

    [Fact]
    public void UnknownSize_SequentialSingleLaneUntilCompleted()
    {
        var s = Make([-1, 100], minSplit: 0);
        var a = s.Take(false)!;
        Assert.Equal((0, 0L, long.MaxValue), Shape(a));
        Assert.Equal(1, s.Take(true)!.FileIndex); // second lane skips the unknown file
        Assert.Null(s.Take(true));                 // never split
        s.Advance(a, 500);
        s.CompleteUnknown(a);
        s.Release(a);
        Assert.True(s.IsFileComplete(0));
    }

    [Fact]
    public void Redownload_OnlyWhenAllowed()
    {
        var done = new[] { new ByteRangeSet([new ByteRange(100, 1000)]) };
        var s = Make([1000], done: done);
        Assert.Null(s.Take(false));
        var a = s.Take(false, allowRedownload: true)!;
        Assert.Equal((0, 0L, 1000L), Shape(a));
        s.Advance(a, 1000);
        Assert.Equal(1000, s.DoneBytes); // no double counting
        Assert.True(s.AllComplete);
    }

    [Fact]
    public void ConcurrentStress_EveryByteDoneExactlyOnce()
    {
        long[] sizes = [700_000, 1, 0, 1_300_000, 333_333, 2_000_000];
        var s = Make(sizes, minSplit: 64 * 1024, maxTake: 256 * 1024);
        var counts = sizes.Select(n => new int[n]).ToArray();
        long added = 0;

        var threads = Enumerable.Range(0, 16).Select(t => new Thread(() =>
        {
            var rnd = new Random(t * 7919);
            bool canStop = t % 4 != 0;
            while (true)
            {
                var a = s.Take(canStop);
                if (a == null)
                {
                    if (s.AllComplete) return;
                    Thread.Yield();
                    continue;
                }
                bool quitEarly = canStop && rnd.Next(5) == 0;
                while (true)
                {
                    long n = s.Reserve(a, rnd.Next(1, 40_000));
                    if (n == 0) break;
                    long pos = a.Position;
                    for (long i = pos; i < pos + n; i++) Interlocked.Increment(ref counts[a.FileIndex][i]);
                    Interlocked.Add(ref added, s.Advance(a, n));
                    if (quitEarly && rnd.Next(3) == 0) break;
                }
                s.Release(a);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        Assert.True(threads.All(t => t.Join(TimeSpan.FromSeconds(30))));

        Assert.True(s.AllComplete);
        Assert.Equal(sizes.Sum(), s.DoneBytes);
        Assert.Equal(sizes.Sum(), Interlocked.Read(ref added));
        for (int f = 0; f < sizes.Length; f++)
            Assert.All(counts[f], c => Assert.Equal(1, c));
    }
}
