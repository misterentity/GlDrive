using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class SegmentedByteRangeSetTests
{
    private static ByteRange R(long s, long e) => new(s, e);

    [Fact]
    public void Add_MergesOverlappingAndAdjacent()
    {
        var set = new ByteRangeSet();
        set.Add(R(10, 20));
        set.Add(R(30, 40));
        Assert.Equal(2, set.Ranges.Count);

        set.Add(R(20, 30)); // adjacent on both sides
        Assert.Equal([R(10, 40)], set.Ranges);
        Assert.Equal(30, set.TotalLength);

        Assert.Equal(5, set.Add(R(5, 15)));   // overlap on the left
        Assert.Equal(0, set.Add(R(12, 18)));  // fully inside
        Assert.Equal(15, set.Add(R(0, 50)));  // swallows everything
        Assert.Equal([R(0, 50)], set.Ranges);
        Assert.Equal(50, set.TotalLength);
    }

    [Fact]
    public void Add_KeepsOrderAndIgnoresEmpty()
    {
        var set = new ByteRangeSet([R(50, 60), R(0, 10), R(20, 30), R(5, 5)]);
        Assert.Equal([R(0, 10), R(20, 30), R(50, 60)], set.Ranges);
        Assert.Equal(30, set.TotalLength);
    }

    [Fact]
    public void Contains_And_Covers()
    {
        var set = new ByteRangeSet([R(0, 10), R(20, 30)]);
        Assert.True(set.Contains(0));
        Assert.True(set.Contains(9));
        Assert.False(set.Contains(10));
        Assert.True(set.Contains(25));
        Assert.False(set.Contains(30));

        Assert.True(set.Covers(R(2, 8)));
        Assert.True(set.Covers(R(20, 30)));
        Assert.False(set.Covers(R(5, 25)));
        Assert.False(set.Covers(R(15, 18)));
        Assert.True(set.Covers(R(7, 7)));
    }

    [Fact]
    public void Gaps_WithinSize()
    {
        var set = new ByteRangeSet([R(10, 20), R(30, 40)]);
        Assert.Equal([R(0, 10), R(20, 30), R(40, 100)], set.Gaps(100));
        Assert.Equal([R(0, 10), R(20, 30)], set.Gaps(35));
        Assert.Equal([R(0, 5)], set.Gaps(5));
        Assert.Empty(new ByteRangeSet([R(0, 50)]).Gaps(50));
        Assert.Equal([R(0, 7)], new ByteRangeSet().Gaps(7));
        Assert.Empty(new ByteRangeSet().Gaps(0));
    }
}
