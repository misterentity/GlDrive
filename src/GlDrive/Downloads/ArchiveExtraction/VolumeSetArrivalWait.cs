using System.IO;
using Serilog;

namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>The readiness waits every route into extraction passes before touching an archive.</summary>
internal static class VolumeSetArrivalWait
{
    /// <summary>
    /// Wait until an ENTIRE multi-volume set has finished arriving, not just the volume that
    /// triggered the watcher. SharpCompress opens every part via SourceStream.LoadAllParts(),
    /// so gating on the first volume alone let extraction start against a set whose remaining
    /// parts were still downloading — see <see cref="VolumeSetReadiness"/> for the three log
    /// clusters that produced, including two that were misreported as unrecoverable corruption.
    ///
    /// NEITHER phase is bounded by duration — see <see cref="VolumeSetArrivalBudget"/>: each
    /// ends only when the set stops changing for <paramref name="noProgressBudgetMs"/>, or at a
    /// twelve-hour ceiling. A set that is still arriving has not stalled, no matter how long it
    /// has been arriving, and calling it a timeout burned the bounded watch retries that exist
    /// for real faults.
    ///
    /// v3.10.73 converted the set-wide loop to that rule but left the first-volume phase on
    /// wall clock, which merely moved the false timeout one gate upstream: across 2026-08-21
    /// the set-wide stall count was 0 while the first-volume timeout went 0 → 2, exactly paired
    /// with the caller's "not ready before timeout". Both were false.
    /// </summary>
    internal static async Task<ArchiveWaitOutcome> WaitForVolumeSetReady(
        string path, CancellationToken ct, int noProgressBudgetMs = 300_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var firstVolume = await WaitForFileReady(path, ct, noProgressBudgetMs);
        if (firstVolume != ArchiveWaitOutcome.Ready) return firstVolume;

        // Every RAR goes through the sampler, including modern .partNN.rar sets and a lone
        // first part whose SFV declares later volumes. Old-style extraction discovery returns
        // null for modern sets (and on an IO failure), so it cannot authorize this shortcut.
        if (!Path.GetExtension(path).Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return ArchiveWaitOutcome.Ready;

        var previous = ArchiveVolumes.SampleVolumeSet(path);
        var waitedCycles = 0;
        var lastProgressMs = sw.ElapsedMilliseconds;
        var lastProgressLogMs = 0L;

        while (true)
        {
            await Task.Delay(2000, ct);

            var current = ArchiveVolumes.SampleVolumeSet(path);

            if (VolumeSetReadiness.IsReady(previous, current))
            {
                // Logged at INF, not DBG: production runs at Information, so a gate that
                // only whispers at Debug is a gate nobody can confirm ever fired — exactly
                // the "0 fires in 534 evaluations" blindness of the v3.10.54 yield guard.
                // One line per archive that actually had to wait, none for sets already settled.
                if (waitedCycles > 0)
                    Log.Information(
                        "Extractor: volume set settled after {Seconds}s — {Path} ({Count} parts, {Bytes} bytes)",
                        sw.ElapsedMilliseconds / 1000, path, current.Count, current.TotalBytes);

                return ArchiveWaitOutcome.Ready;
            }

            if (VolumeSetReadiness.IsStillArriving(previous, current))
            {
                waitedCycles++;
                lastProgressMs = sw.ElapsedMilliseconds;

                // At Information, and rate-limited to one line per minute: a set that arrives
                // for 90 minutes must leave evidence that the wait was deliberate, but a line
                // every two seconds would be its own kind of noise.
                if (sw.ElapsedMilliseconds - lastProgressLogMs >= 60_000)
                {
                    lastProgressLogMs = sw.ElapsedMilliseconds;
                    Log.Information(
                        "Extractor: volume set still arriving after {Seconds}s — {Path} ({Count} parts, {Bytes} bytes, {Locked} locked)",
                        sw.ElapsedMilliseconds / 1000, path, current.Count, current.TotalBytes, current.LockedCount);
                }
            }

            previous = current;

            // The wait ends on INACTIVITY, not on duration. See VolumeSetArrivalBudget: bounding
            // on total elapsed time timed out sets that were visibly still growing, and the
            // resulting retries abandoned them with the reason "no progress after 5 retries".
            var verdict = VolumeSetArrivalBudget.Evaluate(
                sw.ElapsedMilliseconds - lastProgressMs, sw.ElapsedMilliseconds, noProgressBudgetMs);

            if (verdict == VolumeSetArrivalBudget.Verdict.KeepWaiting) continue;

            if (verdict == VolumeSetArrivalBudget.Verdict.CeilingReached)
                Log.Warning(
                    "Extractor: volume set still growing after {Hours}h — giving up on {Path} ({Count} parts, {Bytes} bytes). " +
                    "This is a ceiling, not a stall; nothing here is retryable.",
                    VolumeSetArrivalBudget.AbsoluteCeilingMs / 3_600_000, path, current.Count, current.TotalBytes);
            else
                Log.Warning(
                    "Extractor: volume set stalled — no change for {Seconds}s — {Path} ({Count} parts, {Bytes} bytes, {Locked} locked, {Missing} missing)",
                    noProgressBudgetMs / 1000, path, current.Count, current.TotalBytes, current.LockedCount, current.MissingCount);

            return ArchiveWait.FromVerdict(verdict);
        }
    }

    /// <summary>
    /// Wait until a file is no longer being written to. Checks every 2 seconds for up to 5 minutes.
    /// Essential for network drives where files appear before the transfer completes.
    ///
    /// "Not there yet" is NOT a failure — it is the single most common state during an arrival,
    /// and waiting through it is the entire reason this method has a five-minute budget. Until
    /// 2026-08-18 a missing file returned false immediately, which had two consequences:
    ///   * the caller logged "archive was not ready before timeout" after 2.008s of a 300s
    ///     budget. Across three days of logs that message appeared 69 times and the real
    ///     timeout message ("Timeout waiting for file to be ready") appeared ZERO times —
    ///     every single one was this early exit wearing the timeout's clothes;
    ///   * six such exits burned the whole retry budget in about two minutes of what was a
    ///     90-minute arrival, so the set was abandoned long before it could have been ready.
    /// The trigger is structural rather than rare: for old-style .rar/.r00/.r01… sets the
    /// first volume sorts after every continuation part, so it lands LAST. A watcher event on
    /// any .rNN resolves to a .rar that does not exist yet.
    /// </summary>
    internal static async Task<ArchiveWaitOutcome> WaitForFileReady(
        string path, CancellationToken ct, int noProgressBudgetMs = 300_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSize = -1;
        int stableCount = 0;

        // The surrounding set is the evidence this wait is bounded against. It is sampled from
        // the first tick so that "nothing has changed" is measured from the start of the wait
        // and a set that never moves still gives up after exactly the old budget.
        var previousSet = ArchiveVolumes.SampleVolumeSet(path);
        var lastSetProgressMs = 0L;

        while (true)
        {
            await Task.Delay(2000, ct);

            try
            {
                var exists = File.Exists(path);
                var currentSize = exists ? new FileInfo(path).Length : 0;

                // The decision itself lives in FileArrivalGate, which cannot express "give up".
                // Only the budget below ends this wait unsuccessfully, so the caller's timeout
                // message is now true whenever it is printed.
                if (FileArrivalGate.Observe(exists, currentSize, ref lastSize, ref stableCount)
                    == FileArrivalGate.Decision.ConfirmWithExclusiveOpen)
                {
                    // Size stable for 4+ seconds — confirm with an exclusive open.
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        return ArchiveWaitOutcome.Ready;
                    }
                    catch (IOException)
                    {
                        stableCount = 0; // Still locked, keep waiting
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // Why this wait is bounded on the SET and not on this one file: for old-style
            // .rar/.r00/.r01… sets the first volume sorts after every continuation part, so it
            // lands LAST. While it is absent, this file shows no progress by construction — but
            // the set around it is visibly growing, and a set that is still arriving has not
            // stalled. v3.10.73 converted the set-wide loop below to that rule and left this
            // one on wall clock, which is the whole of what remained: on 2026-08-21 both
            // first-volume timeouts were false, and Mother.Mary.2026.2160p burned two of its
            // five watch retries before the same set settled at 845s and extracted cleanly.
            var currentSet = ArchiveVolumes.SampleVolumeSet(path);
            if (VolumeSetReadiness.IsStillArriving(previousSet, currentSet))
                lastSetProgressMs = sw.ElapsedMilliseconds;
            previousSet = currentSet;

            var verdict = VolumeSetArrivalBudget.Evaluate(
                sw.ElapsedMilliseconds - lastSetProgressMs, sw.ElapsedMilliseconds, noProgressBudgetMs);

            if (verdict == VolumeSetArrivalBudget.Verdict.KeepWaiting) continue;

            if (verdict == VolumeSetArrivalBudget.Verdict.CeilingReached)
                Log.Warning(
                    "Extractor: first volume still not settled after {Hours}h — giving up on {Path}. " +
                    "This is a ceiling, not a stall; nothing here is retryable.",
                    VolumeSetArrivalBudget.AbsoluteCeilingMs / 3_600_000, path);
            else
                Log.Warning(
                    "Timeout waiting for file to be ready: {Path} — no change to its volume set for {Seconds}s",
                    path, noProgressBudgetMs / 1000);

            return ArchiveWait.FromVerdict(verdict);
        }
    }
}
