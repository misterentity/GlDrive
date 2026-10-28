using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceWorkEstimatorTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private const long MiB = 1024 * 1024;
    private static RaceHistoryItem History(string section, string resolution, double seconds, long bytes = 0) => new()
    {
        Section = section, ReleaseName = $"Example.Show.S01E01.{resolution}.WEB.H264-GRP", Result = SpreadJobState.Completed,
        StartedAt = Now, CompletedAt = Now.AddSeconds(seconds), BytesTransferred = bytes
    };
    private static RaceWorkEstimate Estimate(long? bytes = null, double speed = 0, params RaceHistoryItem[] history) =>
        RaceWorkEstimator.Estimate("tv", "Example.Show.S01E02.1080p.WEB.H264-GRP", bytes, speed, history);

    [Fact]
    public void Announced_size_and_existing_route_speed_take_precedence()
    {
        var estimate = Estimate(1000 * MiB, 10 * MiB, History("tv", "1080p", 10, 10 * MiB));
        Assert.Equal(1000 * MiB, estimate.ExpectedBytes);
        Assert.Equal(100, estimate.PredictedSeconds);
        Assert.Equal("announced-size/route-speed", estimate.Basis);
    }

    [Fact]
    public void Announced_size_uses_matching_historical_speed_then_named_default()
    {
        var estimate = Estimate(1000 * MiB, 0, History("tv", "1080p", 50, 500 * MiB), History("tv", "2160p", 1000, MiB));
        Assert.Equal(100, estimate.PredictedSeconds);
        Assert.Equal("announced-size/section-resolution-speed", estimate.Basis);
        Assert.Equal(100, Estimate(1000 * MiB).PredictedSeconds);
        Assert.Equal("announced-size/default-speed", Estimate(1000 * MiB).Basis);
    }

    [Fact]
    public void Section_resolution_medians_are_resistant_to_outliers()
    {
        var estimate = Estimate(null, 0, History("TV", "1080p", 100, 100 * MiB),
            History("tv", "1080p", 200, 200 * MiB), History("tv", "1080p", 10000, 10000 * MiB),
            History("tv", "2160p", 5000), History("movies", "1080p", 4000));
        Assert.Equal(200, estimate.PredictedSeconds);
        Assert.Equal(200 * MiB, estimate.ExpectedBytes);
        Assert.Equal("section-resolution-duration", estimate.Basis);
    }

    [Fact]
    public void Size_median_can_use_current_route_speed_without_inheriting_stale_duration()
    {
        var estimate = Estimate(null, 10 * MiB, History("tv", "1080p", 100, 1000 * MiB), History("tv", "1080p", 500, 3000 * MiB));
        Assert.Equal(2000 * MiB, estimate.ExpectedBytes);
        Assert.Equal(200, estimate.PredictedSeconds);
        Assert.Equal("section-resolution-size/route-speed", estimate.Basis);
    }

    [Theory]
    [InlineData("tv", "2160p", "section-duration")]
    [InlineData("movies", "1080p", "resolution-duration")]
    [InlineData("movies", "2160p", "global-duration")]
    public void Sparse_history_has_explicit_fallback_cohorts(string section, string resolution, string basis)
    {
        var estimate = Estimate(null, 0, History(section, resolution, 120));
        Assert.Equal(120, estimate.PredictedSeconds);
        Assert.Equal(basis, estimate.Basis);
        Assert.Null(estimate.ExpectedBytes);
    }

    [Fact]
    public void Unknown_history_uses_named_static_duration()
    {
        var estimate = Estimate();
        Assert.Equal(300, estimate.PredictedSeconds);
        Assert.Equal("default-duration", estimate.Basis);
        Assert.Null(estimate.ExpectedBytes);
    }

    [Fact]
    public void Failed_stopped_malformed_and_unbounded_history_cannot_bias_the_queue()
    {
        var failed = History("tv", "1080p", 1); failed.Result = SpreadJobState.Failed;
        var stopped = History("tv", "1080p", 1); stopped.Result = SpreadJobState.Stopped;
        var negativeBytes = History("tv", "1080p", 1, -1);
        var hugeBytes = History("tv", "1080p", 1, long.MaxValue);
        Assert.Equal(300, Estimate(null, 0, failed, stopped, negativeBytes, hugeBytes,
            History("tv", "1080p", 0), History("tv", "1080p", -1), History("tv", "1080p", 90000)).PredictedSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1e30)]
    public void Invalid_route_speed_uses_finite_fallback(double speed)
    {
        var estimate = Estimate(1000 * MiB, speed);
        Assert.Equal(100, estimate.PredictedSeconds);
        Assert.Equal("announced-size/default-speed", estimate.Basis);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Invalid_announced_sizes_are_unknown(long bytes)
    {
        var estimate = Estimate(bytes, 10 * MiB);
        Assert.Null(estimate.ExpectedBytes);
        Assert.Equal(300, estimate.PredictedSeconds);
    }

    [Fact]
    public void Predictions_have_hard_lower_and_upper_bounds()
    {
        Assert.Equal(5, Estimate(1, 1e9).PredictedSeconds);
        Assert.Equal(5400, Estimate(1L << 50, 1).PredictedSeconds);
        Assert.Equal(5, Estimate(null, 0, History("tv", "1080p", 0.01)).PredictedSeconds);
        Assert.Equal(5400, Estimate(null, 0, History("tv", "1080p", 10000)).PredictedSeconds);
    }
}
