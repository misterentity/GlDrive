namespace GlDrive.Downloads;

/// <summary>
/// How the watch-folder auto-extractor should react to a failed extraction attempt.
/// </summary>
public enum ExtractFailureKind
{
    /// <summary>Worth retrying — the file was locked, still copying, or a one-off I/O fault.</summary>
    Transient,

    /// <summary>Retrying cannot help while the folder is unchanged — abandon the path.</summary>
    Permanent,
}

/// <summary>
/// Classifies extraction failures so the watch folder stops re-running hopeless work.
///
/// Root cause this exists for (observed 2026-07-21): two watched folders each held a
/// leftover first volume whose sibling parts had already been deleted after a prior
/// successful extraction. Every failure was scheduled for another retry, so an 86 GB
/// volume set was re-read five times per cycle — and because the give-up path did not
/// mark the file durably, the next watcher event restarted the whole cycle. The archive
/// is missing volumes; no amount of waiting changes that, so it must be abandoned on the
/// first failure rather than retried.
/// </summary>
public static class ExtractFailureClassifier
{
    // SharpCompress / UnRAR wording for "this volume set is not complete".
    private static readonly string[] PermanentMarkers =
    {
        "expects a new volume",
        "multi-part rar file is incomplete",
        "is incomplete",
        "cannot find volume",
        "next volume is missing",
        "missing volume",
        "unexpected end of archive",
        "no files to extract",

        // SharpCompress wording for "the archive body is shorter than its own header says",
        // i.e. truncated or missing payload. Safe to call permanent because the watch path
        // only reaches extraction through WaitForFileReady, which requires the size to stop
        // changing AND an exclusive open — so a still-copying file cannot produce this.
        // Observed 2026-07-21..22 on hackers.1995...-GAZER: "expected 16924333715 found
        // 1994329334", retried 5x per app restart, re-reading ~2 GB each time, for two days.
        "unpacked file size does not match header",
    };

    /// <summary>
    /// UnRAR.exe exit codes that no retry can clear. 3 = CRC error (the data is corrupt) and
    /// 11 = wrong password; both are properties of the input, not of timing. Other non-zero
    /// codes (notably 6, open error) can be a transient lock, so they keep their retries.
    /// Observed alongside the marker above: "UnRAR.exe failed (exit 3)" looping every restart.
    /// </summary>
    private static readonly string[] PermanentUnrarExits =
    {
        "unrar.exe failed (exit 3)",
        "unrar.exe failed (exit 11)",
    };

    // Corrupt/encrypted input is also not fixed by waiting, but it is reported very
    // differently and a truncated still-copying file can look identical, so those stay
    // transient on purpose — WaitForFileReady already covers the copying case.
    private static readonly string[] TransientMarkers =
    {
        "being used by another process",
        "access is denied",
        "sharing violation",
        "the process cannot access",
    };

    /// <summary>
    /// Decide whether <paramref name="message"/> describes a failure that a later retry
    /// could plausibly resolve. Unknown messages are treated as transient so genuinely
    /// flaky I/O keeps its retries.
    /// </summary>
    public static ExtractFailureKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return ExtractFailureKind.Transient;

        // Checked before TransientMarkers: an unrecoverable exit code is conclusive even if
        // the surrounding text happens to mention a lock.
        foreach (var marker in PermanentUnrarExits)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return ExtractFailureKind.Permanent;

        foreach (var marker in TransientMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return ExtractFailureKind.Transient;

        foreach (var marker in PermanentMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return ExtractFailureKind.Permanent;

        return ExtractFailureKind.Transient;
    }

    /// <summary>Convenience overload that also inspects inner exceptions.</summary>
    public static ExtractFailureKind Classify(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (Classify(e.Message) == ExtractFailureKind.Permanent)
                return ExtractFailureKind.Permanent;

        return ExtractFailureKind.Transient;
    }
}
