using System.IO;
using System.Threading.Channels;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadManager : IDisposable
{
    private readonly DownloadStore _store;
    private readonly IDownloadSource? _source;
    private readonly IWholeDownloadSource? _wholeSource;
    private readonly DownloadConfig _config;
    private readonly List<DownloadItem> _pendingQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();
    private readonly LiveConcurrencyGate _concurrency;
    private CancellationTokenSource? _cts;
    private Task? _processorTask;
    private int _disposed;
    private readonly Dictionary<string, CancellationTokenSource> _activeCts = new();
    // A row can be removed from history while its backend is still closing. Keep
    // ownership separately so re-enqueue cannot start another writer for that item.
    private readonly Dictionary<string, DownloadItem> _activeItems = new();
    private readonly HashSet<string> _restartAfterExit = [];
    private Task? _progressPersistTask;

    // Retry robustness (v3.6 Phase 2a). Auto-retry re-enqueues after a delay via a
    // background task. Across a Stop()/Start() cycle that task could resurrect a
    // download into a torn-down or freshly-restarted manager (double-enqueue or
    // loss). A monotonic generation stamps each Start(); a retry scheduled under an
    // old generation no-ops. Tracked in _retryTasks so StopAsync can drain them.
    private int _generation;
    private readonly List<Task> _retryTasks = new();
    private readonly object _retryLock = new();

    /// <summary>
    /// Re-check attempts per item while its destination volume is absent. Deliberately NOT
    /// item.RetryCount: that budget means "attempts this download gets before we call it dead",
    /// and an unmounted drive must never exhaust it. In-memory only — a restart re-probes the
    /// drive immediately and, if it is still absent, simply parks again from the first interval.
    /// </summary>
    private readonly Dictionary<string, int> _volumeWaitAttempts = new();
    private readonly object _volumeWaitLock = new();
    private readonly Dictionary<string, int> _sourceWaitAttempts = new();

    public event Action<DownloadItem, DownloadProgress>? DownloadProgressChanged;
    public event Action<DownloadItem>? DownloadStatusChanged;

    public DownloadStore Store => _store;
    internal DownloadRelocation? Relocation { get; set; }

    private readonly DiskReservation? _disk;

    public DownloadManager(DownloadStore store, FtpOperations ftp, StreamingDownloader downloader,
        DownloadConfig config, DiskReservation? disk = null)
        : this(store, new DownloadSource(ftp, downloader), config, disk) { }

    internal DownloadManager(DownloadStore store, IDownloadSource source,
        DownloadConfig config, DiskReservation? disk = null)
        : this(store, config, disk) => _source = source;

    internal DownloadManager(DownloadStore store, IWholeDownloadSource source,
        DownloadConfig config, DiskReservation? disk = null)
        : this(store, config, disk) => _wholeSource = source;

    private DownloadManager(DownloadStore store, DownloadConfig config, DiskReservation? disk)
    {
        _store = store;
        _config = config;
        _disk = disk;
        _concurrency = new LiveConcurrencyGate(config.MaxConcurrentDownloads);
    }

    internal void RefreshOptions() => _concurrency.Resize(_config.MaxConcurrentDownloads);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_processorTask is { IsCompleted: false } || _progressPersistTask is { IsCompleted: false })
            throw new InvalidOperationException("Download manager is already running or still stopping");
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        // New generation — any retry task still pending from a previous run will
        // see the bumped generation and no-op instead of enqueuing into this run.
        Interlocked.Increment(ref _generation);

        // Re-enqueue any queued items from store
        foreach (var item in _store.Items.Where(i => i.Status == DownloadStatus.Queued))
            EnqueueForProcessing(item);

        _processorTask = ProcessLoop(_cts.Token);
        _progressPersistTask = PersistProgressLoop(_cts.Token);
        Log.Information("DownloadManager started (max concurrent: {Max})", _config.MaxConcurrentDownloads);
    }

    /// <summary>
    /// Put an item on the pending queue and signal the processor — the single
    /// low-level enqueue funnel (used by Start re-enqueue, Retry, and the auto-retry
    /// task). Dedups by Id under the lock so a doubled signal can't queue the same
    /// item twice. Never throws if the manager is mid-teardown (signal disposed).
    /// </summary>
    private void EnqueueForProcessing(DownloadItem item)
    {
        lock (_queueLock)
        {
            if (_pendingQueue.Any(i => i.Id == item.Id)) return; // already queued
            _pendingQueue.Add(item);
        }
        try { _queueSignal.Release(); }
        catch (ObjectDisposedException) { /* manager stopped between add and signal */ }
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        Stop();
        var workers = Task.WhenAll(_processorTask ?? Task.CompletedTask,
            _progressPersistTask ?? Task.CompletedTask);
        try
        {
            await workers.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Log.Warning("DownloadManager stop timed out — abandoning background task");
        }
        catch (Exception ex) { Log.Debug(ex, "DownloadManager worker failed during stop"); }

        // Drain pending retry tasks (their Task.Delay is cancelled by _cts above, so
        // they finish promptly) before disposing _cts, so none fire post-stop.
        Task[] retries;
        lock (_retryLock) retries = _retryTasks.ToArray();
        if (retries.Length > 0)
        {
            try { await Task.WhenAll(retries).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        _store.Save();
    }

    public void Stop()
    {
        _cts?.Cancel();
        lock (_activeCts)
        {
            _restartAfterExit.Clear();
            foreach (var cts in _activeCts.Values)
                cts.Cancel();
        }
        // Workers can still register cancellation callbacks while they unwind.
        // Dispose the source together with their semaphores after they finish.
    }

    public bool Enqueue(DownloadItem item)
    {
        lock (_activeCts)
        lock (_queueLock)
        {
            // Duplicate dedup AND the _store.Add/_pendingQueue.Add below run under the
            // SAME _queueLock — so the check-then-add is atomic. Two concurrent
            // Enqueue() calls for one RemotePath serialize here: the first commits the
            // item (immediately visible via the _store.Items snapshot), the second
            // observes it and returns false. No TOCTOU between concurrent enqueues.
            var existing = FindPendingOrActive(item.RemotePath);
            if (existing != null)
            {
                Log.Information("Skipped duplicate download: {Release} (already {Status})", item.ReleaseName, existing.Status);
                return false;
            }

            if (item.SourceKind == DownloadSourceKind.Ftp && Directory.Exists(item.LocalPath))
            {
                Log.Information("Skipped download: {Release} (local path already exists)", item.ReleaseName);
                return false;
            }

            _store.Add(item);
            _pendingQueue.Add(item);
        }
        _queueSignal.Release();
        DownloadStatusChanged?.Invoke(item);
        Log.Information("Enqueued download: {Release}", item.ReleaseName);
        return true;
    }

    internal DownloadItem? FindPendingOrActive(string remotePath)
    {
        lock (_activeCts)
            return _activeItems.Values.FirstOrDefault(i => i.RemotePath == remotePath)
                ?? _store.Items.FirstOrDefault(i => i.RemotePath == remotePath &&
                    i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused);
    }

    public void Cancel(string id)
    {
        var item = _store.GetById(id);
        if (item == null) return;

        lock (_activeCts)
        {
            // Completion and cancellation race for the same terminal transition.
            // Once completion has committed, a late cancel cannot undo it.
            if (item.Status == DownloadStatus.Completed) return;
            _restartAfterExit.Remove(id);
            // Starting a worker and cancelling it share this lock. In particular, a
            // queued item can be waiting for a slot without having an active CTS yet.
            item.Status = DownloadStatus.Cancelled;
            item.ErrorMessage = null;
            if (_activeCts.TryGetValue(id, out var cts))
            {
                cts.Cancel();
            }
        }

        _store.Update(item);
        DownloadStatusChanged?.Invoke(item);
    }

    public void Pause(string id)
    {
        var item = _store.GetById(id);
        if (item == null) return;
        lock (_activeCts)
        {
            if (item.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading)) return;
            _restartAfterExit.Remove(id);
            item.Status = DownloadStatus.Paused;
            item.StatusDetail = null;
            if (_activeCts.TryGetValue(id, out var cts)) cts.Cancel();
        }
        _store.Update(item);
        _store.Save();
        DownloadStatusChanged?.Invoke(item);
    }

    public void Resume(string id)
    {
        var item = _store.GetById(id);
        if (item == null) return;
        lock (_activeCts)
        {
            if (item.Status != DownloadStatus.Paused) return;
            if (_activeCts.ContainsKey(id))
            {
                // An immediate Resume must wait for the previous writer to close.
                _restartAfterExit.Add(id);
                return;
            }
            item.Status = DownloadStatus.Queued;
            item.ErrorMessage = null;
            item.StatusDetail = null;
        }
        _store.Update(item);
        EnqueueForProcessing(item);
        DownloadStatusChanged?.Invoke(item);
    }

    public void Retry(string id)
    {
        var item = _store.GetById(id);
        if (item == null || (item.Status != DownloadStatus.Failed && item.Status != DownloadStatus.Cancelled)) return;

        lock (_activeCts)
        {
            // Let a cancelled worker finish unwinding before another worker owns
            // the same files and download state.
            if (_activeCts.ContainsKey(id))
            {
                _restartAfterExit.Add(id);
                return;
            }
            item.Status = DownloadStatus.Queued;
        }
        // Don't reset DownloadedBytes — resume will pick up from partial
        item.ErrorMessage = null;
        _store.Update(item);
        EnqueueForProcessing(item);
        DownloadStatusChanged?.Invoke(item);
    }

    public void RemoveCompleted() => _store.RemoveCompleted();
    public void RemoveFailed() => _store.RemoveFailed();
    public void RemoveCancelled() => _store.RemoveCancelled();
    public void RemoveFinished() => _store.RemoveFinished();

    public void MoveUp(string id)
    {
        lock (_queueLock)
        {
            var idx = _pendingQueue.FindIndex(i => i.Id == id);
            if (idx > 0)
            {
                (_pendingQueue[idx - 1], _pendingQueue[idx]) = (_pendingQueue[idx], _pendingQueue[idx - 1]);
            }
        }
    }

    public void MoveDown(string id)
    {
        lock (_queueLock)
        {
            var idx = _pendingQueue.FindIndex(i => i.Id == id);
            if (idx >= 0 && idx < _pendingQueue.Count - 1)
            {
                (_pendingQueue[idx], _pendingQueue[idx + 1]) = (_pendingQueue[idx + 1], _pendingQueue[idx]);
            }
        }
    }

    private async Task PersistProgressLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                // Periodically save download progress so resume works across restarts
                var hasActive = _store.Items.Any(i => i.Status == DownloadStatus.Downloading);
                if (hasActive)
                    _store.Save();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessLoop(CancellationToken ct)
    {
        var tasks = new List<Task>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(ct);

                // Schedule check: if scheduling enabled and outside window, delay
                if (_config.ScheduleEnabled)
                {
                    var hour = DateTime.Now.Hour;
                    bool inWindow = _config.ScheduleStartHour <= _config.ScheduleEndHour
                        ? hour >= _config.ScheduleStartHour && hour < _config.ScheduleEndHour
                        : hour >= _config.ScheduleStartHour || hour < _config.ScheduleEndHour;
                    if (!inWindow)
                    {
                        // Re-signal so we check again later
                        _queueSignal.Release();
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                        continue;
                    }
                }

                DownloadItem? item;
                lock (_queueLock)
                {
                    if (_pendingQueue.Count == 0) continue;
                    item = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                }

                // Skip if no longer queued (cancelled, etc.)
                var fresh = _store.GetById(item.Id);
                if (fresh == null || fresh.Status != DownloadStatus.Queued) continue;

                // Cooldown-defer: if the download pool is in a BNC cooldown, a Borrow
                // would just throw and burn a retry. Re-queue and wait for the window
                // to clear — no RetryCount cost (like the race engine's cooldown skip).
                if (_source?.IsInCooldown == true)
                {
                    EnqueueForProcessing(fresh);
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    continue;
                }

                await _concurrency.WaitAsync(ct);
                tasks.Add(ProcessItem(fresh, ct));
                ObserveAndPrune(tasks);
            }
        }
        catch (OperationCanceledException) { }

        try { await Task.WhenAll(tasks); }
        catch (Exception ex) { Log.Debug(ex, "DownloadManager: a ProcessItem task faulted during shutdown drain"); }
    }

    /// <summary>
    /// Schedule a delayed re-enqueue for an auto-retry. Tracked in _retryTasks so
    /// StopAsync can drain it, and generation-guarded so a retry scheduled before a
    /// Stop()/Start() cycle no-ops instead of resurrecting into the new run.
    /// </summary>
    private void ScheduleRetry(DownloadItem item, int delaySeconds)
    {
        var gen = Volatile.Read(ref _generation);
        var source = _cts;
        if (source == null || source.IsCancellationRequested) return;
        var token = source.Token;
        Task task = null!;
        task = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                lock (_activeCts)
                {
                    if (Volatile.Read(ref _generation) == gen &&
                        ReferenceEquals(_store.GetById(item.Id), item) && item.Status == DownloadStatus.Queued)
                        EnqueueForProcessing(item);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { /* _cts disposed mid-delay during stop */ }
            finally
            {
                lock (_retryLock) _retryTasks.Remove(task);
            }
        });
        lock (_retryLock) _retryTasks.Add(task);
    }

    /// <summary>
    /// Remove completed ProcessItem tasks, OBSERVING any fault so it isn't lost to
    /// an unobserved-task exception. ProcessItem catches its own download errors, so
    /// a fault here means an unexpected bug (e.g. store update threw) — log it.
    /// </summary>
    private static void ObserveAndPrune(List<Task> tasks)
    {
        tasks.RemoveAll(t =>
        {
            if (!t.IsCompleted) return false;
            if (t.IsFaulted)
                Log.Error(t.Exception, "DownloadManager: ProcessItem task faulted unexpectedly");
            return true;
        });
    }

    private async Task ProcessItem(DownloadItem item, CancellationToken globalCt)
    {
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
        lock (_activeCts)
        {
            // The queue check happened BEFORE waiting for a concurrency slot. The
            // user may have cancelled or removed this item while that wait was pending.
            if (!ReferenceEquals(_store.GetById(item.Id), item) ||
                item.Status != DownloadStatus.Queued || _activeCts.ContainsKey(item.Id))
            {
                itemCts.Dispose();
                _concurrency.Release();
                return;
            }
            _activeCts[item.Id] = itemCts;
            _activeItems[item.Id] = item;
            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = null;
            item.StatusDetail = null;
            item.RequiresContentApproval = false;
            item.StartedAt = DateTime.UtcNow;
        }

        // The network concurrency slot (acquired by ProcessLoop) is released as soon
        // as the network phase finishes, BEFORE the CPU-bound SFV/extract — so a big
        // extraction doesn't block other downloads from starting. Guarded so finally
        // never double-releases. Disk reservation tracked for symmetric release.
        var slotReleased = false;
        void ReleaseSlot()
        {
            if (slotReleased) return;
            slotReleased = true;
            _concurrency.Release();
        }
        // Disk reservation is RELEASED EXACTLY ONCE per ProcessItem via this guard,
        // mirroring ReleaseSlot. A retry re-enqueues the item and runs a *fresh*
        // ProcessItem with its own local reservation, so reserve/release stay
        // symmetric — the budget can't slowly leak across retry attempts (v3.6 Phase
        // 2b). The guard also prevents a double-release if the method gains more exit
        // paths later. Only ever non-null after a SUCCESSFUL TryReserve.
        string? reservedRoot = null;
        long reservedBytes = 0;
        var reservationReleased = false;
        void ReleaseReservation()
        {
            if (reservationReleased) return;
            reservationReleased = true;
            if (reservedRoot != null) _disk?.Release(reservedRoot, reservedBytes);
        }

        bool ReserveDisk(long? requiredBytes = null)
        {
            if (_disk == null) return true;
            var remaining = requiredBytes ?? Math.Max(0, item.TotalBytes - item.DownloadedBytes);
            if (!_disk.TryReserve(item.LocalPath, remaining, out var root))
            {
                var free = new DriveInfo(root).IsReady ? new DriveInfo(root).AvailableFreeSpace : 0;
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = $"Insufficient disk space: need {FormatSize(remaining)} (with reservations), have ~{FormatSize(free)}";
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
                EmitDownloadOutcome(item, "failed");
                return false;
            }
            reservedRoot = root;
            reservedBytes = remaining;
            return true;
        }

        try
        {
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);

            itemCts.Token.ThrowIfCancellationRequested();
            // Check the destination before listing remotely. Otherwise a network outage
            // burns retries on a download that should remain parked for an absent drive.
            if (DownloadTargetVolume.MissingVolumeRoot(item.LocalPath) is { } missingRoot)
                throw new DriveNotFoundException($"Destination drive {missingRoot} is not mounted");

            DownloadTreePlan plan;
            if (_wholeSource != null)
            {
                var payloadFinished = false;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    lock (_activeCts)
                    {
                        if (payloadFinished || item.Status != DownloadStatus.Downloading ||
                            !_activeCts.TryGetValue(item.Id, out var active) || active != itemCts) return;
                        if (p.TotalBytes > 0)
                        {
                            item.DownloadedBytes = Math.Clamp(p.DownloadedBytes, 0, p.TotalBytes);
                            item.TotalBytes = p.TotalBytes;
                        }
                        item.StatusDetail = p.StatusDetail;
                        DownloadProgressChanged?.Invoke(item, p);
                    }
                });
                // Metadata and screening happen before any payload writer is started.
                // The prepared operation owns its backend until writers have closed.
                await using (var prepared = await _wholeSource.PrepareAsync(item, progress, itemCts.Token))
                {
                    itemCts.Token.ThrowIfCancellationRequested();
                    item.TotalBytes = prepared.TotalBytes;
                    // Persisted progress does not prove that cached pieces still exist or
                    // pass their hashes. Reserve the selected payload conservatively until
                    // the torrent engine verifies its resume data during execution.
                    if (!ReserveDisk(prepared.TotalBytes)) return;
                    await prepared.ExecuteAsync(progress, itemCts.Token);
                    lock (_activeCts)
                    {
                        itemCts.Token.ThrowIfCancellationRequested();
                        payloadFinished = true;
                        item.DownloadedBytes = item.TotalBytes;
                        item.StatusDetail = null;
                    }
                    // Torrent pieces supply integrity verification. FTP inventory and SFV
                    // readiness rules below do not apply to this immutable content manifest.
                    plan = new DownloadTreePlan(true, [], prepared.Directories.ToArray());
                }
            }
            else
            {
                var source = _source ?? throw new InvalidOperationException("Download source is unavailable");
                // Plan the complete tree before starting lanes. Directory-only releases
                // and empty directories are directories too, not candidates for RETR.
                plan = await source.PlanAsync(item.RemotePath, item.LocalPath, itemCts.Token);
                itemCts.Token.ThrowIfCancellationRequested();
                if (plan.IncompleteReasons.Count > 0)
                {
                    ParkForSource(item, $"source reports incomplete ({plan.IncompleteReasons[0]})", itemCts.Token);
                    return;
                }

                if (!plan.IsDirectory)
                {
                    // Single file download
                    var fileName = Path.GetFileName(item.RemotePath);

                    if (HasPartial(item.LocalPath))
                        EmitDownloadOutcome(item, "resumed");

                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        lock (_activeCts)
                        {
                            if (item.Status != DownloadStatus.Downloading ||
                                !_activeCts.TryGetValue(item.Id, out var active) || active != itemCts) return;
                            item.DownloadedBytes = p.DownloadedBytes;
                            item.TotalBytes = p.TotalBytes;
                            DownloadProgressChanged?.Invoke(item, p with { CurrentFileName = fileName });
                        }
                    });

                    await source.DownloadAsync(plan.Files,
                        progress, itemCts.Token);
                }
                else
                {
                    // NFO pre-check
                    if (_config.SkipIncompleteReleases)
                    {
                        var hasNfo = plan.Files.Any(f => f.RemotePath.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));
                        if (!hasNfo)
                        {
                            ParkForSource(item, "no .nfo file has arrived", itemCts.Token, sourceEvidence: false);
                            return;
                        }
                    }

                    // Directory download — calculate total
                    item.TotalBytes = plan.Files.Sum(f => Math.Max(0, f.Size));

                    // Disk-space reservation (v3.6 Phase 2b) — accounts for what OTHER
                    // concurrent downloads already reserved, so N downloads can't each
                    // see "enough room" and collectively overrun the disk. Reserve the
                    // not-yet-on-disk remainder (TotalBytes minus already-downloaded).
                    if (!ReserveDisk()) return;

                    foreach (var directory in plan.Directories)
                        Directory.CreateDirectory(directory);

                    if (plan.Files.Any(f => HasPartial(f.LocalPath, f.Size)))
                        EmitDownloadOutcome(item, "resumed");

                    // The whole release goes to the engine at once so its lanes can work on
                    // different volumes in parallel. Files already complete on disk are skipped
                    // by the engine and counted as done.
                    var payloadFinished = false;
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        lock (_activeCts)
                        {
                            if (payloadFinished || item.Status != DownloadStatus.Downloading ||
                                !_activeCts.TryGetValue(item.Id, out var active) || active != itemCts) return;
                            item.DownloadedBytes = p.DownloadedBytes;
                            DownloadProgressChanged?.Invoke(item, p with { TotalBytes = item.TotalBytes });
                        }
                    });

                    var payload = plan.Files.Where(f => !IsManifest(f)).ToArray();
                    if (payload.Length > 0)
                        await source.DownloadAsync(payload, progress, itemCts.Token);
                    lock (_activeCts)
                    {
                        itemCts.Token.ThrowIfCancellationRequested();
                        payloadFinished = true;
                        item.DownloadedBytes = item.TotalBytes;
                    }

                    // A completed transfer only covers the inventory observed before
                    // it started. Do not extract a snapshot while the upstream release
                    // is still gaining files or a listed volume is still growing.
                    var latest = await source.PlanAsync(item.RemotePath, item.LocalPath, itemCts.Token);
                    itemCts.Token.ThrowIfCancellationRequested();
                    if (latest.IncompleteReasons.Count > 0 || !SameInventory(plan, latest))
                    {
                        ParkForSource(item, latest.IncompleteReasons.Count > 0
                            ? $"source reports incomplete ({latest.IncompleteReasons[0]})"
                            : "source files changed during download", itemCts.Token);
                        return;
                    }

                    // Read after the payload and final listing, including same-size
                    // replacements. A stale local manifest is never authoritative.
                    foreach (var manifest in latest.Files.Where(IsManifest))
                        await source.RefreshManifestAsync(manifest, itemCts.Token);
                    plan = latest;
                }
            }

            // Network phase done — hand the concurrency slot back so other downloads
            // can start while THIS item runs CPU-bound SFV verify + extraction.
            ReleaseSlot();

            // Presence is always required when an SFV exists. The setting only
            // controls the expensive CRC scan, never whether missing files count.
            if (_wholeSource == null && plan.IsDirectory)
            {
                var verification = await SfvVerifier.VerifyDetailedAsync(item.LocalPath,
                    plan.Files.Where(IsManifest).Select(f => f.LocalPath).ToArray(), _config.VerifySfv, itemCts.Token);
                var errors = verification.Issues.Where(i => i.Kind != SfvIssueKind.Missing).ToArray();
                if (errors.Length > 0)
                {
                    foreach (var issue in errors) Log.Warning("SFV verification failed: {File}", issue);
                    FailVerification(item, $"SFV verification failed: {errors.Length} checksum/read/manifest error(s): {errors[0]}", itemCts.Token);
                    return;
                }
                var missing = verification.Issues.Where(i => i.Kind == SfvIssueKind.Missing).ToArray();
                if (missing.Length > 0)
                {
                    ParkForSource(item, $"{missing.Length} SFV file(s) missing: {missing[0]}", itemCts.Token);
                    return;
                }
                if (item.SourceCompletionPending && !plan.HasCompletionMarker &&
                    !plan.Files.Any(IsManifest) && plan.Files.Any(f => ReleaseSourceState.IsArchive(f.LocalPath)))
                {
                    ParkForSource(item, "waiting for an SFV manifest for the archive set", itemCts.Token);
                    return;
                }
                if (_config.VerifySfv && plan.Files.Any(IsManifest))
                    Log.Information("SFV verification passed for {Release}", item.ReleaseName);
            }

            // Auto-extract RAR archives if enabled
            if (_config.AutoExtract && Directory.Exists(item.LocalPath))
            {
                try
                {
                    lock (_activeCts)
                    {
                        itemCts.Token.ThrowIfCancellationRequested();
                        item.Status = DownloadStatus.Extracting;
                    }
                    _store.Update(item);
                    DownloadStatusChanged?.Invoke(item);

                    foreach (var directory in plan.Directories)
                    {
                        var extraction = await ArchiveExtractor.ExtractIfNeeded(directory, itemCts.Token);
                        Log.Information("Extraction result for {Release}: extracted={Extracted}, deleteEnabled={Delete}",
                            item.ReleaseName, extraction.Extracted, _config.DeleteArchivesAfterExtract);
                        if (extraction.Extracted && _config.DeleteArchivesAfterExtract)
                        {
                            var cleanupSucceeded = true;
                            foreach (var firstVolume in extraction.FirstVolumes)
                                cleanupSucceeded &= ArchiveExtractor.DeleteArchiveSet(firstVolume);
                            if (!cleanupSucceeded)
                                Log.Warning("Extraction succeeded but one or more source volumes could not be deleted for {Release}",
                                    item.ReleaseName);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error(ex, "Extraction failed for {Release}", item.ReleaseName);
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = $"Extraction failed: {ex.Message}";
                    _store.Update(item);
                    DownloadStatusChanged?.Invoke(item);
                    EmitDownloadOutcome(item, "failed");
                    return;
                }
            }

            lock (_volumeWaitLock) _volumeWaitAttempts.Remove(item.Id);

            CompleteItem(item, itemCts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation(item, globalCt.IsCancellationRequested);
        }
        catch (Exception) when (itemCts.IsCancellationRequested || item.Status is DownloadStatus.Cancelled or DownloadStatus.Paused)
        {
            // Some FTP/file operations report an I/O error while cancellation
            // tears them down. They must not replace an accepted cancel with a retry.
            RecordCancellation(item, globalCt.IsCancellationRequested);
        }
        catch (DownloadBlockedException ex)
        {
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            item.StatusDetail = null;
            item.RequiresContentApproval = ex.CanSkipFiles;
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
            EmitDownloadOutcome(item, "failed");
        }
        catch (Exception ex) when (DownloadTargetVolume.IsVolumeAbsent(ex, item.LocalPath))
        {
            // The destination drive is not mounted. This is not a transient the ordinary
            // 30/60/90s ladder can outlast, and burning that ladder marks the item Failed and
            // loses the grab — four wishlist releases were lost this way on 2026-08-17/18.
            // Park it instead: keep the item Queued, do NOT consume a retry, and re-check on a
            // widening interval until the volume comes back. See DownloadTargetVolume.
            var root = DownloadTargetVolume.MissingVolumeRoot(item.LocalPath);

            int attempt;
            lock (_volumeWaitLock)
            {
                _volumeWaitAttempts.TryGetValue(item.Id, out attempt);
                attempt++;
                _volumeWaitAttempts[item.Id] = attempt;
            }
            var delay = DownloadTargetVolume.RecheckDelay(attempt);

            Log.Warning(
                "Download parked: {Release} — destination drive {Root} is not mounted (target {Path}). " +
                "Re-checking in {Delay}. No retries consumed; fix the drive or the download path in Settings.",
                item.ReleaseName, root, item.LocalPath, delay);

            item.Status = DownloadStatus.Queued;
            item.ErrorMessage = $"Waiting for drive {root} — destination volume is not mounted";
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
            ScheduleRetry(item, (int)delay.TotalSeconds);
        }
        catch (GlDrive.Downloads.Segmented.NetworkAllowanceExhaustedException ex)
        {
            // The user's data cap, not a failure: park without spending a retry and look again
            // later (allowance periods roll over at midnight / Monday / the 1st).
            Log.Warning("Download parked: {Release} — {Reason}. Re-checking in 15m; raise the allowance in Settings to resume now.",
                item.ReleaseName, ex.Message);
            item.Status = DownloadStatus.Queued;
            item.ErrorMessage = $"Waiting: {ex.Message}";
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
            ScheduleRetry(item, 15 * 60);
        }
        catch (Exception ex)
        {
            // A previously-parked item that got this far reached the network, so the volume
            // question is settled — let the next absence start its backoff from the beginning.
            lock (_volumeWaitLock) _volumeWaitAttempts.Remove(item.Id);

            if (Relocation != null && DownloadRelocation.IsMissingRemotePath(ex))
            {
                try
                {
                    var relocation = await Relocation.ResolveAsync(item, DateTime.UtcNow,
                        () => { _store.Update(item); _store.Save(); }, itemCts.Token);
                    itemCts.Token.ThrowIfCancellationRequested();
                    if (relocation.Outcome is RelocationOutcome.Relocated or RelocationOutcome.Waiting)
                    {
                        lock (_activeCts)
                        {
                            itemCts.Token.ThrowIfCancellationRequested();
                            item.Status = DownloadStatus.Queued;
                            item.ErrorMessage = relocation.Reason;
                            _store.Update(item);
                            _store.Save();
                        }
                        DownloadStatusChanged?.Invoke(item);
                        if (relocation.Outcome == RelocationOutcome.Relocated)
                            Log.Information("Download {Release} {Relocation}", item.ReleaseName, relocation.Reason);
                        ScheduleRetry(item, Math.Max(1, (int)Math.Ceiling(relocation.Delay.TotalSeconds)));
                        return;
                    }
                    if (relocation.Outcome == RelocationOutcome.Exhausted)
                    {
                        FailVerification(item, relocation.Reason, itemCts.Token);
                        return;
                    }
                    if (relocation.Outcome == RelocationOutcome.Missing)
                        ex = new IOException(relocation.Reason, ex);
                }
                catch (OperationCanceledException)
                {
                    RecordCancellation(item, globalCt.IsCancellationRequested);
                    return;
                }
                catch (Exception lookupError)
                {
                    // A failed probe is not evidence of absence. Keep ordinary retry
                    // handling, with the persisted lookup throttle still in force.
                    ex = new IOException("Release relocation lookup failed: " + lookupError.Message, lookupError);
                }
            }

            // Auto-retry with backoff
            if (item.RetryCount < _config.MaxRetries)
            {
                item.RetryCount++;
                var delay = _config.RetryDelaySeconds * item.RetryCount;
                Log.Warning(ex, "Download failed: {Release} — retry {Attempt}/{Max} in {Delay}s",
                    item.ReleaseName, item.RetryCount, _config.MaxRetries, delay);
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = $"Retry {item.RetryCount}/{_config.MaxRetries}: {ex.Message}";
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
                // NOTE: item.RetryCount is post-increment here — means "scheduling retry attempt N".
                EmitDownloadOutcome(item, "retried");
                ScheduleRetry(item, delay);
            }
            else
            {
                Log.Error(ex, "Download failed: {Release} (no retries left)", item.ReleaseName);
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
                EmitDownloadOutcome(item, "failed");
            }
        }
        finally
        {
            // Release the network slot if the post-process path didn't reach the
            // explicit ReleaseSlot() (early return, failure, or cancellation).
            ReleaseSlot();
            // Release the disk reservation symmetrically and exactly once (it covered
            // the in-flight bytes; the files are now actually on disk and counted by
            // the OS). On the retry path this releases before the re-enqueued item's
            // next ProcessItem re-reserves, so the budget never double-counts.
            ReleaseReservation();
            bool resume;
            lock (_activeCts)
            {
                _activeCts.Remove(item.Id);
                _activeItems.Remove(item.Id);
                resume = _restartAfterExit.Remove(item.Id) && !globalCt.IsCancellationRequested;
            }
            itemCts.Dispose();
            if (resume)
            {
                if (item.Status == DownloadStatus.Paused) Resume(item.Id);
                else Retry(item.Id);
            }
        }
    }

    private static bool IsManifest(FtpDownloadFile file) =>
        file.RemotePath.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase);

    private void RecordCancellation(DownloadItem item, bool stopping)
    {
        lock (_activeCts)
        {
            item.Status = StatusAfterCancellation(item.Status, stopping);
            if (item.Status == DownloadStatus.Cancelled) item.ErrorMessage = null;
            _store.Update(item);
            if (stopping) _store.Save();
        }
        DownloadStatusChanged?.Invoke(item);
        if (item.Status == DownloadStatus.Cancelled) EmitDownloadOutcome(item, "cancelled");
    }

    internal static bool SameInventory(DownloadTreePlan first, DownloadTreePlan second) =>
        first.IsDirectory == second.IsDirectory &&
        first.Directories.Order(StringComparer.Ordinal).SequenceEqual(second.Directories.Order(StringComparer.Ordinal)) &&
        first.Files.Select(f => (f.RemotePath, f.Size)).OrderBy(f => f.RemotePath, StringComparer.Ordinal)
            .SequenceEqual(second.Files.Select(f => (f.RemotePath, f.Size)).OrderBy(f => f.RemotePath, StringComparer.Ordinal));

    internal void ParkForSource(DownloadItem item, string reason, CancellationToken ct, bool sourceEvidence = true)
    {
        TimeSpan delay;
        lock (_activeCts)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status == DownloadStatus.Cancelled || !ReferenceEquals(_store.GetById(item.Id), item))
                throw new OperationCanceledException(ct);
            _sourceWaitAttempts.TryGetValue(item.Id, out var attempt);
            attempt = Math.Min(attempt + 1, 5);
            _sourceWaitAttempts[item.Id] = attempt;
            delay = ReleaseSourceState.RecheckDelay(attempt);
            item.Status = DownloadStatus.Queued;
            item.SourceCompletionPending |= sourceEvidence;
            item.ErrorMessage = $"Waiting for release completion: {reason}. Rechecking in {(int)delay.TotalSeconds}s.";
            _store.Update(item);
            // Persist the waiting reason/evidence immediately, even if the app
            // closes before the normal progress debounce fires.
            _store.Save();
        }
        DownloadStatusChanged?.Invoke(item);
        Log.Information("Download waiting for release completion: {Release} — {Reason}; rechecking in {Delay}",
            item.ReleaseName, reason, delay);
        ScheduleRetry(item, (int)delay.TotalSeconds);
    }

    private void FailVerification(DownloadItem item, string reason, CancellationToken ct)
    {
        lock (_activeCts)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status == DownloadStatus.Cancelled) throw new OperationCanceledException(ct);
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = reason;
            _store.Update(item);
        }
        DownloadStatusChanged?.Invoke(item);
        EmitDownloadOutcome(item, "failed");
    }

    internal void CompleteItem(DownloadItem item, CancellationToken ct)
    {
        lock (_activeCts)
        {
            // Empty directories and already-cached files may reach here without
            // another cancellable operation. Check under Cancel's lock so an
            // accepted cancellation cannot be replaced with Completed.
            ct.ThrowIfCancellationRequested();
            if (item.Status == DownloadStatus.Cancelled)
                throw new OperationCanceledException(ct);
            item.Status = DownloadStatus.Completed;
            item.ErrorMessage = null;
            item.StatusDetail = null;
            item.RequiresContentApproval = false;
            item.SourceCompletionPending = false;
            _sourceWaitAttempts.Remove(item.Id);
            item.CompletedAt = DateTime.UtcNow;
            _store.Update(item);
        }
        DownloadStatusChanged?.Invoke(item);
        EmitDownloadOutcome(item, "complete");
        Log.Information("Download completed: {Release}", item.ReleaseName);
    }

    private static void EmitDownloadOutcome(DownloadItem item, string result)
    {
        try
        {
            var recorder = App.TelemetryRecorder;
            if (recorder is null) return;
            long elapsed = 0;
            var endTime = result == "complete" ? item.CompletedAt ?? DateTime.UtcNow : DateTime.UtcNow;
            if (item.StartedAt.HasValue && endTime > item.StartedAt.Value)
                elapsed = (long)(endTime - item.StartedAt.Value).TotalMilliseconds;
            recorder.Record(GlDrive.AiAgent.TelemetryStream.Downloads,
                new GlDrive.AiAgent.DownloadOutcomeEvent
                {
                    DownloadId  = item.Id,
                    ServerId    = item.ServerId,
                    RemotePath  = item.RemotePath,
                    Result      = result,
                    Bytes       = item.TotalBytes,
                    ElapsedMs   = elapsed,
                    RetryCount  = item.RetryCount,
                    FailureClass = result == "failed" ? ClassifyFailure(item.ErrorMessage) : null
                });
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "EmitDownloadOutcome failed"); }
    }

    /// <summary>A staged partial, or a legacy partial written straight to the final name.</summary>
    private static bool HasPartial(string localPath, long size = -1)
    {
        if (File.Exists(localPath + ".gdpart")) return true;
        var final = new FileInfo(localPath);
        return final.Exists && final.Length > 0 && (size < 0 || final.Length < size);
    }

    private static string? ClassifyFailure(string? err)
    {
        if (string.IsNullOrEmpty(err)) return null;
        var low = err.ToLowerInvariant();
        if (low.Contains("timeout"))                     return "timeout";
        if (low.Contains("tls") || low.Contains("handshake")) return "tls";
        if (low.Contains("550"))                         return "ftp-550";
        if (low.Contains("421"))                         return "ftp-421";
        if (low.Contains("disk") || low.Contains("space")) return "disk";
        if (low.Contains("sfv"))                         return "sfv-mismatch";
        return "other";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    internal static DownloadStatus StatusAfterCancellation(DownloadStatus current, bool stopping) =>
        current == DownloadStatus.Paused ? DownloadStatus.Paused :
        stopping && current != DownloadStatus.Cancelled ? DownloadStatus.Queued : DownloadStatus.Cancelled;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        // Cancellation is asynchronous. A worker's finally still releases its slot
        // and saves its item, so keep both semaphore and store alive until it exits.
        Task[] retries;
        lock (_retryLock) retries = _retryTasks.ToArray();
        var drain = Task.WhenAll(retries.Append(_processorTask ?? Task.CompletedTask)
            .Append(_progressPersistTask ?? Task.CompletedTask));
        _ = drain.ContinueWith(completed =>
        {
            if (completed.IsFaulted)
                Log.Warning(completed.Exception, "DownloadManager shutdown worker failed");
            _store.Save();
            _store.Flush();
            _cts?.Dispose();
            _concurrency.Dispose();
            _queueSignal.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        GC.SuppressFinalize(this);
    }
}
