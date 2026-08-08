using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.50: a forever-retry that logs two stacks per attempt buried the day's log
/// (7,244 of 16,084 lines on 2026-08-07). These pin the throttle that replaced it.
/// </summary>
public class MountFailureLogPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OpeningAttemptsAlwaysLogInFull(int attempt)
        => Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(226)]
    public void SteadyStateRetriesAreCompact(int attempt)
        => Assert.False(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(228)]
    public void EveryTwelfthAttemptLogsInFull(int attempt)
        => Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Fact]
    public void NonPositiveAttemptIsTreatedAsTheFirst()
    {
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(0));
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(-5));
    }

    [Fact]
    public void ChangedErrorLogsInFullEvenDeepIntoAStreak()
    {
        // The moment the failure mode changes is the diagnostic moment — it must not
        // be swallowed just because the streak is long.
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 200, previousError: "Timed out trying to connect!", currentError: "530 Too many logins"));
    }

    [Fact]
    public void UnchangedErrorDeepIntoAStreakStaysCompact()
    {
        Assert.False(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 200, previousError: "Timed out trying to connect!", currentError: "Timed out trying to connect!"));
    }

    [Fact]
    public void FirstAttemptHasNoPreviousErrorAndLogsInFull()
    {
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 1, previousError: null, currentError: "Timed out trying to connect!"));
    }

    [Fact]
    public void A226AttemptStreakLogsFarFewerStacksThanAttempts()
    {
        // The regression this guards: 226 attempts previously produced 226 stacks
        // from this call site alone (plus another 226 from MountService).
        var full = 0;
        for (var i = 1; i <= 226; i++)
            if (MountFailureLogPolicy.ShouldLogFullDetail(i, "same", "same")) full++;

        Assert.Equal(3 + 226 / MountFailureLogPolicy.PeriodicInterval, full);
        Assert.True(full < 25, $"expected a large reduction, got {full} full logs in 226 attempts");
    }
}
