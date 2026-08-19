namespace GlDrive.Downloads;

/// <summary>
/// The per-tick decision behind "wait until this file has stopped being written to".
///
/// Extracted from <c>ExtractorWindow.WaitForFileReady</c> on 2026-08-18 because the defect it
/// carried was invisible from outside: a file that did not exist yet returned "give up"
/// immediately, and the caller then reported a TIMEOUT. Across 2026-08-16..18 the message
/// "archive was not ready before timeout" appeared 69 times and the genuine timeout message
/// ("Timeout waiting for file to be ready") appeared ZERO times — every one of them was this
/// early exit wearing the timeout's clothes, at 2.008 s of a 300 s budget. The detect→warn gap
/// was 2.008 s on every single occurrence; an invariant magnitude is a structural exit, not a
/// wait that ran long.
///
/// Why "not there yet" is the NORMAL state rather than an error: for old-style multi-volume
/// sets (<c>.rar</c> + <c>.r00</c>, <c>.r01</c>, …) the first volume sorts AFTER every
/// continuation part, so over FXP it lands LAST. A watcher event on any <c>.rNN</c> resolves to
/// a <c>.rar</c> that will not exist for most of the arrival. Six of those instant exits burned
/// the whole retry budget in about two minutes of what was a 90-minute arrival.
/// </summary>
public static class FileArrivalGate
{
    /// <summary>What the polling loop should do after one observation.</summary>
    public enum Decision
    {
        /// <summary>Not settled. Keep polling until the budget runs out.</summary>
        KeepWaiting,

        /// <summary>Size has held steady long enough — try the exclusive open that confirms it.</summary>
        ConfirmWithExclusiveOpen,
    }

    /// <summary>Consecutive stable observations required before attempting the exclusive open.</summary>
    public const int RequiredStableObservations = 2;

    /// <summary>
    /// Decide from one observation, and fold the running stability count.
    ///
    /// <paramref name="exists"/> false yields <see cref="Decision.KeepWaiting"/> with the count
    /// reset — never a terminal answer. Only budget exhaustion, which the caller owns, ends the
    /// wait unsuccessfully. That separation is the fix: this function cannot express "give up",
    /// so it cannot be mistaken for a timeout again.
    /// </summary>
    public static Decision Observe(bool exists, long currentSize, ref long lastSize, ref int stableCount)
    {
        if (!exists)
        {
            // Whatever we measured before describes a file that is not currently there, so the
            // stability run is void — not merely paused.
            stableCount = 0;
            lastSize = -1;
            return Decision.KeepWaiting;
        }

        // A zero-byte file is a created-but-unwritten placeholder, which is exactly how an
        // external copy begins. Stable at zero is not settled.
        if (currentSize == lastSize && currentSize > 0) stableCount++;
        else stableCount = 0;

        lastSize = currentSize;

        return stableCount >= RequiredStableObservations
            ? Decision.ConfirmWithExclusiveOpen
            : Decision.KeepWaiting;
    }
}
