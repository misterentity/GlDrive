namespace GlDrive.Downloads;

/// <summary>
/// How long the extractor may keep waiting for a multi-volume set that has not settled yet.
///
/// Root cause this exists for (observed 2026-08-20, on the v3.10.72 build that already carried
/// the v3.10.71 first-volume fix): <c>WaitForVolumeSetReady</c> bounded its wait on TOTAL
/// elapsed time. A 2160p set takes far longer than five minutes to land over FXP, so the budget
/// expired against sets that were demonstrably still growing — Freefall.A.Reckoning.for.Boeing
/// timed out at 09:01, 09:07 and 09:12 and then extracted cleanly at 09:13; Here.the.Whole.Time
/// timed out at 09:18, 09:23 and 09:29 and then logged "volume set settled after 138s — 26
/// parts, 12765395625 bytes" at 09:32. On the current build every timeout is this: across
/// 2026-08-20 the "not ready before timeout" and "did not settle within" counts are 6 and 6,
/// exactly paired, and the first-volume gate's own timeout message appeared ZERO times.
///
/// Two things made that worse than log noise:
///   * Each expiry calls <c>ScheduleWatchRetry</c>, and the fifth abandons the path with the
///     reason "no progress after 5 retries" — a claim contradicted by the bytes arriving every
///     two seconds. A set slower than ~25 minutes is abandoned WHILE DOWNLOADING. That is what
///     happened to the 83-part Disclosure.Day UHD set (11 abandons on 2026-08-18).
///   * <see cref="VolumeSetReadiness.IsStillArriving"/> was written precisely to separate "not
///     finished yet (keep waiting, the input is fine)" from "waited the budget and it never
///     settled (a genuine stall)", and its own summary says callers use it that way. The caller
///     did not: it incremented a counter, logged at Debug, and fell out of the loop on wall
///     clock regardless. A helper that documents a distinction nobody acts on is the same shape
///     as the v3.10.62 classifier that documented its own false premise in a comment.
///
/// The budget an extractor actually needs is one on INACTIVITY, not on duration: a set that is
/// still moving has not stalled no matter how long it has been moving. An absolute ceiling
/// still bounds the pathological case (a file being appended to forever, a watch folder pointed
/// at a live log) so nothing waits without end.
/// </summary>
public static class VolumeSetArrivalBudget
{
    /// <summary>
    /// How long the set may show NO progress before the wait is called a stall. This is the
    /// old total budget, reused with the meaning it should always have had.
    /// </summary>
    public const long NoProgressBudgetMs = 300_000;

    /// <summary>
    /// Hard ceiling on one wait regardless of progress. Twelve hours comfortably exceeds any
    /// real release arrival (the slowest observed was ~90 minutes) while still terminating a
    /// path that grows forever.
    /// </summary>
    public const long AbsoluteCeilingMs = 12 * 60 * 60 * 1000L;

    /// <summary>What the wait loop should do after one observation.</summary>
    public enum Verdict
    {
        /// <summary>Still within budget — sample again.</summary>
        KeepWaiting,

        /// <summary>Nothing has changed for <see cref="NoProgressBudgetMs"/>. A real stall.</summary>
        Stalled,

        /// <summary>Progress never stopped but <see cref="AbsoluteCeilingMs"/> was reached.</summary>
        CeilingReached,
    }

    /// <summary>
    /// Decide from the two clocks the loop keeps: how long since the set last changed, and how
    /// long the wait has run in total.
    ///
    /// The ceiling is checked first so a set that grows forever terminates rather than renewing
    /// its inactivity budget indefinitely.
    /// </summary>
    public static Verdict Evaluate(long msSinceLastProgress, long msElapsedTotal)
    {
        if (msElapsedTotal >= AbsoluteCeilingMs) return Verdict.CeilingReached;
        if (msSinceLastProgress >= NoProgressBudgetMs) return Verdict.Stalled;
        return Verdict.KeepWaiting;
    }

    /// <summary>
    /// True when a timeout should consume one of the bounded watch retries. A stall might clear
    /// on a retry; hitting the absolute ceiling after hours of continuous growth will not, and
    /// burning retries on it re-reads tens of GB per cycle for nothing.
    /// </summary>
    public static bool DeservesRetry(Verdict verdict) => verdict == Verdict.Stalled;
}
