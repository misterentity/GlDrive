namespace GlDrive.Spread;

/// <summary>
/// Pure, individually-testable skip predicates extracted from
/// <see cref="SpreadJob.FindBestTransfer"/> (v3.6 Phase 3b). These encode the
/// scheduler's retry-cap / backoff / dirscript policy — the magic numbers and
/// matching rules that protect logins and avoid futile re-tries. Extracting them
/// here gives the spread scheduler real unit coverage without disturbing the hot
/// loop's iteration order or score tie-break (which stay inline in FindBestTransfer
/// for behavior preservation). Every method is a pure function of its arguments.
/// </summary>
internal static class CandidatePredicates
{
    /// <summary>cbftp MAX_SINGLE_PAIR_FILE_TRANSFER_ATTEMPTS — drop a (file,src,dst)
    /// route after this many failures.</summary>
    internal const int PairRetryCap = 4;

    /// <summary>cbftp MAX_TRANSFER_ATTEMPTS_BEFORE_SKIP — drop a file entirely after
    /// this many failures summed across ALL its routes.</summary>
    internal const int FileRetryCap = 7;

    /// <summary>True if this exact (file,src,dst) pair has failed enough to drop.</summary>
    internal static bool PairRetryCapped(int pairFailures) => pairFailures >= PairRetryCap;

    /// <summary>True if this file has failed enough across all routes to drop.</summary>
    internal static bool FileRetryCapped(int fileTotalFailures) => fileTotalFailures >= FileRetryCap;

    /// <summary>
    /// True if a destination is inside its backoff window (recently failed, parked
    /// until <paramref name="retryAt"/>). DateTime.MaxValue means dropped for the
    /// whole race — also "in backoff" (never retry). null = no backoff.
    /// </summary>
    internal static bool DestInBackoff(System.DateTime? retryAt, System.DateTime now)
        => retryAt.HasValue && now < retryAt.Value;

    /// <summary>
    /// True if any denied prefix is a prefix of <paramref name="dstBasePath"/> —
    /// dirscript already rejected this dest subtree this race, so re-MKD is futile.
    /// Case-insensitive, mirroring the inline check.
    /// </summary>
    internal static bool DirscriptBlocked(string dstBasePath, IEnumerable<string>? deniedPrefixes)
    {
        if (deniedPrefixes == null) return false;
        foreach (var denied in deniedPrefixes)
            if (dstBasePath.StartsWith(denied, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// SFV-first gate: until the .sfv reaches a dest, only .sfv/.nfo files may be
    /// sent to it (glftpd zipscript needs the SFV first). True ⇒ block this file.
    /// </summary>
    internal static bool SfvFirstBlocked(string fileName, bool destStillNeedsSfv)
    {
        if (!destStillNeedsSfv) return false;
        return !fileName.EndsWith(".sfv", System.StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".nfo", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if either side is at its concurrent-slot ceiling.</summary>
    internal static bool SlotsFull(int dstActive, int dstMaxUpload, int srcActive, int srcMaxDownload)
        => dstActive >= dstMaxUpload || srcActive >= srcMaxDownload;
}
