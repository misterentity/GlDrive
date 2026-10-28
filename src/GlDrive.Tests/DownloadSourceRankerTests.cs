using GlDrive.Downloads;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadSourceRankerTests
{
    private const string Release = "Example.Show.S01E01.1080p.WEB.H264-GROUP";
    private static DownloadSourceCandidate Source(string id, string? release = null, bool available = true,
        string? path = null) => new(id, release ?? Release, path ?? "/tv/" + (release ?? Release), available);

    [Fact]
    public void FasterMeasuredLocalRouteWinsRegardlessOfInputOrder()
    {
        var speeds = new SpeedTracker();
        speeds.RecordTransfer("slow", DownloadSourceRanker.LocalDestination, 1000, TimeSpan.FromSeconds(1));
        speeds.RecordTransfer("fast", DownloadSourceRanker.LocalDestination, 5000, TimeSpan.FromSeconds(1));
        var ranked = DownloadSourceRanker.Rank(Release, [Source("slow"), Source("fast")], speeds);
        Assert.Equal(new[] { "fast", "slow" }, ranked.Select(source => source.Source.ServerId));
        Assert.Equal(5000, ranked[0].BytesPerSecond);
        Assert.All(ranked, source => Assert.True(source.HasMeasuredSpeed));
    }

    [Fact]
    public void FxpAndReverseRoutesDoNotPretendToMeasureLocalDownloads()
    {
        var speeds = new SpeedTracker();
        speeds.RecordTransfer("a", "other-site", 1000000, TimeSpan.FromSeconds(1));
        speeds.RecordTransfer(DownloadSourceRanker.LocalDestination, "a", 1000000, TimeSpan.FromSeconds(1));
        speeds.RecordTransfer("b", DownloadSourceRanker.LocalDestination, 1000, TimeSpan.FromSeconds(1));
        var ranked = DownloadSourceRanker.Rank(Release, [Source("a"), Source("b")], speeds);
        Assert.Equal("b", ranked[0].Source.ServerId);
        Assert.Equal(0, ranked[1].BytesPerSecond);
        Assert.False(ranked[1].HasMeasuredSpeed);
    }

    [Fact]
    public void UnknownSpeedsHaveStableTiesWithoutAddressGuessing()
    {
        var speeds = new SpeedTracker();
        var ranked = DownloadSourceRanker.Rank(Release, [Source("z"), Source("a")], speeds);
        Assert.Equal(new[] { "a", "z" }, ranked.Select(source => source.Source.ServerId));
        Assert.All(ranked, source => Assert.Equal(0, source.BytesPerSecond));
    }

    [Fact]
    public void MovingAverageIsUsedInsteadOfLatestOutlier()
    {
        var speeds = new SpeedTracker();
        speeds.RecordTransfer("a", DownloadSourceRanker.LocalDestination, 100, TimeSpan.FromSeconds(1));
        speeds.RecordTransfer("a", DownloadSourceRanker.LocalDestination, 900, TimeSpan.FromSeconds(1));
        speeds.RecordTransfer("b", DownloadSourceRanker.LocalDestination, 600, TimeSpan.FromSeconds(1));
        Assert.Equal(500, DownloadSourceRanker.MeasuredSpeed(speeds, "a"));
        Assert.Equal("b", DownloadSourceRanker.Rank(Release, [Source("a"), Source("b")], speeds)[0].Source.ServerId);
    }

    [Fact]
    public void OtherVariantsUnavailableCopiesUnsafePathsAndDuplicateRowsAreExcluded()
    {
        var sources = new[]
        {
            Source("other", Release.Replace("1080p", "2160p")), Source("offline", available: false),
            Source("bad", path: "/tv/../" + Release), Source(""), Source("good"), Source("good")
        };
        Assert.Equal("good", Assert.Single(DownloadSourceRanker.Rank(Release, sources, new())).Source.ServerId);
    }
}
