namespace GlDrive.Downloads;

/// <summary>
/// Decides whether a multi-volume archive set has finished arriving.
///
/// Root cause this exists for (observed 2026-08-14): the watch folder gated extraction on
/// <c>WaitForFileReady(path)</c> — but <c>path</c> is only the FIRST volume. SharpCompress
/// opens the whole set through <c>SourceStream.LoadAllParts()</c>, so extraction began the
/// moment <c>name.rar</c> stopped growing, while <c>name.r22</c>, <c>.r21</c>, <c>.r20</c> …
/// were still being written. The readiness invariant is a property of the volume SET; it was
/// being enforced on one member of it.
///
/// Three separate log clusters were the same defect:
///   * <c>IOException "…\.r22 is being used by another process"</c> — the honest symptom,
///     retried five times over ~2.5 min (far less than a 2160p set takes to land) and then
///     abandoned non-durably, so every app restart replayed the whole cycle.
///   * <c>"unpacked file size does not match header: expected 16924333715 found 1994329334"</c>
///     — not corruption, a set that was 2 GB into a 16.9 GB download. Ruled Permanent and
///     recorded DURABLY.
///   * <c>"UnRAR.exe failed (exit 3)"</c> (CRC) — the natural result of reading a half-written
///     volume, likewise ruled Permanent.
///
/// The last two mean the classifier issued "unrecoverable" verdicts about files that were
/// merely incomplete at the time. <see cref="ExtractAbandonStore"/>'s fingerprint let them
/// lapse once the download finished, which is why this never became permanent damage — but
/// the work, the log noise, and the false verdicts were all avoidable.
///
/// A set is ready when nothing about it is still moving: no volume is write-locked, and
/// neither the volume count nor the total byte count changed between two consecutive
/// samples. The count check matters as much as the bytes — sampling while parts 1-5 exist
/// and parts 6-30 have not started yet would otherwise look perfectly settled.
/// </summary>
public static class VolumeSetReadiness
{
    /// <summary>
    /// One observation of a volume set. <paramref name="LockedCount"/> is how many members
    /// could not be opened for exclusive read, i.e. are still being written.
    /// </summary>
    public readonly record struct Snapshot(int Count, long TotalBytes, int LockedCount);

    /// <summary>
    /// True when <paramref name="current"/> shows a set that has stopped changing and is
    /// fully unlocked, and <paramref name="previous"/> agrees on its shape.
    ///
    /// An empty set is never ready: zero volumes means discovery raced the first write, and
    /// treating that as settled would hand SharpCompress an archive with no parts.
    /// </summary>
    public static bool IsReady(Snapshot previous, Snapshot current)
    {
        if (current.Count <= 0) return false;
        if (current.LockedCount > 0) return false;

        return current.Count == previous.Count
            && current.TotalBytes == previous.TotalBytes;
    }

    /// <summary>
    /// True when the set is still actively arriving — a volume is locked, or the set grew
    /// between samples. Callers use this to distinguish "not finished yet" (keep waiting,
    /// the input is fine) from "waited the full budget and it never settled" (a genuine
    /// stall worth a retry slot). Keeping those apart is what stops a slow download from
    /// burning the bounded retry budget that exists for real faults.
    /// </summary>
    public static bool IsStillArriving(Snapshot previous, Snapshot current)
    {
        if (current.LockedCount > 0) return true;

        return current.Count != previous.Count
            || current.TotalBytes != previous.TotalBytes;
    }
}
