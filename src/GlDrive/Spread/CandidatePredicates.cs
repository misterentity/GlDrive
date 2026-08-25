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

    /// <summary>
    /// A section blacklist records that a site refused to RECEIVE a section. It is a
    /// DESTINATION rule and must never cost a site its SOURCE role: a site you cannot
    /// upload to is still a perfectly good site to download FROM. Indeed superbnc is
    /// upload-restricted by design, so it accumulates exactly these entries.
    ///
    /// Dropping a blacklisted site from the race participant list conflated the two
    /// roles and silently deleted the only site holding the release. On 2026-08-03 that
    /// cost 375 of 377 [tv-hd] races — the whole category — because a stale entry
    /// (".imdbinfoname: path-filter denied", written by the v3.10.44 metadata bug)
    /// removed superbnc, the source, so Phase 1 never probed it and every race reported
    /// "Release not found on any server".
    ///
    /// Destination exclusion is enforced where it belongs — SpreadJob's Phase 2 dest
    /// selection — which also handles the fill-only case and emits an actionable
    /// message. Participant selection must not pre-empt it.
    /// </summary>
    internal const bool BlacklistExcludesSourceRole = false;

    /// <summary>
    /// Whether a site can ever receive this release before any FTP work begins.
    /// Auto-race used to check rules and section mappings but not the three
    /// destination-only exclusions that SpreadJob applies later. In the common
    /// production shape (a download-only source plus an affil-blocked peer), that
    /// started a job which was guaranteed to fail with "No viable destinations".
    /// Keep the suffix match identical to SpreadJob's affil gate: a short group such
    /// as NOMA must match "-NOMA" only, not text in the release title.
    ///
    /// <paramref name="destinationReachable"/> MUST be live connectivity — whether the
    /// server currently has a spread pool — not merely whether it is configured and
    /// enabled. v3.10.78: the first three exclusions are all CONFIG facts, so a server
    /// that was unreachable for eleven days still counted as a viable receiver. SYN's
    /// FTP host died 2026-08-13; SpreadManager's destination preflight kept scoring it
    /// eligible, so <c>viableReceiverCount</c> was never 0 and the preflight fired once
    /// in three days while 128 jobs started and failed with the exact "No viable
    /// destinations" it exists to prevent. SpreadJob then builds its participant map
    /// from the live pool registry, which drops SYN, leaving only the affil-blocked
    /// peer. A preflight that asks "is there a destination?" of configuration alone has
    /// no evidence for the answer it gives (recurring pattern #1), and a guard sized
    /// against config eligibility rather than the contended resource — an actually
    /// connected server — is inert once the two drift apart (recurring pattern #8).
    /// </summary>
    internal static bool CanReceiveRelease(
        string releaseName,
        IEnumerable<string>? affils,
        bool downloadOnly,
        bool destinationBlacklisted,
        bool destinationReachable)
    {
        if (downloadOnly || destinationBlacklisted || !destinationReachable) return false;
        return affils == null || !affils.Any(group =>
            !string.IsNullOrWhiteSpace(group) &&
            releaseName.EndsWith($"-{group}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// May a directory scan fall back onto the dedicated FXP (spread) pool?
    ///
    /// The fallback exists so a saturated main pool can't abandon a scan outright, but
    /// it was unconditional — and a scan re-runs every ~2s while a transfer waits up to
    /// 30s to borrow, so the scan wins the permit race every single time. On 2026-08-09
    /// that inverted the whole engine against zephyr (loginCap 4 = main 1 + spread 3, no
    /// headroom): 1,393 "main pool exhausted, falling back to spread pool", 2,779 FXP
    /// borrow timeouts, 1,464 failed dest scans — against ONE FXP transfer error and TWO
    /// MKD failures all day. Both sites were healthy; the engine was starving itself.
    /// 321 races delivered files in 4.
    ///
    /// So: scans are best-effort, transfers are the point. A scan may use the spread pool
    /// only while at least one permit would remain for an FXP borrow. Yielding is safe and
    /// self-correcting — the scan retries next cycle, and a cycle with no transfer in
    /// flight always has headroom, so this cannot deadlock (transfers need the SOURCE
    /// scan's file list, which is gated the same way and runs when the pool is idle).
    ///
    /// The original recovery case still works: when the main pool is unusable (dead or
    /// fully exhausted, not merely busy) the scan takes the spread pool regardless, since
    /// otherwise it would never run at all.
    ///
    /// <paramref name="spreadUsableMax"/> MUST be the pool's <c>EffectiveMaxSize</c> — the
    /// count of logins the account gate will actually grant — not its nominal
    /// <c>MaxSize</c>. v3.10.54: passing MaxSize made this guard inert. It was authored
    /// when spreadPoolSize was 3 against 3 usable logins, where the two numbers agreed;
    /// production later ran spreadPoolSize 10 against a gate granting 1, so
    /// <c>10 - active >= 2</c> was true on all 534 evaluations of 2026-08-10 and the scan
    /// yielded exactly 0 times. A capacity number that isn't the contended resource cannot
    /// answer "is there room" (recurring pattern #1).
    /// </summary>
    internal static bool ScanMayBorrowSpreadPool(int spreadActive, int spreadUsableMax, bool mainPoolUsable)
        => !mainPoolUsable || spreadUsableMax - spreadActive >= 2;

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
    /// How many permanent MKD denials on a dirscript-denied path we tolerate from a
    /// dest whose release dir a scan has "confirmed", before we stop believing the
    /// confirmation. A confirmation is evidence, not a permanent exemption: the dir
    /// can vanish site-side (nuke/cleanup) between the scan and the transfer, and the
    /// dest may be denied MKD on a deeper component the scan never proved existed.
    /// Small on purpose — these denials are deterministic, so a handful is already
    /// conclusive proof the override is wrong.
    /// </summary>
    internal const int MaxMkdDenialsWithDirConfirmed = 3;

    /// <summary>
    /// Full dirscript gate including the fill-only "dir confirmed" override.
    /// True ⇒ skip this dest.
    ///
    /// A dest whose base path was MKD/dirscript-denied this race is normally skipped.
    /// The override lets a fill-only dest back in once a scan confirms the release dir
    /// exists (another racer created it) — CWD then succeeds and no MKD is needed.
    ///
    /// The override is BOUNDED. <c>_destDirConfirmed</c> was previously add-only and
    /// unbounded, so a stale confirmation overrode the denial forever: on 2026-07-27
    /// superbnc was confirmed at 07:35 (34 files), the dir was removed site-side by
    /// 07:37, and the race then re-attempted MKD on a path superbnc may not create
    /// 278 times in 29 minutes. Counting the dest's own denials makes that impossible
    /// while preserving the genuine fill-only case (which costs zero denials).
    /// </summary>
    internal static bool DirscriptBlockedAfterOverride(
        string dstBasePath, IEnumerable<string>? deniedPrefixes,
        bool dirConfirmed, int mkdDenialCount)
    {
        if (!DirscriptBlocked(dstBasePath, deniedPrefixes)) return false;
        return !(dirConfirmed && mkdDenialCount < MaxMkdDenialsWithDirConfirmed);
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

    /// <summary>
    /// What the Phase-1 source sweep is actually entitled to conclude.
    ///
    /// The sweep probes every candidate section path on every server. A probe can
    /// end three ways: the dir is there, the site says it isn't, or we never got an
    /// answer (borrow timeout, pool cooldown, dead control channel). The first two
    /// are evidence; the third is not. Collapsing "no answer" into "not there" is
    /// what let one transient connection failure park a live release for an hour.
    ///
    /// Absence is only provable by a probe that came back. See
    /// <see cref="SourceProbeVerdict"/>.
    /// </summary>
    /// <param name="serversWithRelease">Servers confirmed to hold the release.</param>
    /// <param name="pathsAnsweredCleanly">Probes that returned a definitive yes/no.</param>
    /// <param name="pathsErrored">Probes that threw before returning anything.</param>
    internal static SourceProbeVerdict ClassifySourceProbe(
        int serversWithRelease, int pathsAnsweredCleanly, int pathsErrored)
    {
        if (serversWithRelease > 0) return SourceProbeVerdict.Found;
        if (pathsAnsweredCleanly > 0) return SourceProbeVerdict.Absent;
        // Nothing came back — including the "nothing was probed at all" case
        // (no sections configured, or no pool for any server). Either way we
        // never asked, so we cannot say the release is missing.
        _ = pathsErrored;
        return SourceProbeVerdict.Inconclusive;
    }
}

/// <summary>Outcome of the Phase-1 source-discovery sweep in <see cref="SpreadJob"/>.</summary>
internal enum SourceProbeVerdict
{
    /// <summary>At least one server holds the release.</summary>
    Found,

    /// <summary>At least one probe answered, and every answer was "not here".</summary>
    Absent,

    /// <summary>No probe returned an answer. Says nothing about the release.</summary>
    Inconclusive,
}
