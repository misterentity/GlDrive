using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.49 — a mount that failed at startup was never retried, so the 2026-08-06
/// resume-from-sleep (network not up yet at 09:07) killed all FTP for 10+ hours
/// while IRC recovered on its own.
/// </summary>
public class MountRetryPolicyTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]   // capped
    [InlineData(6, 300)]
    [InlineData(50, 300)]
    public void DelayFor_backs_off_exponentially_then_caps(int attempt, int expectedSeconds)
        => Assert.Equal(expectedSeconds, MountRetryPolicy.DelayFor(attempt).TotalSeconds);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void DelayFor_treats_non_positive_attempts_as_the_first(int attempt)
        => Assert.Equal(MountRetryPolicy.InitialDelay, MountRetryPolicy.DelayFor(attempt));

    [Fact]
    public void DelayFor_never_exceeds_the_cap_for_any_attempt()
    {
        // A huge attempt count must not overflow into a negative or absurd delay —
        // hammering a BNC risks its ~2h rate-limit cooldown.
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var delay = MountRetryPolicy.DelayFor(attempt);
            Assert.InRange(delay, MountRetryPolicy.InitialDelay, MountRetryPolicy.MaxDelay);
        }
    }

    [Fact]
    public void DelayFor_is_monotonically_non_decreasing()
    {
        for (var attempt = 1; attempt < 20; attempt++)
            Assert.True(MountRetryPolicy.DelayFor(attempt + 1) >= MountRetryPolicy.DelayFor(attempt));
    }

    [Fact]
    public void ShouldRetry_retries_a_configured_enabled_unmounted_server()
        => Assert.True(MountRetryPolicy.ShouldRetry(
            alreadyMounted: false, existsInConfig: true, enabled: true, autoMountOnStart: true));

    [Fact]
    public void ShouldRetry_stops_once_the_server_is_mounted()
        => Assert.False(MountRetryPolicy.ShouldRetry(
            alreadyMounted: true, existsInConfig: true, enabled: true, autoMountOnStart: true));

    [Fact]
    public void ShouldRetry_stops_when_the_server_was_removed_from_config()
        => Assert.False(MountRetryPolicy.ShouldRetry(
            alreadyMounted: false, existsInConfig: false, enabled: true, autoMountOnStart: true));

    [Fact]
    public void ShouldRetry_stops_when_the_server_was_disabled()
        => Assert.False(MountRetryPolicy.ShouldRetry(
            alreadyMounted: false, existsInConfig: true, enabled: false, autoMountOnStart: true));

    [Fact]
    public void ShouldRetry_respects_autoMountOnStart_being_turned_off()
        => Assert.False(MountRetryPolicy.ShouldRetry(
            alreadyMounted: false, existsInConfig: true, enabled: true, autoMountOnStart: false));

    [Fact]
    public void Retry_reaches_the_cap_quickly_enough_to_recover_within_minutes()
    {
        // The whole point is prompt self-recovery: four retries must all land inside
        // the first ~8 minutes, not hours.
        var cumulative = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 4; attempt++)
            cumulative += MountRetryPolicy.DelayFor(attempt);

        Assert.True(cumulative <= TimeSpan.FromMinutes(8),
            $"four retries took {cumulative}, which is too slow to recover from a resume-from-sleep");
    }
}
