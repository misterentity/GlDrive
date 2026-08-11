namespace GlDrive.Spread;

/// <summary>
/// Splits a failed dest scan into "we were busy" and "something is broken".
///
/// v3.10.54. `Spread scan FAILED (both pools unavailable)` logged WRN + a full stack on
/// every occurrence. On 2026-08-10, 522 of its 527 occurrences (99.1%) were a borrow that
/// timed out or a pool that refused to dial — no I/O was ever attempted. That is exactly
/// the state the branch above it logs at INF ("main pool exhausted ... falling back") and
/// exactly what a deliberate yield logs at INF; only the third spelling screamed.
///
/// The cost is not cosmetic. At ~9 lines each that was 8% of a quiet day's log, and
/// 1,874/day on 2026-08-09 rolled the 10 MB cap at 13:52 and split the day in half —
/// leaving this sweep ~1.5 days of history to trend against. Log volume destroys the
/// evidence needed to diagnose the NEXT bug (the v3.10.47 lesson), so severity has to
/// track breakage rather than author surprise (recurring pattern #5).
///
/// The test is the property that DEFINES contention — the scan never obtained a
/// connection, because a permit, a cooldown, or a cancellation said no before any
/// command went out — rather than a list of exception types observed once (pattern #4).
/// Anything else keeps its WRN and its stack: a demoted real fault is how a regression
/// goes unnoticed.
/// </summary>
internal static class ScanFailureClassifier
{
    /// <summary>
    /// True when the scan failed purely because no login was available. Such failures are
    /// self-correcting (the scan re-runs next cycle) and are already accounted for
    /// elsewhere — the pool logs a cooldown once when it enters one, and the FXP borrow
    /// timeout carries the full gate counters.
    /// </summary>
    internal static bool IsContention(System.Exception? ex)
    {
        // No exception is not evidence of contention (recurring pattern #1).
        if (ex == null) return false;

        // A borrow that timed out or was cancelled never opened a connection.
        // TaskCanceledException derives from OperationCanceledException.
        if (ex is System.OperationCanceledException) return true;

        // The pool declined to dial: a BNC refusal cooldown, or the account gate having
        // no permit to give. Both are the pool doing its job, and both already logged.
        if (ex is System.InvalidOperationException
            && (ex.Message.Contains("BNC cooldown", System.StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("login cap reached", System.StringComparison.OrdinalIgnoreCase)))
            return true;

        // A contention cause wrapped by a caller is still contention.
        return ex.InnerException != null && IsContention(ex.InnerException);
    }
}
