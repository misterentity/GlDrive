namespace GlDrive.Downloads;

/// <summary>
/// Identity of a multi-volume archive set by what a retry could actually change: how many
/// parts are present and how many bytes they hold.
///
/// Root cause this exists for (observed 2026-08-18, GAZPROM 2160p UHD set, 83 parts / 66 GB):
/// the fingerprint was computed as <c>volumes.Sum(v =&gt; v.Length)</c> over a list whose FIRST
/// entry was constructed unconditionally from the caller's path — <c>new FileInfo(firstVolume)</c>
/// — whether or not that file existed. For old-style <c>.rar/.r00/.r01…</c> sets arriving over
/// FXP the <c>.rar</c> sorts AFTER every <c>.rNN</c>, so it lands LAST: for most of the arrival
/// window there are 82 real parts and no <c>.rar</c>. Reading <c>.Length</c> off the phantom
/// threw, the whole computation fell into its catch, and it returned the <c>(-1,-1)</c>
/// "cannot read" sentinel.
///
/// That sentinel then flowed into <see cref="TransientAbandonLedger"/>, whose contract is
/// "a fingerprint that differs from the record means retry" — so an UNREADABLE fingerprint
/// unparked the archive on every single evaluation. 8 of the 11 revivals logged that day read
/// literally <c>changed (-1 parts, -1 bytes)</c>; the 3 genuine ones read 21 → 53 → 83 parts.
/// An invariant magnitude across every occurrence is a fixed sentinel, not live data.
///
/// Two lessons are encoded here:
///   * A part that vanished between enumeration and measurement must degrade the fingerprint
///     (contribute nothing), never discard it. One racy <c>.Length</c> on a set being written
///     is the normal case, not an exceptional one.
///   * "Unknown" is a third answer, not a magic pair of numbers. <see cref="IsKnown"/> makes
///     every consumer state which of the three it is handling, so no consumer can silently
///     read "unknown" as "changed" again.
/// </summary>
public readonly record struct VolumeSetFingerprint(int VolumeCount, long TotalBytes)
{
    /// <summary>
    /// The set could not be observed at all (the directory is gone, or enumeration threw).
    /// Deliberately NOT a value any real set can take, so it can never compare equal to one.
    /// </summary>
    public static readonly VolumeSetFingerprint Unknown = new(-1, -1);

    /// <summary>An empty-but-observed set — the path exists nowhere, which is a real answer.</summary>
    public static readonly VolumeSetFingerprint Absent = new(0, 0);

    /// <summary>False only for <see cref="Unknown"/>. Consumers MUST branch on this first.</summary>
    public bool IsKnown => VolumeCount >= 0 && TotalBytes >= 0;

    /// <summary>
    /// Fold per-volume observations into a fingerprint. A volume that does not exist (or whose
    /// length could not be read) contributes neither to the count nor to the bytes, so the
    /// phantom first volume that caused the 2026-08-18 loop simply drops out and the remaining
    /// 82 real parts still produce a usable, monotonically-growing fingerprint.
    /// </summary>
    public static VolumeSetFingerprint FromVolumes(IEnumerable<long?> volumeLengths)
    {
        var count = 0;
        long bytes = 0;

        foreach (var length in volumeLengths)
        {
            if (length is not long n || n < 0) continue;
            count++;
            bytes += n;
        }

        return new VolumeSetFingerprint(count, bytes);
    }
}
