using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class StatsProbeCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_reset_rejects_old_positive_negative_and_trailer_results(bool trailer)
    {
        var cache = new StatsProbeCache();
        var pending = cache.Read();
        cache.Reset();
        Assert.False(cache.TryPublish(pending with
        {
            WorkingCommand = "SITE OLD", ViaListTrailer = trailer,
            UnavailableUntil = DateTime.UtcNow.AddHours(6)
        }));
        var current = cache.Read();
        Assert.Null(current.WorkingCommand);
        Assert.False(current.ViaListTrailer);
        Assert.Equal(default, current.UnavailableUntil);
        Assert.True(cache.TryPublish(current with { WorkingCommand = "SITE NEW" }));
        Assert.Equal("SITE NEW", cache.Read().WorkingCommand);
        Assert.False(cache.TryPublish(pending));
        Assert.Equal("SITE NEW", cache.Read().WorkingCommand);
    }
}
