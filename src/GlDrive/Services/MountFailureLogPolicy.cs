namespace GlDrive.Services;

/// <summary>
/// How loudly a repeated mount failure should be logged (v3.10.50).
///
/// v3.10.49's <see cref="MountRetryPolicy"/> retries a failed mount forever at a 5
/// minute cap — correct, but every attempt wrote TWO full stack traces (an ERR from
/// <c>MountService.Mount</c> and a WRN from <c>ServerManager.RemountLoop</c>). With
/// two sites down on 2026-08-07 that was 7,244 of 16,084 lines — 45% of the day's
/// log — for a condition whose first occurrence had already said everything useful.
/// This is the same failure as v3.10.47, where 902 expected MKD denials logged at
/// WRN+stack rolled the log past its cap and evicted a day of history. Volume is not
/// free: it destroys the evidence you need for the NEXT diagnosis.
///
/// So: keep warning-level full detail while the failure is still news (and whenever
/// the error CHANGES, which is the genuinely diagnostic moment), then fall back to an
/// information-level one-line summary. At the 5 minute cap, every-12th gives one
/// warning with detailed evidence per hour.
/// </summary>
public static class MountFailureLogPolicy
{
    /// <summary>Attempts at the start of a failure streak that always log in full.</summary>
    public const int VerboseAttempts = 3;

    /// <summary>After the opening burst, one attempt in this many logs in full.</summary>
    public const int PeriodicInterval = 12;

    /// <summary>
    /// Whether retry <paramref name="attempt"/> (1-based) should log the exception with
    /// its stack, rather than a compact one-liner.
    /// </summary>
    public static bool ShouldLogFullDetail(int attempt)
    {
        if (attempt < 1) attempt = 1;
        if (attempt <= VerboseAttempts) return true;
        return attempt % PeriodicInterval == 0;
    }

    /// <summary>
    /// Whether retry <paramref name="attempt"/> should log in full, treating a changed
    /// error as newsworthy regardless of position in the streak. <paramref name="previousError"/>
    /// is the prior attempt's message (null on the first attempt).
    /// </summary>
    public static bool ShouldLogFullDetail(int attempt, string? previousError, string? currentError)
    {
        if (!string.Equals(previousError, currentError, StringComparison.Ordinal)) return true;
        return ShouldLogFullDetail(attempt);
    }
}
