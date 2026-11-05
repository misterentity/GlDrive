using System.IO;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads.ArchiveExtraction;

// Watch folders and automatic extraction: every route into AutoExtractItem, and the
// bookkeeping (retries, abandon verdicts, space and managed-download parking) around it.
internal sealed partial class ArchiveExtractionEngine
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _watchFolders = new();
    private bool _watchEnabled;
    // An automation-created engine can inspect settings without starting saved automation.
    // Preserve the saved preference until an explicit watch toggle/settings operation changes it.
    private bool _watchResumeDeferred;
    private readonly HashSet<string> _watchProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedDownloadParked = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadExtractionGuard _downloadGuard;

    // Fingerprint-keyed, NOT a bare HashSet. It was one until 2026-08-15, add-only and
    // consulted ahead of every other check, so a release abandoned mid-copy was dead for the
    // process lifetime — see TransientAbandonLedger for why that hit externally-landed
    // releases in particular.
    private readonly TransientAbandonLedger _watchAbandoned = new();

    // Durable twin of _watchAbandoned for verdicts a restart cannot change. The
    // in-memory set is still the fast path; this one keeps a provably-unextractable
    // volume set from being replayed on every launch (see ExtractAbandonStore).
    private readonly ExtractAbandonStore _abandonStore;
    private readonly Dictionary<string, int> _watchRetryCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchLock = new();

    // Paths held because their output drive is out of room. Keyed on the drive's free space,
    // not the volume-set fingerprint — freeing space never changes the fingerprint, so a full
    // disk parked in _watchAbandoned could only ever be revived by a restart.
    private readonly ExtractSpaceParking _spaceParked = new();

    /// <summary>
    /// How often abandoned paths are re-fingerprinted. Long enough that a multi-GB copy has
    /// moved measurably between sweeps, short enough that a release landing from outside
    /// GlDrive extracts without the user waiting on a restart.
    /// </summary>
    private static readonly TimeSpan AbandonSweepInterval = TimeSpan.FromMinutes(5);
    private int _abandonSweepStarted;

    /// <summary>Add a folder to watch. False if it is already watched.</summary>
    internal bool AddWatchFolder(string folder)
    {
        if (_watchFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return false;

        _watchFolders.Add(folder);
        SaveSettings();

        if (_watchEnabled)
            StartWatcherFor(folder);
        return true;
    }

    /// <summary>
    /// Flip watching on or off. Refuses (returns false, saves nothing) to turn on with no
    /// watch folders. Turning on scans every folder and auto-extracts what it finds.
    /// </summary>
    internal bool ToggleWatch()
    {
        _watchResumeDeferred = false;
        if (_watchEnabled)
        {
            StopAllWatchers();
            _watchEnabled = false;
        }
        else
        {
            if (_watchFolders.Count == 0)
                return false;

            _watchEnabled = true;
            StartAbandonSweep();

            // Scan existing archives in watch folders
            // Watchers and the initial scan outlive the call that enabled them. Do not
            // let an automation save attribution become their permanent identity.
            using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
            {
                foreach (var folder in _watchFolders)
                {
                    StartWatcherFor(folder);
                    _ = ScanAndAutoExtractAsync(folder);
                }
            }
        }
        SaveSettings();
        RaiseSettingsChanged();
        return true;
    }

    private void StartWatcherFor(string folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = ScanSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += OnWatchedFileCreated;
            watcher.Renamed += OnWatchedFileRenamed;
            watcher.Error += (_, args) =>
            {
                Log.Warning(args.GetException(), "FileSystemWatcher error on {Dir}; scheduling recovery scan", folder);
                _ = RecoverWatcherAsync(folder, watcher);
            };

            _watchers.Add(watcher);
            using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
                watcher.EnableRaisingEvents = true;
            Log.Information("Watching folder: {Dir}", folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start watcher for {Dir}", folder);
        }
    }

    private void StopAllWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnWatchedFileCreated(object sender, FileSystemEventArgs e) =>
        HandleWatchedFile(e.FullPath);

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs e) =>
        HandleWatchedFile(e.FullPath);

    private void HandleWatchedFile(string path) => _ = HandleWatchedFileAsync(path);

    internal async Task HandleWatchedFileAsync(string eventPath)
    {
        var path = ArchiveVolumes.ResolveFirstVolumePath(eventPath);
        if (path == null || path.Contains(".gldrive-staging-", StringComparison.OrdinalIgnoreCase)) return;
        if (ParkForManagedDownload(path)) return;

        // The release folder itself is gone (a failed download was discarded, or it was deleted
        // by hand). No retry can change that, so drop the bookkeeping rather than re-watch it
        // through five more stall windows. Checked on the folder, not the first volume: for
        // old-style sets the .rar legitimately arrives after every .rNN.
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            lock (_watchLock)
            {
                _watchProcessed.Remove(path);
                _watchRetryCounts.Remove(path);
            }
            Log.Information("Extractor: release folder no longer exists — dropping {Path}", path);
            return;
        }

        // A give-up that outlives the condition it describes is a permanent exemption. This
        // asks the ledger with the CURRENT fingerprint, so a copy that was still arriving when
        // we ran out of retries revives itself the moment it grows, and a stalled one stays put.
        var (liveVolumes, liveBytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(path);
        switch (_watchAbandoned.Evaluate(path, liveVolumes, liveBytes))
        {
            case TransientAbandonLedger.AbandonState.StillAbandoned:
                return;

            // Learned nothing this pass. Stay parked and keep the record: retrying on an
            // unreadable fingerprint is what produced the 2026-08-18 hot loop, and the record
            // survives so the set revives normally the moment it becomes observable again.
            case TransientAbandonLedger.AbandonState.Unknown:
                return;

            case TransientAbandonLedger.AbandonState.Revived:
                // Only on an actual revival may we clear this bookkeeping. Doing it on every
                // event would defeat the duplicate-event gate below and let an archive that is
                // mid-extraction be queued twice.
                lock (_watchLock)
                {
                    _watchProcessed.Remove(path);
                    _watchRetryCounts.Remove(path);
                }
                Log.Information(
                    "Extractor: retrying previously abandoned archive — volume set changed ({Count} parts, {Bytes} bytes): {Path}",
                    liveVolumes, liveBytes, path);
                break;
        }

        // Durable check, outside the in-memory set: this is what stops a restart from
        // replaying an extraction that was already proven impossible for this exact
        // volume set. ShouldSkip revives the path itself once the set changes.
        if (SkipIfAbandoned(path)) return;

        lock (_watchLock)
        {
            if (!_watchProcessed.Add(path)) return;
        }

        Log.Information("Extractor: watched archive detected — {Path}", path);

        try
        {
            var readiness = await VolumeSetArrivalWait.WaitForVolumeSetReady(path, _lifetimeCts.Token);
            if (ParkForManagedDownload(path)) return;
            if (readiness != ArchiveWaitOutcome.Ready)
            {
                // The two endings are not the same event and must not read the same in the log.
                // A stall may clear on a retry; the ceiling means the set grew for twelve hours
                // straight, and re-reading tens of GB per cycle cannot help it. ArchiveWait
                // .DeservesRetry has drawn that distinction since v3.10.73 — until v3.10.76
                // nothing in production branched on it, so the ceiling burned retries anyway.
                if (ArchiveWait.DeservesRetry(readiness))
                {
                    HandleStalledArchive(path);
                }
                else
                {
                    Log.Warning(
                        "Extractor: archive never stopped growing — abandoning {Path} without consuming a retry",
                        path);
                    AbandonWatchedPath(path, "still growing at the arrival ceiling", durable: false);
                }
                return;
            }

            await _ui.InvokeAsync(() =>
            {
                var item = Archives.FirstOrDefault(a =>
                    a.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (item?.Status is "Done" or "Extracting") return;

                if (item == null)
                {
                    item = ArchiveVolumes.CreateItem(path);
                    Archives.Add(item);
                }
                else
                {
                    item.Status = "Queued";
                    item.ErrorMessage = null;
                }

                RaiseStatusChanged();
                Log.Information("Extractor: queued for auto-extract — {File}", item.FileName);
                _ = AutoExtractItem(item);
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Extractor: failed to queue watched archive {Path}", path);
            ScheduleWatchRetry(path);
        }
    }

    private async Task ScanAndAutoExtractAsync(string folder)
    {
        try
        {
            var recursive = await _ui.InvokeAsync(() => ScanSubfolders);
            var paths = await Task.Run(() =>
            {
                var discovered = new List<string>();
                ArchiveVolumes.CollectArchivePaths(folder, recursive, discovered);
                return discovered;
            }, _lifetimeCts.Token);

            await _ui.InvokeAsync(() =>
            {
                var existing = new HashSet<string>(Archives.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                    if (existing.Add(path)) Archives.Add(ArchiveVolumes.CreateItem(path));

                foreach (var item in Archives.Where(a => a.Status == "Queued" &&
                             paths.Contains(a.FilePath, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    // The durable abandon record exists specifically to stop a restart from
                    // replaying a hopeless extraction — and THIS is the restart path. It used
                    // to run without asking, so every launch re-extracted the same three
                    // archives that were already recorded unextractable on 2026-08-13
                    // (observed again 2026-08-14 19:45, byte-identical failures). Checked
                    // before IsAlreadyExtracted because that opens the archive to compare
                    // entries — expensive on an 85-volume set, and futile on a broken one.
                    if (SkipIfAbandoned(item.FilePath)) continue;
                    if (ParkForManagedDownload(item.FilePath, item)) continue;

                    if (IsAlreadyExtracted(item))
                    {
                        item.Status = "Done";
                        item.Progress = 100;
                        lock (_watchLock) _watchProcessed.Add(item.FilePath);
                        continue;
                    }

                    lock (_watchLock)
                    {
                        if (!_watchProcessed.Add(item.FilePath)) continue;
                    }
                    _ = AutoExtractItem(item);
                }
                RaiseStatusChanged();
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Watch-folder recovery scan failed for {Dir}", folder); }
    }

    private async Task AutoExtractItem(ArchiveItem item)
    {
        if (item.Status != "Queued") return;
        if (ParkForManagedDownload(item.FilePath, item)) return;

        // THE chokepoint. v3.10.63 put this check in the two routes I knew about
        // (HandleWatchedFileAsync, ScanAndAutoExtractAsync) and shipped — and the replay
        // continued, because a THIRD route (the initial watch-folder scan) also reaches
        // here. Guarding call sites is how this defect survived two fixes; guarding the
        // operation itself is what actually ends it. Every route into extraction passes
        // through this method, so this is the only placement that cannot be bypassed.
        if (SkipIfAbandoned(item.FilePath)) return;
        if (_spaceParked.IsParked(item.FilePath)) return;

        // The SECOND invariant this chokepoint owes every route, and it was missing until
        // v3.10.120. WaitForVolumeSetReady shipped in v3.10.62/.63 wired into exactly one
        // caller — HandleWatchedFileAsync — while the initial watch-folder scan and the
        // recovery scan reached extraction directly. On 2026-09-14 09:31:28, one second
        // after launch, both startup routes ran UnRAR against incomplete UHD sets with no
        // "watched archive detected" / "still arriving" / "settled" line anywhere in the
        // log, took exit 3, and recorded the sets durably unrecoverable.
        //
        // ExtractFailureClassifier's "unpacked file size does not match header" marker is
        // Permanent ONLY on the promise that "a still-arriving set must never reach
        // extraction" — a promise written in its source and kept by one caller out of
        // three. A restart landing mid-download of a multi-volume set is routine here
        // (the updater polls every 3h), so this was live every time that coincided.
        //
        // Placed here for the same reason SkipIfAbandoned is, in the words already above:
        // guarding call sites is how this defect survived two fixes. For a set that has
        // already settled — including the watcher route, which gates early for its own
        // logging — this returns Ready after one 2s sample and logs nothing.
        // Task.Run, not a bare await: all three routes call this from the engine's context,
        // which for a bound window is the UI thread. WaitForVolumeSetReady samples the whole
        // volume set every 2s and can legitimately run for hours on a big set — on the
        // dispatcher that would freeze the window for the entire download. The watcher route
        // already ran it on a pool thread; this makes that true for all three.
        var readiness = await Task.Run(
            () => VolumeSetArrivalWait.WaitForVolumeSetReady(item.FilePath, _lifetimeCts.Token), _lifetimeCts.Token);
        if (ParkForManagedDownload(item.FilePath, item)) return;

        if (readiness != ArchiveWaitOutcome.Ready)
        {
            // Same two endings the watcher route draws, for the same reasons: a stall may
            // clear on a retry, the arrival ceiling cannot. Neither is a durable verdict —
            // an incomplete set is not a corrupt one, which is the entire defect — unless the
            // source itself marked the parts missing (HandleStalledArchive).
            if (ArchiveWait.DeservesRetry(readiness))
                HandleStalledArchive(item.FilePath);
            else
            {
                Log.Warning(
                    "Extractor: archive never stopped growing — abandoning {Path} without consuming a retry",
                    item.FilePath);
                AbandonWatchedPath(item.FilePath, "still growing at the arrival ceiling", durable: false);
            }

            return;
        }

        var gateHeld = false;

        var outputDir = GetOutputDir(item);
        var password = "";
        var overwriteIdx = 0;
        var deleteAfter = false;

        _ui.Send(() =>
        {
            password = Password;
            overwriteIdx = OverwriteMode;
            deleteAfter = DeleteAfterExtract;
        });

        // Scene sets are stored (m0), so the volume bytes are the unpacked size. A compressed
        // archive can need more; that case still ends in the disk-full catch below and parks.
        var (_, requiredBytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(item.FilePath);
        if (!_spaceParked.Fits(outputDir, requiredBytes, out var freeBytes))
        {
            ParkForSpace(item.FilePath, outputDir, requiredBytes, freeBytes);
            return;
        }

        Log.Information(
            "Extractor: auto-extract starting — {File} → {Dir} (deleteAfter={Delete})",
            item.FileName, outputDir, deleteAfter);

        try
        {
            await _extractionGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            // Admission can wait behind another archive while a managed download
            // starts or resumes. Check again before touching its files.
            if (ParkForManagedDownload(item.FilePath, item)) return;
            if (!File.Exists(item.FilePath) || IsAlreadyExtracted(item))
            {
                item.Status = "Done";
                item.Progress = 100;
                return;
            }
            item.Status = "Extracting";
            item.Progress = 0;
            await _unpacker.ExtractArchiveAsync(item.FilePath, outputDir, password, overwriteIdx, ItemProgress(item), _lifetimeCts.Token);
            item.Status = "Done";
            item.Progress = 100;

            Log.Information("Extractor: auto-extract completed — {File}", item.FileName);

            if (deleteAfter)
            {
                // Give SharpCompress and filesystem filters a brief settling period;
                // source cleanup also retries transient file-in-use failures.
                await Task.Delay(500, _lifetimeCts.Token);
                Log.Information("Extractor: deleting source archive set for {File}", item.FileName);
                if (!await Task.Run(() => DeleteSourceFiles(item.FilePath), _lifetimeCts.Token))
                    item.ErrorMessage = "Extracted, but source cleanup was incomplete";
            }
            lock (_watchLock) _watchRetryCounts.Remove(item.FilePath);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            item.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.ErrorMessage = ex.Message;
            if (ExtractSpaceParking.IsDiskFull(ex))
            {
                Log.Warning("Auto-extract failed: {File} — output drive is full", item.FileName);
                ParkForSpace(item.FilePath, outputDir, requiredBytes,
                    ExtractSpaceParking.DefaultFreeSpace(ExtractSpaceParking.RootOf(outputDir)));
            }
            else
            {
                Log.Warning(ex, "Auto-extract failed: {File}", item.FileName);
                ScheduleWatchRetry(item.FilePath, ex);
            }
        }
        finally
        {
            if (gateHeld) _extractionGate.Release();
        }

        if (!_lifetimeCts.IsCancellationRequested) await _ui.InvokeAsync(RaiseStatusChanged);
    }

    /// <summary>
    /// Hold an archive until its output drive has room. Consumes no retry and records no
    /// abandon verdict: a full disk says nothing about the archive, and only the sweep's
    /// free-space check can release it.
    /// </summary>
    private void ParkForSpace(string path, string outputDir, long requiredBytes, long? freeBytes)
    {
        var first = _spaceParked.Park(path, outputDir, requiredBytes);
        lock (_watchLock)
        {
            _watchProcessed.Add(path);
            _watchRetryCounts.Remove(path);
        }
        StartAbandonSweep();

        if (first)
            Log.Warning(
                "Extractor: waiting for disk space — {Path} needs {Need} on {Root} but only {Free} is free ({Headroom} kept in reserve). It will extract automatically once space frees.",
                path, ByteFormat.Compact(requiredBytes), ExtractSpaceParking.RootOf(outputDir),
                freeBytes is { } f ? ByteFormat.Compact(f) : "an unknown amount", ByteFormat.Compact(_spaceParked.Headroom));
    }

    /// <summary>
    /// The one place a stalled readiness wait is handled, for every route into extraction.
    ///
    /// A stall usually means a copy paused, and a retry may clear it. But when the set is
    /// missing parts AND the folder holds the site's zipscript marker saying so, the source
    /// never had them: 2026-10-13..15, a 3-of-15-part Small.Prophets folder replayed six
    /// 300s waits and 13 WRN lines on every restart. The durable verdict is keyed to the
    /// volume-set fingerprint, so it lapses the moment a missing part arrives.
    /// </summary>
    private void HandleStalledArchive(string path)
    {
        Log.Warning("Extractor: archive stalled before it was ready — {Path}", path);

        var marker = ArchiveVolumes.IncompleteAtSourceMarker(path);
        if (marker != null)
        {
            AbandonWatchedPath(path,
                $"incomplete at its source (site marker \"{marker}\") and its missing parts are not arriving",
                durable: true);
            return;
        }

        ScheduleWatchRetry(path);
    }

    private void ScheduleWatchRetry(string path, Exception? failure = null)
    {
        // A failure that a retry cannot fix (notably an incomplete multi-volume set whose
        // sibling parts are gone) must be abandoned on the first attempt. Retrying re-reads
        // the whole set — for a 2160p release that is tens of GB of pointless I/O per cycle.
        if (failure != null &&
            ExtractFailureClassifier.Classify(failure) == ExtractFailureKind.Permanent)
        {
            AbandonWatchedPath(path, $"unrecoverable ({failure.Message})", durable: true);
            return;
        }

        int attempt;
        lock (_watchLock)
        {
            _watchProcessed.Remove(path);
            _watchRetryCounts.TryGetValue(path, out attempt);
            attempt++;
            _watchRetryCounts[path] = attempt;
        }
        if (attempt > 5)
        {
            AbandonWatchedPath(path, "no progress after 5 retries");
            return;
        }
        _ = RetryWatchedFileAsync(path, attempt);
    }

    /// <summary>
    /// The single gate every path into extraction must pass: has this exact volume set
    /// already been ruled unextractable and not changed since?
    ///
    /// This is a helper rather than an inline check because it was inline, in ONE of the two
    /// callers. <see cref="HandleWatchedFileAsync"/> asked the store;
    /// <see cref="ScanAndAutoExtractAsync"/> — the startup/recovery scan — did not. So the
    /// durable record introduced in v3.10.57 to survive restarts was bypassed by the restart
    /// path itself, and every launch replayed the same hopeless work (three archives,
    /// re-confirmed 2026-08-14 19:45 with byte-identical failures against records written
    /// 2026-08-13T17:56Z).
    ///
    /// Same shape as the CPSV desync guard that lived in one caller's catch while six other
    /// borrowers went unguarded: an invariant about a RESOURCE has to sit where every caller
    /// reaches it. Any new route to AutoExtractItem must call this first.
    /// </summary>
    internal bool SkipIfAbandoned(string path)
    {
        var (volumeCount, totalBytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(path);
        if (!_abandonStore.ShouldSkip(path, volumeCount, totalBytes)) return false;

        Log.Information("Extractor: skipping {Path} — unchanged volume set already ruled unextractable", path);
        lock (_watchLock)
        {
            // A restart reloads only the durable store. Register its parked paths with
            // the live ledger too, so the sweep can see growth and the watcher can clear
            // _watchProcessed when a new or resized part makes this verdict obsolete.
            _watchAbandoned.Abandon(path, volumeCount, totalBytes);
            _watchProcessed.Add(path);
        }
        return true;
    }

    /// <summary>
    /// Stop considering a watched archive. Without this the give-up path left the file
    /// absent from <see cref="_watchProcessed"/> (ScheduleWatchRetry removes it before
    /// counting), so the next watcher event or recovery scan restarted the full retry
    /// cycle — observed looping indefinitely against the same two archives on 2026-07-21.
    /// </summary>
    private void AbandonWatchedPath(string path, string reason, bool durable = false)
    {
        // Only a classifier verdict of Permanent earns a durable record. Retry
        // exhaustion ("no progress after 5 retries") can be a slow copy or a flaky
        // disk, and persisting that would freeze a recoverable path across restarts.
        if (durable)
        {
            var (volumeCount, totalBytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(path);
            _abandonStore.Record(path, volumeCount, totalBytes, reason);
        }

        var (volumes, bytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(path);

        bool firstTime;
        lock (_watchLock)
        {
            firstTime = !_watchAbandoned.IsAbandoned(path, volumes, bytes);
            _watchAbandoned.Abandon(path, volumes, bytes);
            _watchProcessed.Add(path);
            _watchRetryCounts.Remove(path);
        }

        if (firstTime)
            Log.Warning("Extractor: abandoning watched archive {Path} — {Reason}. {Revival}", path, reason,
                durable
                    ? "It will be retried when the volume set changes (a new or resized part arrives)."
                    : "It will be retried after the folder changes or the app restarts.");
    }

    /// <summary>
    /// Start the sweep once per engine lifetime. Watch toggling and watcher recovery both
    /// reach this, so it is guarded rather than trusted to be called once.
    /// </summary>
    private void StartAbandonSweep()
    {
        if (Interlocked.Exchange(ref _abandonSweepStarted, 1) == 0)
        {
            using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
            _ = SweepAbandonedAsync();
        }
    }

    private bool ParkForManagedDownload(string path, ArchiveItem? item = null)
    {
        var owner = _downloadGuard.FindOwner(path);
        lock (_watchLock)
        {
            if (owner == null)
            {
                if (_managedDownloadParked.Remove(path)) _watchProcessed.Remove(path);
                return false;
            }
            _managedDownloadParked.Add(path);
            _watchProcessed.Add(path);
        }
        if (item != null)
        {
            item.Status = "Waiting";
            item.ErrorMessage = "Waiting for managed download completion";
        }
        // Reuse the bounded sweep, without consuming extraction retries or
        // recording an incomplete managed release as permanently corrupt.
        StartAbandonSweep();
        return true;
    }

    /// <summary>
    /// Re-examine abandoned paths on a timer.
    ///
    /// Fingerprint-based revival needs something to ask it. For a multi-volume set arriving,
    /// each new part raises a Created event — but a single large archive being written IN
    /// PLACE (the usual shape when a release is copied in from outside GlDrive) raises Created
    /// exactly once, at zero bytes. Only <c>Created</c> and <c>Renamed</c> are subscribed, so
    /// nothing further ever fires and the release would sit abandoned until the next restart
    /// even though the ledger would happily revive it.
    ///
    /// A sweep is used rather than subscribing <c>Changed</c> because Changed fires
    /// continuously throughout a multi-GB copy and each evaluation costs a directory
    /// enumeration. This costs one fingerprint per abandoned path per interval — bounded by
    /// the number of failures, not by library size — and it doubles as a safety net for
    /// watcher events dropped on a full 64 KB buffer or a network share.
    /// </summary>
    private async Task SweepAbandonedAsync()
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(AbandonSweepInterval, _lifetimeCts.Token);
                if (!_watchEnabled) continue;

                string[] managed;
                lock (_watchLock) managed = _managedDownloadParked.ToArray();
                foreach (var path in managed)
                {
                    if (_lifetimeCts.IsCancellationRequested) return;
                    if (ParkForManagedDownload(path)) continue;
                    // This also handles a managed extraction that removed its
                    // source archive: there is then nothing left to queue.
                    if (!File.Exists(path))
                    {
                        await _ui.InvokeAsync(() =>
                        {
                            var item = Archives.FirstOrDefault(a => a.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                            if (item?.Status == "Waiting")
                            {
                                item.Status = "Done";
                                item.Progress = 100;
                                item.ErrorMessage = null;
                            }
                        });
                        continue;
                    }
                    await HandleWatchedFileAsync(path);
                }

                foreach (var path in _watchAbandoned.AbandonedPaths())
                {
                    if (_lifetimeCts.IsCancellationRequested) return;

                    var (volumes, bytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(path);
                    if (_watchAbandoned.Evaluate(path, volumes, bytes)
                        != TransientAbandonLedger.AbandonState.Revived)
                        continue;

                    lock (_watchLock)
                    {
                        _watchProcessed.Remove(path);
                        _watchRetryCounts.Remove(path);
                    }

                    Log.Information(
                        "Extractor: sweep found abandoned archive changed ({Count} parts, {Bytes} bytes) — retrying {Path}",
                        volumes, bytes, path);

                    await HandleWatchedFileAsync(path);
                }

                foreach (var path in _spaceParked.TakeReady())
                {
                    if (_lifetimeCts.IsCancellationRequested) return;

                    lock (_watchLock)
                    {
                        _watchProcessed.Remove(path);
                        _watchRetryCounts.Remove(path);
                    }

                    Log.Information("Extractor: disk space available again — retrying {Path}", path);
                    await HandleWatchedFileAsync(path);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Extractor: abandoned-path sweep failed"); }
    }

    private async Task RetryWatchedFileAsync(string path, int attempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30 * attempt), _lifetimeCts.Token);
            await HandleWatchedFileAsync(path);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
    }

    private async Task RecoverWatcherAsync(string folder, FileSystemWatcher failedWatcher)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            await _ui.InvokeAsync(() =>
            {
                failedWatcher.EnableRaisingEvents = false;
                failedWatcher.Dispose();
                _watchers.Remove(failedWatcher);
                if (_watchEnabled && Directory.Exists(folder)) StartWatcherFor(folder);
            });
            if (_watchEnabled) await ScanAndAutoExtractAsync(folder);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Failed to recover watcher for {Dir}", folder); }
    }
}
