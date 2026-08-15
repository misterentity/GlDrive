namespace GlDrive.Downloads;

/// <summary>
/// In-memory record of watched archives the extractor gave up on for reasons a later attempt
/// COULD fix — chiefly retry exhaustion while the files were still arriving. The non-durable
/// twin of <see cref="ExtractAbandonStore"/>, which handles verdicts a restart cannot change.
///
/// Root cause this exists for (reported 2026-08-15, "sometimes releases land from outside the
/// GlDrive drive and need extraction"): this was a bare add-only <c>HashSet&lt;string&gt;</c>
/// consulted before every other check in the watcher path. Once abandoned, no watcher event,
/// no folder change and no fingerprint change could revive a path — only an app restart. The
/// abandon message told the user "It will be retried after the folder changes", and that half
/// of the promise was simply not implemented.
///
/// It bit external arrivals specifically because the two routes look nothing alike:
///   * GlDrive's own downloads land in <c>.gldrive-staging-*</c> (skipped) and are MOVED into
///     place, so the watcher sees a single event for an already-complete file.
///   * An external copy is created empty and filled in place, so the watcher fires at 0 bytes
///     and the readiness budget — WaitForVolumeSetReady (300s) over 6 attempts plus
///     30/60/90/120/150s backoff, about 37 minutes — runs out mid-copy on anything large.
/// Whether a release ever extracted came down to whether the copy beat that timer.
///
/// Third time this codebase has shipped a flag with no expiry (v3.10.41 UAC decline stranded
/// the box 51h; v3.10.42 <c>_destDirConfirmed</c> overrode the MKD gate forever). The fix is
/// the one the durable twin already uses: key the verdict to the volume-set fingerprint, so it
/// lapses exactly when a retry could plausibly behave differently — a part arriving, or a
/// truncated one growing — and holds firm when nothing has moved.
/// </summary>
public sealed class TransientAbandonLedger
{
    private readonly object _lock = new();

    private readonly Dictionary<string, (int VolumeCount, long TotalBytes)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record that this exact volume set was given up on.</summary>
    public void Abandon(string path, int volumeCount, long totalBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_lock) _entries[path] = (volumeCount, totalBytes);
    }

    /// <summary>
    /// What the caller should do with this path right now.
    ///
    /// <see cref="AbandonState.Revived"/> and <see cref="AbandonState.NotAbandoned"/> both mean
    /// "proceed", but they are NOT interchangeable: only Revived may clear the caller's
    /// in-flight bookkeeping. Collapsing them into a bare bool would clear the watcher's
    /// duplicate-event gate on every ordinary event and let an archive that is currently
    /// extracting be queued a second time.
    /// </summary>
    public enum AbandonState
    {
        /// <summary>No record — an ordinary event for a path we have not given up on.</summary>
        NotAbandoned,

        /// <summary>Given up on, and nothing about the set has changed since. Stay parked.</summary>
        StillAbandoned,

        /// <summary>Given up on, but the set has changed. The record is dropped; retry it.</summary>
        Revived,
    }

    /// <summary>
    /// Evaluate a path against its CURRENT fingerprint. A changed fingerprint drops the record,
    /// so the caller retries exactly once per genuine change — a still-growing copy revives, a
    /// stalled one stays parked.
    /// </summary>
    public AbandonState Evaluate(string path, int volumeCount, long totalBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return AbandonState.NotAbandoned;

        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var recorded)) return AbandonState.NotAbandoned;

            // The (-1,-1) sentinel from ComputeVolumeSetFingerprint means "could not read the
            // set". An unreadable fingerprint must never hold a path down, or a momentary I/O
            // blip becomes a permanent exemption — the exact failure this class exists to end.
            if (volumeCount < 0 || totalBytes < 0)
            {
                _entries.Remove(path);
                return AbandonState.Revived;
            }

            if (recorded.VolumeCount != volumeCount || recorded.TotalBytes != totalBytes)
            {
                _entries.Remove(path);
                return AbandonState.Revived;
            }

            return AbandonState.StillAbandoned;
        }
    }

    /// <summary>Convenience for callers that only need "should I stop here?".</summary>
    public bool IsAbandoned(string path, int volumeCount, long totalBytes) =>
        Evaluate(path, volumeCount, totalBytes) == AbandonState.StillAbandoned;

    /// <summary>Drop a path — used when it extracts, or when the caller forces a retry.</summary>
    public void Forget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_lock) _entries.Remove(path);
    }

    /// <summary>
    /// Snapshot of currently-abandoned paths, so the periodic sweep can re-examine only these
    /// rather than re-walking every watch folder. Bounded by the number of failures, not by
    /// library size.
    /// </summary>
    public IReadOnlyList<string> AbandonedPaths()
    {
        lock (_lock) return _entries.Keys.ToList();
    }
}
