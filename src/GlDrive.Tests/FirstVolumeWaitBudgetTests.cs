using System;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-21 defect fixed in v3.10.76: v3.10.73 converted the
/// SET-WIDE wait from a duration budget to an inactivity budget, but left the FIRST-VOLUME
/// wait ahead of it bounded on wall clock. That merely moved the false timeout one gate
/// upstream.
///
/// The log evidence, which is what these tests encode:
///   * 2026-08-20 (pre-.73): "not ready before timeout" 6, first-volume timeout 0.
///   * 2026-08-21 (post-.73): set-wide stalls 0 — the .73 fix works — but the first-volume
///     timeout went 0 → 2, exactly paired with "not ready before timeout" 2. Both were false:
///     Mother.Mary.2026.2160p.WEB.h265-ETHEL timed out at 14:19:46 and 14:24:58, burning two
///     of its five watch retries, and the identical set then logged "volume set settled after
///     845s (35 parts, 17181919933 bytes)" and extracted cleanly.
///
/// Why the first volume is the gate most likely to be waiting on nothing: for old-style
/// .rar/.r00/.r01… sets the first volume sorts AFTER every continuation part, so over FXP it
/// lands LAST. While it is absent, that one file shows no progress by construction — and the
/// set around it is visibly growing.
/// </summary>
public sealed class FirstVolumeWaitBudgetTests
{
    private const long Budget = VolumeSetArrivalBudget.NoProgressBudgetMs; // 300_000

    /// <summary>
    /// The Mother.Mary arrival: the set grew on every sample for 845 seconds while the first
    /// volume had not appeared. Under the old wall-clock bound this produced a timeout at 300s.
    /// </summary>
    [Fact]
    public void FirstVolumeWaitSurvivesAnArrivalLongerThanTheOldBudget()
    {
        for (long t = 2000; t <= 845_000; t += 2000)
        {
            // Set progress observed on every 2s sample, so time-since-progress stays at one tick.
            Assert.Equal(
                VolumeSetArrivalBudget.Verdict.KeepWaiting,
                VolumeSetArrivalBudget.Evaluate(
                    msSinceLastProgress: 2000, msElapsedTotal: t, noProgressBudgetMs: Budget));
        }
    }

    /// <summary>
    /// The exact moment the old code broke: 300s elapsed, but the set moved 2s ago.
    /// Duration says "timeout"; activity says "still arriving". Activity is the correct answer.
    /// </summary>
    [Fact]
    public void ElapsedBudgetAloneNoLongerEndsTheWait()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.KeepWaiting,
            VolumeSetArrivalBudget.Evaluate(
                msSinceLastProgress: 2000, msElapsedTotal: Budget, noProgressBudgetMs: Budget));
    }

    /// <summary>
    /// The behaviour that must NOT change: a first volume whose set never moves at all still
    /// gives up after exactly the old budget, because the wait samples the set from tick one.
    /// </summary>
    [Fact]
    public void ASetThatNeverMovesStillGivesUpAtTheOldBudget()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.KeepWaiting,
            VolumeSetArrivalBudget.Evaluate(Budget - 1, Budget - 1, Budget));

        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.Stalled,
            VolumeSetArrivalBudget.Evaluate(Budget, Budget, Budget));
    }

    /// <summary>A stall that begins after a long arrival is still a stall.</summary>
    [Fact]
    public void StallAfterALongArrivalIsDetected()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.Stalled,
            VolumeSetArrivalBudget.Evaluate(
                msSinceLastProgress: Budget, msElapsedTotal: 845_000 + Budget, noProgressBudgetMs: Budget));
    }

    /// <summary>The caller-supplied budget is honoured, not silently replaced by the constant.</summary>
    [Theory]
    [InlineData(60_000)]
    [InlineData(600_000)]
    public void CallerSuppliedInactivityBudgetIsHonoured(long budget)
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.KeepWaiting,
            VolumeSetArrivalBudget.Evaluate(budget - 1, 10 * budget, budget));

        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.Stalled,
            VolumeSetArrivalBudget.Evaluate(budget, 10 * budget, budget));
    }

    /// <summary>The ceiling still bounds a set that grows forever, and outranks inactivity.</summary>
    [Fact]
    public void CeilingStillTerminatesAnEndlessArrival()
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Verdict.CeilingReached,
            VolumeSetArrivalBudget.Evaluate(
                msSinceLastProgress: 2000,
                msElapsedTotal: VolumeSetArrivalBudget.AbsoluteCeilingMs,
                noProgressBudgetMs: Budget));
    }

    /// <summary>The default overload must keep meaning exactly what it meant before.</summary>
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(2000L, 845_000L)]
    [InlineData(Budget, 60 * 60_000L)]
    [InlineData(2000L, VolumeSetArrivalBudget.AbsoluteCeilingMs)]
    public void DefaultOverloadDelegatesToTheConstantBudget(long sinceProgress, long elapsed)
    {
        Assert.Equal(
            VolumeSetArrivalBudget.Evaluate(sinceProgress, elapsed, Budget),
            VolumeSetArrivalBudget.Evaluate(sinceProgress, elapsed));
    }
}

/// <summary>
/// Regression cover for the second half of the v3.10.76 fix: the readiness gate answered with
/// a bare <c>bool</c>, so its caller could not tell a stall from the twelve-hour ceiling and
/// burned a bounded watch retry on both. <c>VolumeSetArrivalBudget.DeservesRetry</c> was
/// written for exactly that distinction in v3.10.73 and had ZERO production callers — only a
/// unit test asserted it, which made the gap look covered.
/// </summary>
public sealed class ArchiveWaitOutcomeTests
{
    [Fact]
    public void StalledIsTheOnlyOutcomeThatConsumesARetry()
    {
        Assert.True(ArchiveWait.DeservesRetry(ArchiveWaitOutcome.Stalled));
        Assert.False(ArchiveWait.DeservesRetry(ArchiveWaitOutcome.CeilingReached));
        Assert.False(ArchiveWait.DeservesRetry(ArchiveWaitOutcome.Ready));
    }

    [Fact]
    public void TerminalVerdictsMapOntoOutcomes()
    {
        Assert.Equal(
            ArchiveWaitOutcome.Stalled,
            ArchiveWait.FromVerdict(VolumeSetArrivalBudget.Verdict.Stalled));

        Assert.Equal(
            ArchiveWaitOutcome.CeilingReached,
            ArchiveWait.FromVerdict(VolumeSetArrivalBudget.Verdict.CeilingReached));
    }

    /// <summary>
    /// KeepWaiting is not an ending. Mapping it silently onto a terminal outcome is how a
    /// "still arriving" set would be reported as finished.
    /// </summary>
    [Fact]
    public void KeepWaitingIsNotATerminalOutcome()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArchiveWait.FromVerdict(VolumeSetArrivalBudget.Verdict.KeepWaiting));
    }

    /// <summary>
    /// The agreement that must hold between the two layers: an outcome consumes a retry exactly
    /// when the verdict it came from does. If these ever diverge the caller is acting on a
    /// different rule than the budget documents.
    /// </summary>
    [Theory]
    [InlineData(VolumeSetArrivalBudget.Verdict.Stalled)]
    [InlineData(VolumeSetArrivalBudget.Verdict.CeilingReached)]
    public void OutcomeRetryRuleAgreesWithTheBudgetVerdict(VolumeSetArrivalBudget.Verdict verdict)
    {
        Assert.Equal(
            VolumeSetArrivalBudget.DeservesRetry(verdict),
            ArchiveWait.DeservesRetry(ArchiveWait.FromVerdict(verdict)));
    }
}
