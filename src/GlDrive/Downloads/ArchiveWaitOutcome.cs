namespace GlDrive.Downloads;

/// <summary>
/// How a wait for an archive to finish arriving ended.
///
/// Root cause this exists for (observed 2026-08-21, on the v3.10.75 build): the readiness gate
/// answered with a <c>bool</c>, so every unsuccessful end looked identical to its caller. Two
/// separate defects hid inside that single <c>false</c>:
///
///   * <see cref="VolumeSetArrivalBudget.DeservesRetry"/> was written to say that hitting the
///     twelve-hour ceiling must NOT consume one of the five bounded watch retries — retrying a
///     set that grew for twelve hours re-reads tens of GB for nothing. It had ZERO production
///     callers; only a unit test asserted it. A predicate nobody branches on is the same shape
///     as the v3.10.73 defect it was written during, and the passing test made the gap look
///     covered.
///   * the caller logged one message ("archive was not ready before timeout") for a genuine
///     stall and for the ceiling alike, so the log could not distinguish them either.
///
/// Reporting the reason instead of a bare bool is what lets the caller act on the distinction
/// the budget already draws.
/// </summary>
public enum ArchiveWaitOutcome
{
    /// <summary>The archive — and, for a multi-volume set, every part of it — has settled.</summary>
    Ready,

    /// <summary>Nothing changed for the inactivity budget. A real stall; a retry may clear it.</summary>
    Stalled,

    /// <summary>Growth never stopped and the absolute ceiling was reached. Retrying cannot help.</summary>
    CeilingReached,
}

/// <summary>Maps a budget verdict onto the outcome a caller acts on.</summary>
public static class ArchiveWait
{
    /// <summary>
    /// Translate the terminal verdicts. <see cref="VolumeSetArrivalBudget.Verdict.KeepWaiting"/>
    /// is not terminal and must never reach here.
    /// </summary>
    public static ArchiveWaitOutcome FromVerdict(VolumeSetArrivalBudget.Verdict verdict) => verdict switch
    {
        VolumeSetArrivalBudget.Verdict.Stalled => ArchiveWaitOutcome.Stalled,
        VolumeSetArrivalBudget.Verdict.CeilingReached => ArchiveWaitOutcome.CeilingReached,
        _ => throw new ArgumentOutOfRangeException(
            nameof(verdict), verdict, "KeepWaiting is not a terminal verdict"),
    };

    /// <summary>
    /// True when this ending should consume one of the bounded watch retries. Only a genuine
    /// stall might clear on a retry.
    /// </summary>
    public static bool DeservesRetry(ArchiveWaitOutcome outcome) => outcome == ArchiveWaitOutcome.Stalled;
}
