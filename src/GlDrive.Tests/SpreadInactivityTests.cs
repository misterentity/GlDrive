using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class SpreadInactivityTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Repeated_cooldown_polls_stop_suppressing_inactivity_after_three_minutes()
    {
        for (var seconds = 5; seconds < 180; seconds += 5)
            Assert.Equal(0, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(seconds), true, false, 1800));
        Assert.Equal(180, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(180), true, false, 1800));
        Assert.Equal(600, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(600), true, false, 1800));
    }

    [Fact]
    public void Completion_wait_has_its_own_bounded_allowance()
    {
        Assert.Equal(0, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(300), false, true, 360));
        Assert.Equal(360, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(360), false, true, 360));
        Assert.Equal(300, SpreadJob.EffectiveIdleSeconds(Start, Start.AddSeconds(300), true, false, 360));
    }

    [Fact]
    public void Actual_activity_starts_a_new_allowance()
    {
        var now = Start.AddSeconds(600);
        Assert.Equal(0, SpreadJob.EffectiveIdleSeconds(now.AddSeconds(-10), now, true, false, 360));
        Assert.Equal(65, SpreadJob.EffectiveIdleSeconds(now.AddSeconds(-65), now, false, false, 360));
    }
}
