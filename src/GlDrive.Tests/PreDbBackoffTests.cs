using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-19/20 log flood: api.predb.net answered 503 for hours, the
/// dashboard refreshed on a fixed 15-second timer regardless, and every failure logged a full
/// stack trace — 135 warnings on 08-19, 64 on 08-20, four wasted requests a minute.
/// </summary>
public sealed class PreDbBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyClientNeverSkips()
    {
        var b = new PreDbBackoff();
        Assert.False(b.ShouldSkip(T0));
        Assert.Equal(0, b.ConsecutiveFailures);
    }

    [Fact]
    public void FailureOpensACooldownThatSuppressesTheNextAttempt()
    {
        var b = new PreDbBackoff();
        var delay = b.RecordFailure(T0);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        Assert.True(b.ShouldSkip(T0 + TimeSpan.FromSeconds(29)));
        Assert.False(b.ShouldSkip(T0 + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void LadderEscalatesAndThenHoldsAtTheCap()
    {
        var b = new PreDbBackoff();
        var now = T0;

        Assert.Equal(TimeSpan.FromSeconds(30), b.RecordFailure(now));
        Assert.Equal(TimeSpan.FromMinutes(1), b.RecordFailure(now));
        Assert.Equal(TimeSpan.FromMinutes(2), b.RecordFailure(now));
        Assert.Equal(TimeSpan.FromMinutes(5), b.RecordFailure(now));
        Assert.Equal(TimeSpan.FromMinutes(15), b.RecordFailure(now));

        // Capped, not overflowing off the end of the ladder.
        for (var i = 0; i < 50; i++)
            Assert.Equal(TimeSpan.FromMinutes(15), b.RecordFailure(now));
    }

    [Fact]
    public void SuccessClearsTheLadderCompletely()
    {
        var b = new PreDbBackoff();
        b.RecordFailure(T0);
        b.RecordFailure(T0);
        b.RecordFailure(T0);

        b.RecordSuccess();

        Assert.Equal(0, b.ConsecutiveFailures);
        Assert.False(b.ShouldSkip(T0));
        // And the next failure starts at the bottom of the ladder, not where it left off.
        Assert.Equal(TimeSpan.FromSeconds(30), b.RecordFailure(T0));
    }

    /// <summary>
    /// The stack trace explains the outage exactly once. Restating it every 15 seconds is what
    /// buried the rest of the log.
    /// </summary>
    [Fact]
    public void OnlyTheFirstFailureOfARunCarriesTheException()
    {
        var b = new PreDbBackoff();

        Assert.True(b.ShouldLogWithException);
        b.RecordFailure(T0);
        Assert.True(b.ShouldLogWithException);   // still the first failure's line
        b.RecordFailure(T0);
        Assert.False(b.ShouldLogWithException);

        b.RecordSuccess();
        Assert.True(b.ShouldLogWithException);   // a new outage is a new explanation
    }

    /// <summary>
    /// The point of the whole exercise: replay the observed outage cadence and count requests.
    /// The old client made one every 15 seconds; the ladder must cut that by well over 90%.
    /// </summary>
    [Fact]
    public void ReplayingTheObservedOutageCollapsesTheRequestCount()
    {
        var b = new PreDbBackoff();
        var attempted = 0;

        // Six hours of 503s, polled every 15 seconds.
        for (var tick = 0; tick < 6 * 60 * 4; tick++)
        {
            var now = T0 + TimeSpan.FromSeconds(15 * tick);
            if (b.ShouldSkip(now)) continue;
            attempted++;
            b.RecordFailure(now);
        }

        Assert.Equal(1440, 6 * 60 * 4);          // what the old client would have sent
        Assert.True(attempted < 40, $"expected the ladder to collapse the outage to a few dozen requests, got {attempted}");
    }
}
