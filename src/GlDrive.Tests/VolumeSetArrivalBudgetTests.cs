using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-20 defect: the extractor's set-wide wait was bounded on
/// TOTAL elapsed time, so a 2160p set that had not finished arriving after five minutes was
/// reported as a timeout, retried, and — on the fifth retry — abandoned with the reason "no
/// progress after 5 retries" while bytes were landing every two seconds.
///
/// The invariant these tests hold: a set that is still moving has NOT stalled, no matter how
/// long it has been moving. Only inactivity, or the absolute ceiling, ends the wait.
/// </summary>
public sealed class VolumeSetArrivalBudgetTests
{
    // The two arrivals from the 2026-08-20 log. Freefall was detected at 08:56:30 and settled
    // at 09:13:46 (~17 min); Here.the.Whole.Time ran 09:12:58 → 09:32:09 (~19 min). Under the
    // old total-elapsed budget each of those produced three false timeouts.
    [Theory]
    [InlineData(17 * 60_000)]
    [InlineData(19 * 60_000)]
    [InlineData(90 * 60_000)]
    public void ContinuousProgressNeverTimesOut(long totalMs)
    {
        // Progress observed on every 2s sample: msSinceLastProgress never exceeds one interval.
        for (long t = 2000; t <= totalMs; t += 2000)
            Assert.Equal(
                VolumeSetArrivalBudget.Verdict.KeepWaiting,
                VolumeSetArrivalBudget.Evaluate(msSinceLastProgress: 2000, msElapsedTotal: t));
    }

    [Fact]
    public void StallsOnlyAfterTheFullNoProgressBudget()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.KeepWaiting,
            VolumeSetArrivalBudget.Evaluate(VolumeSetArrivalBudget.NoProgressBudgetMs - 1, 60 * 60_000));

        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.Stalled,
            VolumeSetArrivalBudget.Evaluate(VolumeSetArrivalBudget.NoProgressBudgetMs, 60 * 60_000));
    }

    /// <summary>
    /// A stall is a stall whether it happens in the first minute or the ninetieth. The old code
    /// could only detect one that began early enough to fit inside the total budget.
    /// </summary>
    [Fact]
    public void LateStallIsStillDetected()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.Stalled,
            VolumeSetArrivalBudget.Evaluate(VolumeSetArrivalBudget.NoProgressBudgetMs, 5 * 60 * 60_000));
    }

    /// <summary>
    /// The ceiling exists so a path that grows forever — a live log in a watch folder — cannot
    /// renew its inactivity budget indefinitely. It outranks the stall check.
    /// </summary>
    [Fact]
    public void CeilingTerminatesAnEndlesslyGrowingSet()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.KeepWaiting,
            VolumeSetArrivalBudget.Evaluate(2000, VolumeSetArrivalBudget.AbsoluteCeilingMs - 1));

        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.CeilingReached,
            VolumeSetArrivalBudget.Evaluate(2000, VolumeSetArrivalBudget.AbsoluteCeilingMs));
    }

    [Fact]
    public void CeilingOutranksStall()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.CeilingReached,
            VolumeSetArrivalBudget.Evaluate(
                VolumeSetArrivalBudget.NoProgressBudgetMs * 10,
                VolumeSetArrivalBudget.AbsoluteCeilingMs));
    }

    /// <summary>
    /// Only a stall may consume one of the five bounded watch retries. Re-reading tens of GB
    /// after twelve hours of continuous growth would not end differently.
    /// </summary>
    [Fact]
    public void OnlyStallDeservesARetry()
    {
        Assert.True(VolumeSetArrivalBudget.DeservesRetry(VolumeSetArrivalBudget.Verdict.Stalled));
        Assert.False(VolumeSetArrivalBudget.DeservesRetry(VolumeSetArrivalBudget.Verdict.CeilingReached));
        Assert.False(VolumeSetArrivalBudget.DeservesRetry(VolumeSetArrivalBudget.Verdict.KeepWaiting));
    }

    /// <summary>
    /// The behavioural claim, replayed against the readiness helper the wait loop actually
    /// uses: a growing set resets the clock, and the wait survives well past the old budget.
    /// This is the test that fails if the loop ever goes back to bounding on duration.
    /// </summary>
    [Fact]
    public void GrowingSetResetsTheClockAcrossAFullArrival()
    {
        var previous = new VolumeSetReadiness.Snapshot(1, 50_000_000, 0);
        long lastProgressMs = 0;
        long now = 0;

        // 26 parts landing over ~19 minutes, one part per ~44s, mirroring Here.the.Whole.Time.
        for (var part = 2; part <= 26; part++)
        {
            for (var tick = 0; tick < 22; tick++)
            {
                now += 2000;
                // locked=0 deliberately: the clock must be reset by a part LANDING, not by the
                // permanent "a member is write-locked" signal, which would make the test pass for
                // the wrong reason. Between landings 21 of every 22 samples show no change.
                var current = new VolumeSetReadiness.Snapshot(part, 50_000_000L * part, 0);

                if (VolumeSetReadiness.IsStillArriving(previous, current)) lastProgressMs = now;
                previous = current;

                Assert.Equal(
                    VolumeSetArrivalBudget.Verdict.KeepWaiting,
                    VolumeSetArrivalBudget.Evaluate(now - lastProgressMs, now));
            }
        }

        Assert.True(now > VolumeSetArrivalBudget.NoProgressBudgetMs,
            "the arrival must outlast the old total budget or this proves nothing");
    }
}
