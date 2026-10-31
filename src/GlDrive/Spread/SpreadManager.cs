using System.Collections.Concurrent;
using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Services;
using Serilog;

namespace GlDrive.Spread;

public partial class SpreadManager : IDisposable, GlDrive.Downloads.Segmented.IRaceLoginLender
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, FtpConnectionPool> _spreadPools = new();
    private readonly Dictionary<string, int> _directFxpActivity = new();
    private readonly Dictionary<string, FtpClientFactory> _factories = new();
    // A refused local admission is already a decision, not another network fault.
    // Scope it to the pool/factory that observed it so remounts do not inherit stale holds.
    private readonly Dictionary<string, (object Owner, DateTime RetryAtUtc)> _poolAdmissionHolds = new();
    private readonly List<SpreadJob> _activeJobs = new();
    private readonly List<PersistedRace> _raceQueue = new();
    // Admission runs callbacks outside _lock. Reserve its request before leaving
    // the lock so capacity, deduplication and durable snapshots see it throughout.
    private readonly List<PersistedRace> _startingRequests = new();
    // The request behind each running job, so an in-flight race is persisted with the queue.
    private readonly Dictionary<SpreadJob, PersistedRace> _activeRequests = new();
    private readonly RaceQueueStore? _queueStore;
    private readonly SpeedTracker _speedTracker = new();
    private readonly SkiplistEvaluator _skiplist = new();
    private readonly RaceHistoryStore _history;
    private readonly SectionBlacklistStore _blacklist;
    private readonly MetadataFilterService _metadataFilter;
    private readonly Lock _lock = new();
    private readonly AutoRaceSkipNotice _autoRaceSkipNotice = new();
    private readonly SpreadPoolRecoveryLoop _poolRecovery = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _poolInitGates = new();
    private volatile bool _disposed;
    private readonly GlDrive.Util.BackgroundTasks _workers = new();
    private Timer? _queueWakeTimer;
    private List<PersistedRace>? _shutdownQueueSnapshot;
    public event Action<string, string>? PartialCleanupNotice;
    private (bool Validate, int Keepalive)? _appliedPoolHealth;

    internal void RefreshPoolHealth()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var options = (_config.Spread.ValidateConnectionOnBorrow, _config.Spread.SpreadKeepaliveSeconds);
            if (_appliedPoolHealth == options) return;
            foreach (var pool in _spreadPools.Values.Where(p => !p.IsDisposed))
                pool.ConfigureHealth(options.ValidateConnectionOnBorrow, options.SpreadKeepaliveSeconds);
            _appliedPoolHealth = options;
            Log.Information("Configuration: applied live: spread.validateConnectionOnBorrow, spread.spreadKeepaliveSeconds");
        }
    }

    internal void RefreshSiteRules()
    {
        lock (_lock)
        {
            _topologyVersion++;
            _sectionFeasibility.Clear();
            // Cached rule-based refusals must not outlive edits to those rules.
            foreach (var pair in _recentlyDeadRaces)
                if (pair.Value.reason == "affil-blocked") _recentlyDeadRaces.TryRemove(pair.Key, out _);
        }
    }

    // Global per-server transfer-gate (v2.4: Option A). Each FXP transfer
    // acquires BOTH src and dst gates before STOR. Without this, concurrent
    // races each independently took N slots on a shared BNC source -> BNC
    // 530 storm + GnuTLS native crashes (observed 3x 2026-05-15).
    private readonly ConcurrentDictionary<string, ServerGate> _serverGates = new();
    // Default per-server concurrent transfer cap. Picked deliberately below
    // any plausible BNC login cap so the FIRST race against a new BNC doesn't
    // overshoot; auto-tune (Option B) further tightens when a 530 surfaces.
    private const int DefaultPerServerConcurrentTransfers = 3;
    private static readonly TimeSpan GateAcquireTimeout = TimeSpan.FromSeconds(45);

    // TTL constants for the dead-race short-circuit map. Tunable here.
    private static readonly TimeSpan AffilBlockedTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReleaseNotFoundTtl = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan NoActivityTtl = TimeSpan.FromMinutes(30);
    // Short on purpose: the source was never even LISTed (pool starvation), so the
    // release may be perfectly raceable minutes later once pressure clears.
    private static readonly TimeSpan SourceScanFailedTtl = TimeSpan.FromMinutes(5);

    // In-memory TTL map of (section, release) -> (expiry, reason). Used to suppress
    // re-queueing of races that just failed with a known dead-end class (originally
    // "affil-blocked", later expanded to cover release-not-found and no-activity
    // stalls). Without this, a notification-driven release that no destination can
    // accept gets re-fired every poll cycle (observed 214x/day for one release,
    // 1200x for Law.and.Order.S08E11). Cleared on process restart — persistence
    // not needed because the restart itself clears the polling cadence.
    private readonly ConcurrentDictionary<(string section, string release), (DateTime expiry, string reason)> _recentlyDeadRaces = new();

    // Auto-race structural feasibility cache (v3.6 Phase 0). An announce fires
    // TryAutoRace, which spins a background Task to run rules/metadata gating —
    // but a section that NO 2 connected servers can host is infeasible for EVERY
    // release in that section, and the old code rediscovered that per-release on
    // every announce (339 wasted "Only 1 server allowed" spin-ups in one day).
    // This caches the cheap, FTP-free structural verdict PER SECTION so repeat
    // announces short-circuit before the Task.Run. Keyed by category (case-insens).
    // Entries carry the topology version they were computed under; a mount/unmount
    // bumps _topologyVersion and silently invalidates them (no stale suppression
    // when a new section-capable server connects). TTL is a backstop.
    private readonly ConcurrentDictionary<string, (DateTime expiry, int topoVersion, string detail)> _sectionFeasibility =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SectionInfeasibleTtl = TimeSpan.FromMinutes(10);
    // Bumped whenever the connected spread-pool SET changes (add/remove), so the
    // feasibility cache can't suppress a race after topology changed under it.
    private int _topologyVersion;

    public Func<string, FtpConnectionPool?>? _getMainPool;

    // Per-server search accessor (MountService.Search). Wired by ServerManager so the
    // spread engine can locate an alternate source mid-race without a hard dependency
    // on ServerManager. Null => alternate-source search unavailable (skipped).
    public Func<string, GlDrive.Downloads.FtpSearchService?>? _getSearchService;

    public event Action<string, string, string>? AutoRaceAttempted; // section, release, result

    public IReadOnlyList<SpreadJob> ActiveJobs
    {
        get { lock (_lock) return _activeJobs.ToList(); }
    }

    public RaceHistoryStore History => _history;

    /// <summary>A detached snapshot of requests waiting for a race slot.</summary>
    public IReadOnlyList<PersistedRace> QueuedRaces
    {
        get { lock (_lock) return _raceQueue.Select(r => r with { ServerIds = r.ServerIds.ToList(), SourceHints = r.SourceHints.ToList(), Partial = r.Partial?.Copy() }).ToArray(); }
    }

    /// <summary>Cancel a waiting request without interrupting already active transfers.</summary>
    public bool RemoveQueuedRace(string section, string releaseName)
    {
        bool removed;
        lock (_lock)
        {
            removed = CancelQueuedNoLock(section, releaseName);
            ScheduleQueueWakeupNoLock();
        }
        if (removed) PersistQueue();
        return removed;
    }

    /// <summary>Cancel a queued wait even if its due timer has just admitted its next probe.</summary>
    public bool CancelRaceRequest(string section, string releaseName)
    {
        bool removed;
        lock (_lock)
        {
            removed = CancelQueuedNoLock(section, releaseName);
            foreach (var starting in _startingRequests.Where(r => IsSameRace(r.Section, r.ReleaseName, section, releaseName) && r.HasOwnedPartial))
                _raceQueue.Add(RaceLifetimePolicy.CleanupRequest(starting, DateTime.UtcNow,
                    "Stopped by user during admission; owned partial retained until cleanup deadline",
                    RaceLifetimePolicy.Deadline(starting.Partial, _config.Spread.PartialRaceDeadlineHours))
                    with { StopReason = RaceStopReason.UserStop.ToString() });
            removed |= _startingRequests.RemoveAll(r => IsSameRace(r.Section, r.ReleaseName, section, releaseName)) > 0;
            foreach (var job in _activeJobs.Where(j => IsSameRace(j.Section, j.ReleaseName, section, releaseName)))
            {
                // Set Stopped while holding the same lock that requeues a Waiting job.
                // A cancelled probe can neither be registered nor returned to the queue.
                job.Stop();
                if (_activeRequests.TryGetValue(job, out var active))
                {
                    var cancelled = active with { Partial = job.CapturePartialState(), StopReason = RaceStopReason.UserStop.ToString() };
                    if (cancelled.HasOwnedPartial)
                    {
                        _raceQueue.RemoveAll(r => IsSameRace(r.Section, r.ReleaseName, section, releaseName));
                        _raceQueue.Add(RaceLifetimePolicy.CleanupRequest(cancelled, DateTime.UtcNow,
                            "Stopped by user; owned partial retained until cleanup deadline",
                            RaceLifetimePolicy.Deadline(cancelled.Partial, _config.Spread.PartialRaceDeadlineHours)));
                    }
                }
                _activeRequests.Remove(job);
                removed = true;
            }
            ScheduleQueueWakeupNoLock();
        }
        if (removed) PersistQueue();
        return removed;
    }

    /// <summary>
    /// PRD O2 — per-server pool health snapshot. Returns one entry per known
    /// spread pool with live counters (size/active/created/quarantine), BNC
    /// auto-detected login cap, and cooldown status. Surfaced in the Spread
    /// tab so a user can see at a glance why throughput is what it is.
    /// </summary>
    public IReadOnlyList<PoolHealthSnapshot> PoolHealth()
    {
        var list = new List<PoolHealthSnapshot>();
        lock (_lock)
        {
            foreach (var (id, pool) in _spreadPools)
            {
                var name = _config.Servers.FirstOrDefault(s => s.Id == id)?.Name ?? id;
                list.Add(new PoolHealthSnapshot(
                    ServerId: id,
                    ServerName: name,
                    MaxSize: pool.MaxSize,
                    Active: pool.ActiveCount,
                    Created: pool.TotalCreated,
                    Quarantined: pool.QuarantineSize,
                    ObservedBncCap: pool.ObservedLoginCap,
                    InCooldown: pool.IsThrottled));
            }
        }
        return list;
    }
    public SectionBlacklistStore Blacklist => _blacklist;
    public SpeedTracker TransferSpeeds => _speedTracker;

    public event Action<SpreadJob>? JobStarted;
    public event Action<SpreadJob>? JobCompleted;
    public event Action<SpreadJob>? JobEnded;
    public event Action<SpreadJob>? JobProgressChanged;

    public SpreadManager(AppConfig config) : this(config, ConfigManager.AppDataPath)
    {
        // Process-wide route memory; only the live manager may own its file.
        if (!ConfigManager.ReadOnly)
            FxpTransfer.EnableRelayRoutePersistence(Path.Combine(ConfigManager.AppDataPath, "fxp-relay-routes.json"));
    }

    // The state-directory seam keeps lifecycle regression tests away from live stores.
    internal SpreadManager(AppConfig config, string stateDirectory)
    {
        _config = config;
        _history = new RaceHistoryStore(Path.Combine(stateDirectory, "race-history.json"));
        _blacklist = new SectionBlacklistStore(Path.Combine(stateDirectory, "section-blacklist.json"));
        _metadataFilter = new MetadataFilterService(config, SitebotFacts);
        _history.Load();
        _blacklist.Load();
        // Read-only (screenshot) managers must not overwrite the live app's queue.
        if (!ConfigManager.ReadOnly)
        {
            _queueStore = new RaceQueueStore(Path.Combine(stateDirectory, "race-queue.json"));
            RestoreQueue();
        }
        // Screenshot rendering constructs and disposes demo managers while the real
        // app may keep recording transfers. Enabling persistence here lets Dispose()
        // overwrite its newer speed history with the renderer's stale snapshot.
        if (!ConfigManager.ReadOnly)
            _speedTracker.EnablePersistence(
                Path.Combine(stateDirectory, "spread-speed-history.json"));
        GuardedFtpLogin.LoginObserved += OnLoginObserved;
    }

    public async Task InitializePool(string serverId, FtpClientFactory factory, CancellationToken ct)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_spreadPools.ContainsKey(serverId)) return;
            if (!_factories.TryGetValue(serverId, out var previous) || !ReferenceEquals(previous, factory))
                _poolAdmissionHolds.Remove(serverId);
            // Record the desired factory before the first attempt. v3.10.73 only
            // recorded it after success, so the existing dead-pool recovery path
            // had nothing with which to recover a pool that failed at startup.
            _factories[serverId] = factory;
        }

        var initialized = await TryInitializePool(serverId, factory, ct, recoveryAttempt: null);
        if (!initialized && !ct.IsCancellationRequested && IsPoolRecoveryDesired(serverId, factory))
            SchedulePoolRecovery(serverId, factory);
    }

    private async Task<bool> TryInitializePool(
        string serverId,
        FtpClientFactory factory,
        CancellationToken ct,
        int? recoveryAttempt)
    {
        var initGate = _poolInitGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        FtpConnectionPool? pool = null;
        try
        {
            await initGate.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            lock (_lock)
            {
                if (_spreadPools.ContainsKey(serverId)) return true;
                if (_disposed || !_factories.TryGetValue(serverId, out var desired) ||
                    !ReferenceEquals(desired, factory)) return false;
                if (PoolAdmissionRetryAtNoLock(serverId, factory, AdmissionUtcNow()) != null) return false;
            }

            // Chain mode is gone — races now run N² concurrent routes throttled by
            // per-site slots, so a 1-connection spread pool serialises every
            // transfer and makes the whole engine look slow. Honor the configured
            // SpreadPoolSize (default 3) instead of the old hard-cap-at-1. Pool
            // creation is best-effort: if the server's login cap rejects some of
            // the N attempts, FtpConnectionPool runs with whatever it got.
            // Pool max is capped at the per-server gate's default (3) regardless of
            // user's SpreadPoolSize setting. Previously, SpreadPoolSize=12 vs BNC
            // cap of 4 meant the pool's Borrow() could fire connection-creates that
            // hit "530 restricted to N logins" even when only 3 FXP transfers were
            // active (scans + transfers competed for slots). The gate alone wasn't
            // enough — scan borrows and main-pool fallback share the same BNC pool.
            // Sizing pool to gate makes Borrow() block on Channel.ReadAsync until a
            // connection returns, instead of attempting CreateAndConnect and failing.
            var poolSize = Math.Min(
                Math.Max(_config.Spread.SpreadPoolSize, 1),
                DefaultPerServerConcurrentTransfers);
            if (poolSize <= 0) return false;

            // Share the account-wide login gate with the main + download pools for this
            // server (same host:port:username key) so the spread pool's logins count
            // against the SAME account cap — this is the linchpin that makes the cap
            // truly account-wide instead of per-pool.
            var loginGate = ResolveAccountLoginGate(serverId);
            // priorityLogins: true — draw from the gate's reserved FXP permit so the main
            // pool can't starve racing out of every login (the v3.7.2 root-cause fix).
            pool = new FtpConnectionPool(factory, poolSize, loginGate, priorityLogins: true);
            // Spread pools have no ConnectionMonitor keepalive, so idle connections
            // die and the next FXP borrow fails "No connection to the server exists"
            // (dominant failure on 2026-05-20 v2.6.0). Enable borrow-time NOOP
            // validation + a keepalive timer per config.
            pool.ConfigureHealth(_config.Spread.ValidateConnectionOnBorrow, _config.Spread.SpreadKeepaliveSeconds);
            // Wire BNC-limit auto-detect (Option B): when the pool sees a 530
            // "restricted to N simultaneous logins", tighten BOTH the gate AND
            // the pool's max size to N-1 (reserve one slot for ghost-kill).
            // Tightening only the gate left the pool free to attempt N+1 borrows
            // (scan + 3 transfers concurrent) and re-trigger the 530 storm —
            // observed 2026-05-15 14:48 with v2.4.0 (created=3, max=12, 530).
            pool.LoginLimitObserved += observedLimit =>
            {
                try
                {
                    var safe = Math.Max(1, observedLimit - 1);
                    GetOrCreateGate(serverId).TightenTo(safe);
                    pool.ShrinkMaxSize(safe);
                }
                catch (Exception ex) { Log.Debug(ex, "ServerGate auto-tune failed for {Server}", serverId); }
            };
            await pool.Initialize(ct);
            var registered = false;
            lock (_lock)
            {
                // Unmount/config replacement may have happened while the FTP
                // handshake was in flight. Never publish a stale pool.
                if (!_disposed && !_spreadPools.ContainsKey(serverId) &&
                    _factories.TryGetValue(serverId, out var desired) &&
                    ReferenceEquals(desired, factory))
                {
                    _spreadPools[serverId] = pool;
                    _poolAdmissionHolds.Remove(serverId);
                    _topologyVersion++; // invalidate the section-feasibility cache
                    registered = true;
                }
            }
            if (!registered)
            {
                await pool.DisposeAsync();
                lock (_lock) return _spreadPools.ContainsKey(serverId);
            }
            // Ensure the gate exists from the moment the pool comes up.
            GetOrCreateGate(serverId);
            Log.Information("Spread pool initialized for {ServerId} (size={Size}, gate={Gate})",
                serverId, poolSize, DefaultPerServerConcurrentTransfers);
            // Races restored at startup, or held while this pool was down, can start now.
            DequeueNextRace();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeFailedPool(pool, serverId);
            return false;
        }
        catch (FtpLoginHeldException ex)
        {
            RecordPoolAdmissionHold(serverId, factory, ex);
            await DisposeFailedPool(pool, serverId);
            return false;
        }
        catch (Exception ex)
        {
            if (recoveryAttempt == null)
                Log.Warning(ex, "Failed to initialize spread pool for {ServerId}; background recovery scheduled",
                    serverId);
            else
                Log.Information("Spread pool recovery attempt {Attempt} failed for {ServerId}: {ErrorType}: {Error}",
                    recoveryAttempt, serverId, ex.GetType().Name, ex.Message);
            await DisposeFailedPool(pool, serverId);
            return false;
        }
        finally
        {
            initGate.Release();
        }
    }

    private static async Task DisposeFailedPool(FtpConnectionPool? pool, string serverId)
    {
        if (pool == null) return;
        try { await pool.DisposeAsync(); }
        catch (Exception ex) { Log.Debug(ex, "Failed spread pool cleanup for {ServerId}", serverId); }
    }

    private bool IsPoolRecoveryDesired(string serverId, FtpClientFactory factory)
    {
        lock (_lock)
            return !_disposed && !_spreadPools.ContainsKey(serverId) &&
                   _factories.TryGetValue(serverId, out var desired) &&
                   ReferenceEquals(desired, factory);
    }

    private void SchedulePoolRecovery(string serverId, FtpClientFactory factory)
    {
        if (!_poolRecovery.Schedule(serverId, async (attempt, ct) =>
            {
                if (!IsPoolRecoveryDesired(serverId, factory)) return true;

                var initialized = await TryInitializePool(serverId, factory, ct, attempt);
                if (initialized)
                {
                    Log.Information("Spread pool recovered for {ServerId} on background attempt {Attempt}",
                        serverId, attempt);
                    return true;
                }
                return !IsPoolRecoveryDesired(serverId, factory);
            })) return;

        Log.Information("Spread pool recovery queued for {ServerId}; first retry in {Delay}",
            serverId, MountRetryPolicy.DelayFor(1));
    }

    /// <summary>The server's live spread pool, or null when none is up.</summary>
    internal FtpConnectionPool? GetLivePool(string serverId)
    {
        lock (_lock) return _spreadPools.TryGetValue(serverId, out var pool) && !pool.IsDisposed ? pool : null;
    }

    public async Task DisposePool(string serverId)
    {
        FtpConnectionPool? pool;
        lock (_lock)
        {
            _spreadPools.Remove(serverId, out pool);
            _factories.Remove(serverId);
            _poolAdmissionHolds.Remove(serverId);
            _topologyVersion++; // invalidate the section-feasibility cache
        }

        // Removing the desired factory above prevents an in-flight attempt from
        // registering after unmount; cancellation then drains its retry task.
        await _poolRecovery.CancelAsync(serverId);

        if (pool != null)
            await pool.DisposeAsync();
    }

    public SpreadJob? StartRace(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        var result = StartRaceCore(section, releaseName, serverIds, mode, knownSourceServerId, knownSourcePath,
            throwOnAdmissionFailure: true);
        // Auto-race callers rely on null for every non-started decision, even when
        // the detailed result identifies an already running job.
        return result.Outcome == RaceStartOutcome.Started ? result.Job : null;
    }

    public RaceStartResult StartRaceDetailed(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        var result = StartRaceCore(section, releaseName, serverIds, mode, knownSourceServerId, knownSourcePath,
            throwOnAdmissionFailure: false);
        return result;
    }

    internal Func<DateTime> AdmissionUtcNow { get; set; } = () => DateTime.UtcNow;

    private RaceStartResult StartRaceCore(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode, string? knownSourceServerId, string? knownSourcePath,
        bool throwOnAdmissionFailure)
    {
        // Sanitize inputs
        releaseName = SanitizeFtpPath(releaseName);
        section = SanitizeFtpPath(section);

        // Dedup the participant list — callers may pass the same server twice
        // (e.g. RequestFiller passing [sourceId, requesterId] where they match,
        // or UI code merging lists). A dup would crash StartRaceInternal's
        // ToDictionary with ArgumentException.
        serverIds = serverIds.Distinct().ToList();

        PersistedRace request;
        RaceStartResult? queued = null;
        lock (_lock)
        {
            if (_disposed) return new(RaceStartOutcome.Unavailable, section, releaseName, Reason: "Spread engine is stopped");
            var now = AdmissionUtcNow();
            var maxRaces = Math.Max(_config.Spread.MaxConcurrentRaces, 1);
            request = new PersistedRace(section, releaseName, serverIds.ToList(), mode,
                knownSourceServerId, knownSourcePath, now);
            if (IsRaceNuked(request))
                return new(RaceStartOutcome.Unavailable, section, releaseName, Reason: NukeHoldMessage);
            queued = MergeDuplicateNoLock(request, now);
            if (queued == null)
            {

            // Issue #4 (+ v1.66 expansion): short-circuit re-queues of a release
            // that recently failed with a known dead-end class (affil-blocked,
            // release-not-found, no-activity). The TTL map is populated by the
            // job.Error handler via ClassifyDeadRace.
            if (IsRecentlyDeadRace(section, releaseName, now, out var deadReason, out var remaining))
            {
                Log.Debug("Spread: skipping {Release} — recently failed: {Reason}", releaseName, deadReason);
                return new(RaceStartOutcome.Suppressed, section, releaseName, SuppressedFor: remaining, Reason: deadReason);
            }

            request = _config.Spread.MergeRaceAnnounces ? MergeRaceNoLock(request, request, true) : PredictRaceNoLock(request);
            var waitingForAdmission = request.ServerIds.Any(id => IsPoolAdmissionHeldNoLock(id, now))
                && (request.ServerIds.Count(id => CanUsePoolNoLock(id, now)) < 2 || KnownSourcesAdmissionHeldNoLock(request, now));
            if (_activeJobs.Count + _startingRequests.Count >= maxRaces || waitingForAdmission)
            {
                _raceQueue.Add(request);
                ScheduleQueueWakeupNoLock();
                if (waitingForAdmission)
                    Log.Information("Race queued while FTP login admission is deferred: {Release}", releaseName);
                else Log.Information("Race queued (max concurrent {Max}): {Release}", maxRaces, releaseName);
                var position = QueuePositionNoLock(request, now);
                queued = new(RaceStartOutcome.Queued, section, releaseName, QueuePosition: position,
                    Reason: waitingForAdmission ? "Waiting for FTP login admission" : position == null ? "Waiting for connected spread pools" : null);
            }
            else
            {
                _startingRequests.Add(request);
            }
            }
        }
        if (queued != null)
        {
            PersistQueue();
            if (queued.HintsMerged)
                _workers.TryRun(() => { DequeueNextRace(); return Task.CompletedTask; });
            return queued;
        }

        try { return new(RaceStartOutcome.Started, section, releaseName, Job: StartReservedRace(request)); }
        catch (Exception ex) when (!throwOnAdmissionFailure && (ex is InvalidOperationException or OperationCanceledException))
        {
            // A subscriber failure after registration is not a rejected admission.
            // Preserve the exception instead of claiming a live job was unavailable.
            lock (_lock)
                if (_activeRequests.Values.Any(r => r.RequestId == request.RequestId)) throw;
            return new(RaceStartOutcome.Unavailable, section, releaseName, Reason: ex.Message);
        }
    }

    private PersistedRace QueueRequestAt(PersistedRace request, DateTime now) =>
        !request.CleanupOnly && request.HasOwnedPartial
        && RaceLifetimePolicy.Deadline(request.Partial, _config.Spread.PartialRaceDeadlineHours) is { } deadline && now >= deadline
            ? RaceLifetimePolicy.CleanupRequest(request, now, "Partial completion deadline reached; cleaning owned directory")
                with { StopReason = RaceStopReason.Deadline.ToString() }
            : request;

    private IEnumerable<int> EligibleQueueIndicesNoLock(DateTime now) => RaceQueuePolicy.Order(_raceQueue
        .Select((request, index) => (Request: QueueRequestAt(request, now), Index: index))
        .Where(p => !p.Request.IsExpired(now) && p.Request.IsDue(now) && !IsRaceNuked(p.Request)
            && !_controlRequestsInFlight.Contains(p.Request.RequestId)
            && (p.Request.CleanupOnly || p.Request.ServerIds.Count(id => CanUsePoolNoLock(id, now)) >= 2
                && !KnownSourcesAdmissionHeldNoLock(p.Request, now)))
        .Select(p => (PredictRaceNoLock(p.Request), p.Index)), now, _config.Spread.SizeAwareRaceQueue, _config.Spread.RaceQueueMaxAgeMinutes);

    private int? QueuePositionNoLock(PersistedRace request, DateTime now)
    {
        var position = 0;
        foreach (var index in EligibleQueueIndicesNoLock(now))
        {
            position++;
            if (ReferenceEquals(_raceQueue[index], request)) return position;
        }
        return null;
    }

    private SpreadJob StartReservedRace(PersistedRace request, bool retainOnFailure = false)
    {
        // Persist before invoking external callbacks. A restart during admission
        // restores this as queued, without spending an in-flight resume attempt.
        PersistQueue();
        try { return StartRaceInternal(request); }
        catch
        {
            lock (_lock)
            {
                // Previously queued work retains its age, source hint and resume
                // count for a later drain. A rejected direct call stays rejected.
                var index = StartingIndexNoLock(request);
                if (index >= 0)
                {
                    var latest = _startingRequests[index];
                    _startingRequests.RemoveAt(index);
                    if (retainOnFailure && !_disposed) _raceQueue.Insert(0, latest);
                }
            }
            PersistQueue();
            throw;
        }
    }

    private SpreadJob StartRaceInternal(PersistedRace request)
    {
        var (section, releaseName, serverIds, mode, knownSourceServerId, knownSourcePath, _, _, _) = request;
        Dictionary<string, FtpConnectionPool> pools;
        Dictionary<string, ServerConfig> configs;

        lock (_lock)
        {
            var startingIndex = StartingIndexNoLock(request);
            if (startingIndex < 0) throw new OperationCanceledException("Race request cancelled during admission");
            request = _startingRequests[startingIndex];
            (section, releaseName, serverIds, mode, knownSourceServerId, knownSourcePath, _, _, _) = request;
            if (IsRaceNuked(request)) throw new InvalidOperationException(NukeHoldMessage);
            pools = serverIds
                .Where(id => CanUsePoolNoLock(id, AdmissionUtcNow()))
                .ToDictionary(id => id, id => _spreadPools[id]);

            configs = serverIds
                .Where(id => _config.Servers.Any(s => s.Id == id))
                .ToDictionary(id => id, id => _config.Servers.First(s => s.Id == id));

            if (!request.CleanupOnly && pools.Count < 2)
                throw new InvalidOperationException(
                    $"Need at least 2 connected spread pools (have {pools.Count}). " +
                    $"Connected pools: [{string.Join(", ", _spreadPools.Keys)}], " +
                    $"requested: [{string.Join(", ", serverIds)}]");
        }

        // Collect main server pools for scanning (spread pools are for FXP only)
        var mainPools = new Dictionary<string, FtpConnectionPool>();
        if (_getMainPool != null && !request.CleanupOnly)
        {
            foreach (var id in serverIds)
            {
                var mainPool = _getMainPool(id);
                if (mainPool != null) mainPools[id] = mainPool;
            }
        }

        SpreadJob job;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var startingIndex = StartingIndexNoLock(request);
            if (startingIndex < 0) throw new OperationCanceledException("Race request cancelled during admission");
            // Main-pool callbacks may have admitted another announce. Construct
            // and register from its canonical request in the same critical section.
            request = _startingRequests[startingIndex];
            (section, releaseName, serverIds, mode, knownSourceServerId, knownSourcePath, _, _, _) = request;
            pools = serverIds.Where(id => CanUsePoolNoLock(id, AdmissionUtcNow()))
                .ToDictionary(id => id, id => _spreadPools[id]);
            configs = serverIds.Where(id => _config.Servers.Any(s => s.Id == id))
                .ToDictionary(id => id, id => _config.Servers.First(s => s.Id == id));
            if (!request.CleanupOnly && pools.Count < 2)
                throw new InvalidOperationException("Spread pools changed during race admission");
            if (IsRaceNuked(request)) throw new InvalidOperationException(NukeHoldMessage);
            job = new SpreadJob(section, releaseName, mode, _config.Spread,
                pools, mainPools, configs, _speedTracker, _skiplist, _blacklist,
                knownSourceServerId, knownSourcePath);
            job.RestorePartial(request);
            job.SitebotFacts = SitebotFacts;
            job.IsReleaseNuked = IsNuked;
            // Resume timestamps include previous active work, not just queue wait.
            job.QueuedAtUtc = request.ResumeCount == 0 && request.Resumes == 0 ? request.QueuedAtUtc : null;
            if (_config.Spread.MergeRaceAnnounces) job.QueueSourceHints(request.SourceHints);
            _startingRequests.RemoveAt(startingIndex);
            _activeJobs.Add(job);
            _activeRequests[job] = request;
        }
        job.PartialStateChanged += j => SavePartialState(j, request);
        job.PartialCleanupMessage += message => PartialCleanupNotice?.Invoke(releaseName, message);

        // Hand the job a closure over our gate acquirer so every individual
        // FXP transfer respects the global per-server concurrency cap
        // regardless of which job kicked it off.
        job.AcquireTransferGates = (srcId, dstId, ct) =>
            AcquireTransferGates(srcId, dstId, ct);

        job.ProgressChanged += j => JobProgressChanged?.Invoke(j);
        job.Completed += j =>
        {
            JobCompleted?.Invoke(j);
        };
        job.Error += (j, msg) =>
        {
            // SetFailed already logged this exact message at WRN ("Spread job
            // failed") one line earlier — keep this handler's copy at Debug so
            // every failure isn't double-counted in log reviews (268 duplicate
            // pairs on 2026-07-01).
            Log.Debug("Spread job error: {Release} — {Error}", j.ReleaseName, msg);

            // Issue #4 (+ v1.66 expansion): when a race fails with a known dead-
            // end class, park (section, release) on a TTL so notification polling
            // can't re-fire it. ClassifyDeadRace picks the right TTL per class;
            // null => not a class we suppress, fall through (still logged above).
            var classified = ClassifyDeadRace(msg);
            if (classified is { } c)
            {
                var key = DeadRaceKey(j.Section, j.ReleaseName);
                _recentlyDeadRaces[key] = (DateTime.UtcNow + c.ttl, c.reason);
                Log.Debug("Spread: parking {Release} on dead-race TTL ({Reason}, {Minutes}min)",
                    j.ReleaseName, c.reason, (int)c.ttl.TotalMinutes);
            }
        };
        job.LivePoolResolver = id => { lock (_lock) { _spreadPools.TryGetValue(id, out var p); return p; } };
        job.LiveMainPoolResolver = _getMainPool;
        job.SourceSearch = async (release, exclude, ct, stopToken) =>
        {
            var r = await SearchReleaseOnServers(release, exclude, ct, stopToken);
            return r == null ? null : (r.ServerId, r.RemotePath, r.Category);
        };
        job.ServerConfigResolver = id => _config.Servers.FirstOrDefault(s => s.Id == id);

        PersistQueue();
        JobStarted?.Invoke(job);

        var sids = serverIds;
        _workers.TryRun(async () =>
        {
            var deferredForPools = false;
            try
            {
                // Re-initialize dead spread pools before running.
                // Pools die when all connections are poisoned/discarded (GnuTLS crashes,
                // network errors) and there's no keepalive/reconnect for spread pools.
                if (_disposed) return;
                if (!request.CleanupOnly) await ReinitDeadPools(sids);

                // Re-capture pool snapshot AFTER reinit — ReinitDeadPools may have
                // replaced an exhausted undersized pool with a brand new instance,
                // and the job's original snapshot would point at the disposed pool.
                Dictionary<string, FtpConnectionPool> fresh;
                lock (_lock)
                {
                    fresh = sids
                        .Where(id => CanUsePoolNoLock(id, AdmissionUtcNow()))
                        .ToDictionary(id => id, id => _spreadPools[id]);
                }

                bool sourceAdmissionHeld;
                lock (_lock) sourceAdmissionHeld = KnownSourcesAdmissionHeldNoLock(request, AdmissionUtcNow());
                if (!request.CleanupOnly && (fresh.Count < 2 || sourceAdmissionHeld))
                {
                    // A participating server was unmounted between StartRace() and here —
                    // not enough pools to race. Keep the durable request queued
                    // for reconnection instead of dropping it before RunAsync.
                    deferredForPools = true;
                    Log.Information("Spread job {Release}: only {Count} pools available after reinit; waiting for reconnect",
                        job.ReleaseName, fresh.Count);
                    return;
                }

                job.UpdatePools(fresh);

                if (_disposed) return;
                await job.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Spread job crashed: {Release} [{Section}]", job.ReleaseName, job.Section);
            }
            finally
            {
                // Always remove from active jobs — RunAsync can exit normally
                // (hard timeout, user stop, while-loop end) without invoking
                // Error/Completed, which left zombie jobs in _activeJobs and
                // permanently blocked the queue.
                bool wasActive;
                bool waiting;
                var partial = job.CapturePartialState();
                PersistedRace? followup = null;
                lock (_lock)
                {
                    var latest = _activeRequests.GetValueOrDefault(job) ?? request;
                    var finishedRequest = latest with
                    {
                        Partial = partial, StopReason = job.StopReason == RaceStopReason.None ? latest.StopReason : job.StopReason.ToString()
                    };
                    wasActive = _activeJobs.Remove(job);
                    var stillRequested = _activeRequests.Remove(job);
                    waiting = job.State == SpreadJobState.Waiting;
                    if (wasActive && !_disposed)
                    {
                        followup = NextRequest(finishedRequest, job, DateTime.UtcNow, deferredForPools, stillRequested);
                        if (followup != null)
                        {
                            _raceQueue.RemoveAll(r => IsSameRace(r.Section, r.ReleaseName, section, releaseName));
                            _raceQueue.Add(followup);
                        }
                    }
                    ScheduleQueueWakeupNoLock();
                }
                PersistQueue();
                if (followup?.CleanupOnly == true && (!request.CleanupOnly
                    || followup.CleanupAttempts >= RaceLifetimePolicy.MaxCleanupAttempts))
                    PartialCleanupNotice?.Invoke(releaseName, followup.WaitReason ?? "Owned partial needs cleanup");
                if (wasActive)
                {
                    if (!request.CleanupOnly && !deferredForPools && !waiting)
                    {
                        RecordHistory(job);
                        try { JobEnded?.Invoke(job); }
                        catch { Log.Warning("Race hook admission failed; completed work is unaffected"); }
                    }
                    if (job.State == SpreadJobState.Stopped)
                        Log.Information("Spread job stopped: {Release} [{Section}]",
                            job.ReleaseName, job.Section);
                    DequeueNextRace();
                }
            }
        });
        return job;
    }

    private void RecordHistory(SpreadJob job)
    {
        try
        {
            var totalBytes = job.Sites.Values.Sum(s => s.BytesTransferred);
            var totalFiles = job.Sites.Values.Any() ? job.Sites.Values.Max(s => s.FilesOwned) : 0;
            var siteNames = string.Join(", ", job.Sites.Values.Select(s => s.ServerName));

            // PRD R1 — outcome metrics. FilesTotal = release file count (max across
            // sites). Destinations = non-source sites, excluding fill-only dests
            // whose dir never appeared (they never participated — counting them
            // kept CleanComplete permanently false while the real dest finished).
            // FilesDelivered = best dest's owned count. CleanComplete = every
            // participating dest got the full set.
            var filesTotal = job.Sites.Values.Any() ? job.Sites.Values.Max(s => s.FilesTotal) : 0;
            var unopened = job.UnopenedFillOnlyDests;
            var dests = job.Sites.Values
                .Where(s => !s.IsSource && !unopened.Contains(s.ServerId))
                .ToList();
            var filesDelivered = dests.Count > 0 ? dests.Max(s => s.FilesOwned) : 0;
            var cleanComplete = job.State == SpreadJobState.Completed
                && filesTotal > 0
                && dests.Count > 0
                && dests.All(s => s.FilesOwned >= filesTotal);

            // Persist only the matched (denying) skiplist rule. The full per-rule
            // trace was 98% of race-history.json (9.9MB / 500 entries, ~10MB held
            // resident for the whole process lifetime) yet only the denying rule
            // drives the history detail popup; allowed races match no rule -> null.
            var slimTrace = job.SkiplistTrace?.Where(t => t.IsMatch).Take(1).ToList();
            if (slimTrace is { Count: 0 }) slimTrace = null;

            _history.Add(new RaceHistoryItem
            {
                Id = job.Id,
                ReleaseName = job.ReleaseName,
                Section = job.Section,
                Mode = job.Mode,
                Result = job.State,
                StartedAt = job.StartedAt,
                CompletedAt = DateTime.UtcNow,
                SiteCount = job.Sites.Count,
                FilesTransferred = totalFiles,
                BytesTransferred = totalBytes,
                SiteNames = siteNames,
                SkiplistResult = job.SkiplistResult,
                SkiplistTrace = slimTrace,
                FilesTotal = filesTotal,
                FilesDelivered = filesDelivered,
                CleanComplete = cleanComplete,
                Score = job.Score,
                FailureCategory = job.State == SpreadJobState.Failed
                    ? SpreadJob.ClassifyFailure(job.LastError)
                    : "",
                DestinationState = job.DestinationStateSummary(),
                StopReason = job.StopReason == RaceStopReason.None ? "" : job.StopReason.ToString(),
                ResumeCount = job.ResumeCount,
                FirstUploadAtUtc = job.FirstUploadAtUtc,
                DestinationOutcomes = job.DestinationOutcomes(),
                QueueWaitSeconds = job.QueueWaitSeconds,
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to record race history");
        }
    }

    /// <summary>
    /// Re-initialize spread pools that have died (all connections poisoned/discarded).
    /// Without this, dead pools stay dead forever since spread pools have no keepalive.
    /// </summary>
    private async Task ReinitDeadPools(IEnumerable<string> serverIds)
    {
        foreach (var id in serverIds)
        {
            FtpConnectionPool? pool;
            lock (_lock)
            {
                if (!_spreadPools.TryGetValue(id, out pool)) continue;
                if (PoolAdmissionRetryAtNoLock(id, pool, AdmissionUtcNow()) != null) continue;
            }

            if (!pool.IsExhausted || pool.IsTrustParked) continue;

            var serverName = _config.Servers.FirstOrDefault(s => s.Id == id)?.Name ?? id;

            // Don't revive an exhausted pool while the account is in a login-cap /
            // BNC cooldown — every reinit attempt just re-hits the gate timeout and
            // feeds the quarantine churn. The cooldown auto-clears (login-cap 20s,
            // refusal 90s) or on the next successful connect; the next race retries.
            if (pool.IsThrottled)
            {
                Log.Information("Spread pool exhausted for {Server} but in cooldown — skipping reinit until it clears", serverName);
                continue;
            }

            // INF, not WRN: spread pools deliberately have no keepalive (unlike the main
            // pools), so they go idle-dead between races and this fires on essentially
            // every race — 281 times on 2026-08-12 alone. It is the designed recovery
            // path, not a fault. Logging it at WRN made routine operation look degraded
            // and pushed the 10 MB rolling cap; gldrive-20260810.log rolled at 23:41 and
            // cost half a day of forensic history, which is the same harm noted in
            // v3.10.47 and v3.10.54.
            Log.Information("Spread pool exhausted for {Server} — reinitializing", serverName);
            try
            {
                // Match the configured spread pool size — with chain mode
                // removed we want all available slots back. Reinit must not
                // silently shrink the pool to 1 like the old chain-mode build
                // did or every subsequent race would run serial.
                // Reinit must respect the same BNC-safety cap as InitializePool:
                // pool size never exceeds the per-server gate's default. The
                // configured SpreadPoolSize is treated as a ceiling only.
                var neededSize = Math.Min(
                    Math.Max(_config.Spread.SpreadPoolSize, 1),
                    DefaultPerServerConcurrentTransfers);

                if (pool.MaxSize < neededSize)
                {
                    // Pool is undersized — replace it entirely
                    FtpClientFactory? factory;
                    lock (_lock) _factories.TryGetValue(id, out factory);
                    if (factory != null)
                    {
                        var newPool = new FtpConnectionPool(factory, neededSize, ResolveAccountLoginGate(id), priorityLogins: true);
                        newPool.ConfigureHealth(_config.Spread.ValidateConnectionOnBorrow, _config.Spread.SpreadKeepaliveSeconds);
                        try { await newPool.Initialize(CancellationToken.None); }
                        catch { await DisposeFailedPool(newPool, id); throw; }
                        var replaced = false;
                        lock (_lock)
                        {
                            if (!_disposed && _spreadPools.TryGetValue(id, out var current) && ReferenceEquals(current, pool))
                            {
                                _spreadPools[id] = newPool;
                                _poolAdmissionHolds.Remove(id);
                                replaced = true;
                            }
                        }
                        if (!replaced) { await DisposeFailedPool(newPool, id); continue; }
                        await pool.DisposeAsync();
                        Log.Information("Spread pool replaced for {Server} (old size={Old}, new size={New})",
                            serverName, pool.MaxSize, neededSize);
                        continue;
                    }
                }

                await pool.Reinitialize(CancellationToken.None);
                lock (_lock) _poolAdmissionHolds.Remove(id);
            }
            catch (FtpLoginHeldException ex)
            {
                RecordPoolAdmissionHold(id, pool, ex);
            }
            catch (Exception ex)
            {
                if (pool.IsTrustParked) Log.Information("Spread pool parked for a local certificate decision: {Server}", serverName);
                else Log.Warning(ex, "Failed to reinitialize spread pool for {Server}", serverName);
            }
        }
    }

    private DateTime? PoolAdmissionRetryAtNoLock(string id, object owner, DateTime now)
    {
        if (!_poolAdmissionHolds.TryGetValue(id, out var hold)) return null;
        if (ReferenceEquals(hold.Owner, owner) && hold.RetryAtUtc > now) return hold.RetryAtUtc;
        _poolAdmissionHolds.Remove(id);
        return null;
    }

    private bool IsPoolAdmissionHeldNoLock(string id, DateTime now) =>
        _spreadPools.TryGetValue(id, out var pool) && pool.IsExhausted
        && PoolAdmissionRetryAtNoLock(id, pool, now) != null;

    private bool CanUsePoolNoLock(string id, DateTime now) =>
        _spreadPools.TryGetValue(id, out var pool) && !pool.IsDisposed && !pool.IsTrustParked
        // Admission holds forbid new logins, never the reuse of an existing session.
        && !IsPoolAdmissionHeldNoLock(id, now);

    private bool KnownSourcesAdmissionHeldNoLock(PersistedRace request, DateTime now)
    {
        var sources = RaceAnnounceMerge.Hints(request).Select(hint => hint.ServerId)
            .Where(id => request.ServerIds.Contains(id) && RaceAnnounceMerge.CanSource(_config.Servers.FirstOrDefault(s => s.Id == id)))
            .Distinct().ToArray();
        return sources.Any(id => IsPoolAdmissionHeldNoLock(id, now))
            && !sources.Any(id => CanUsePoolNoLock(id, now));
    }

    private void RecordPoolAdmissionHold(string id, object owner, FtpLoginHeldException error)
    {
        DateTime retryAt;
        lock (_lock)
        {
            if (_disposed || !(_spreadPools.TryGetValue(id, out var currentPool) && ReferenceEquals(currentPool, owner))
                && !(_factories.TryGetValue(id, out var currentFactory) && ReferenceEquals(currentFactory, owner))) return;
            var now = AdmissionUtcNow();
            retryAt = error.RetryAtUtc is { } deadline && deadline > now ? deadline : now.AddSeconds(30);
            _poolAdmissionHolds[id] = (owner, retryAt);
            ScheduleQueueWakeupNoLock();
        }
        Log.Information("Spread pool login admission deferred for {ServerId} until {RetryAtUtc}", id, retryAt);
    }

    private void OnLoginObserved(FtpLoginObservation observation)
    {
        if (!observation.Succeeded || observation.Ghost) return;
        var initialize = new List<(string Id, FtpClientFactory Factory)>();
        lock (_lock)
        {
            if (_disposed) return;
            var recovered = _config.Servers.Where(server => _poolAdmissionHolds.ContainsKey(server.Id)
                && FtpLoginAccount.From(server.Connection).Key == observation.Account.Key).Select(server => server.Id).ToArray();
            if (recovered.Length == 0) return;
            foreach (var id in recovered)
            {
                _poolAdmissionHolds.Remove(id);
                if (!_spreadPools.ContainsKey(id) && _factories.TryGetValue(id, out var factory))
                    initialize.Add((id, factory));
            }
            ScheduleQueueWakeupNoLock();
        }
        // A successful manual/main-pool login supersedes an old outage deadline.
        // Resume through normal admission; the shared login guards remain authoritative.
        _workers.TryRun(async () =>
        {
            foreach (var (id, factory) in initialize)
                await TryInitializePool(id, factory, CancellationToken.None, recoveryAttempt: null);
            DequeueNextRace();
        });
    }

    /// <summary>
    /// Starts queued races while there is capacity. Ranks eligible requests using the
    /// configured queue policy; unavailable pools wait for <see cref="TryInitializePool"/>
    /// to drain them. Dequeuing blindly threw "Need at least 2 connected spread pools"
    /// and dropped the race whenever a pool was briefly down - and a restored queue is
    /// always ahead of the pools at startup.
    /// </summary>
    private void DequeueNextRace()
    {
        while (true)
        {
            PersistedRace? next = null;
            var expired = new List<PersistedRace>();
            lock (_lock)
            {
                if (_disposed) return;
                ScheduleQueueWakeupNoLock();
                var maxRaces = Math.Max(_config.Spread.MaxConcurrentRaces, 1);
                if (_raceQueue.Count == 0 || _activeJobs.Count + _startingRequests.Count >= maxRaces)
                    return;

                var now = DateTime.UtcNow;
                expired.AddRange(_raceQueue.Where(r => r.IsExpired(now)));
                _raceQueue.RemoveAll(r => r.IsExpired(now));

                for (var i = 0; i < _raceQueue.Count; i++)
                {
                    _raceQueue[i] = PredictRaceNoLock(QueueRequestAt(_raceQueue[i], now));
                }
                // Cleanup deadlines get a bounded turn even when the source is
                // unavailable. All fresh due races outrank ordinary resume attempts.
                var index = EligibleQueueIndicesNoLock(now).DefaultIfEmpty(-1).First();
                if (index >= 0)
                {
                    next = _raceQueue[index];
                    _raceQueue.RemoveAt(index);
                    _startingRequests.Add(next);
                }
            }

            foreach (var r in expired)
                Log.Warning("Queued race expired after {Hours:F0}h without a start: {Release}",
                    (DateTime.UtcNow - r.QueuedAtUtc).TotalHours, r.ReleaseName);
            if (next == null)
            {
                if (expired.Count > 0) PersistQueue();
                return;
            }

            try
            {
                StartReservedRace(next, retainOnFailure: true);
            }
            catch (ObjectDisposedException) when (_disposed) { return; }
            catch (OperationCanceledException) { continue; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to start queued race; request retained: {Release}", next.ReleaseName);
                return;
            }
        }
    }

    private void RestoreQueue()
    {
        var restore = RaceQueueStore.Restore(_queueStore!.Load(), DateTime.UtcNow);
        lock (_lock)
        {
            _raceQueue.AddRange(restore.Races);
            ScheduleQueueWakeupNoLock();
        }
        if (restore.Races.Count + restore.DroppedStale + restore.DroppedResumeCap > 0)
            Log.Information(
                "Race queue restored: {Count} race(s), {Resumed} were in flight; dropped {Stale} older than {Hours}h and {Capped} at the {Max}-resume cap",
                restore.Races.Count, restore.Resumed, restore.DroppedStale, (int)RaceQueueStore.MaxAge.TotalHours,
                restore.DroppedResumeCap, RaceQueueStore.MaxResumes);
        PersistQueue();
    }

    // One timer for the whole queue, not one sleeping task per incomplete release.
    // Overdue requests with unavailable pools are woken by pool initialization.
    private void ScheduleQueueWakeupNoLock()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        var next = _raceQueue.Where(r => !IsRaceNuked(r) && (!r.CleanupOnly || r.CleanupAttempts < RaceLifetimePolicy.MaxCleanupAttempts))
            .SelectMany(r => new[]
            {
                r.ServerIds.Any(id => _spreadPools.TryGetValue(id, out var pool) && pool.IsTrustParked) ? now.AddSeconds(30) : (DateTime?)null,
                r.CleanupOnly ? null : r.ServerIds.Where(id => IsPoolAdmissionHeldNoLock(id, now))
                    .Select(id => (DateTime?)_poolAdmissionHolds[id].RetryAtUtc).Min(),
                r.CleanupOnly && r.NextProbeAtUtc <= now ? now.AddSeconds(30) : r.NextProbeAtUtc,
                r.CleanupOnly ? null : RaceLifetimePolicy.Deadline(r.Partial, _config.Spread.PartialRaceDeadlineHours) is { } deadline
                    ? deadline <= now ? now.AddSeconds(30) : deadline : (DateTime?)null
            })
            .Where(at => at > now).Min();
        if (next == null)
        {
            _queueWakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        _queueWakeTimer ??= new Timer(_ => _workers.TryRun(() =>
        {
            DequeueNextRace();
            return Task.CompletedTask;
        }));
        _queueWakeTimer.Change(next.Value - now, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Snapshot of running + queued requests. Never called under <see cref="_lock"/>.
    /// Skipped once disposed so shutdown (which stops every job) can't erase the
    /// in-flight races it is about to interrupt.
    /// </summary>
    private bool PersistQueue()
    {
        return _queueStore?.Save(() =>
        {
            lock (_lock)
            {
                if (_disposed) return _shutdownQueueSnapshot?.ToList();
                return _activeRequests.Values.Select(r => r with { WasActive = true })
                    .Concat(_startingRequests)
                    .Concat(_raceQueue)
                    .ToList();
            }
        }) ?? true;
    }

    /// <summary>
    /// Called by ServerManager when a new release is detected.
    /// Starts a race if auto-race is enabled and section matches.
    /// </summary>
    public void TryAutoRace(string category, string releaseName,
        string? sourceServerId = null, string? sourcePath = null)
    {
        if (!_config.Spread.AutoRaceOnNotification)
        {
            AutoRaceAttempted?.Invoke(category, releaseName, "Auto-race disabled");
            return;
        }

        // Pass ALL connected servers with any sections — the job auto-discovers paths
        var connectedIds = GetConnectedServerIds();
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connectedIds.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id)
            .ToList();

        if (serverIds.Count < 2)
        {
            // Visibility: this was the silent killer — when spread pools fail to
            // initialize (login-cap starvation), _spreadPools is empty, every
            // auto-race lands here, and NOTHING was logged. Surface it so "review
            // logs" actually shows why racing is dead — but name the missing sites,
            // and only warn when that set changes: a peer outage skips every announce.
            int livePools;
            lock (_lock) livePools = _spreadPools.Count;
            var unavailable = _config.Servers
                .Where(s => s.Enabled && s.SpreadSite.Sections.Count > 0 && !connectedIds.Contains(s.Id))
                .Select(s => s.Name)
                .ToArray();
            var unavailableText = unavailable.Length > 0 ? string.Join(", ", unavailable) : "none";
            var hint = livePools == 0 ? " No FXP pool is live — if sites are mounted, spread pool init failed (check login cap)." : "";
            if (_autoRaceSkipNotice.Record(unavailable, DateTime.UtcNow) is { } suppressed)
                Log.Warning("Auto-race skipped for {Release} [{Section}]: {Count} of 2 required race sites connected; " +
                            "unavailable: {Unavailable}.{Hint} Repeats log at Debug until this changes ({Suppressed} suppressed).",
                    releaseName, category, serverIds.Count, unavailableText, hint, suppressed);
            else
                Log.Debug("Auto-race skipped for {Release} [{Section}]: {Count} of 2 required race sites connected; unavailable: {Unavailable}",
                    releaseName, category, serverIds.Count, unavailableText);
            AutoRaceAttempted?.Invoke(category, releaseName,
                $"Skipped — only {serverIds.Count} connected race site(s); unavailable: {unavailableText}");
            return;
        }
        _autoRaceSkipNotice.Reset();

        // Structural feasibility short-circuit (FTP-free). If fewer than 2 connected
        // servers can structurally host this section (not blacklisted AND section-
        // mappable), the async gate below would ALWAYS skip with "Only 1 server
        // allowed" — and it'd recompute that for every release in the section on
        // every announce. Suppress here, cached per-section, before spawning the Task.
        if (!IsSectionStructurallyFeasible(category, serverIds, out var infeasDetail))
        {
            AutoRaceAttempted?.Invoke(category, releaseName, infeasDetail);
            return;
        }

        // Pre-check: rules evaluation + metadata filter happen async (metadata
        // filter may do an HTTP call). Fire and forget — TryAutoRace stays sync
        // for its callers, but the gating runs in the background.
        _workers.TryRun(async () =>
        {
            try
            {
                await TryAutoRaceInternalAsync(category, releaseName, serverIds,
                    sourceServerId, sourcePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto-race pre-check failed for {Release}", releaseName);
                AutoRaceAttempted?.Invoke(category, releaseName, $"Pre-check error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Cheap, FTP-free necessary-condition gate for an auto-race: are there >=2
    /// connected servers that could structurally host this section (not blacklisted
    /// for the category AND <see cref="SectionMapper.HasSectionFor"/>)? This is a
    /// strict SUPERSET of the async gate in <see cref="TryAutoRaceInternalAsync"/>
    /// (rules/metadata can only REMOVE servers), so "infeasible" here guarantees the
    /// async path would also skip — safe to suppress. Result is cached per section
    /// and invalidated by topology changes (mount/unmount bump _topologyVersion).
    /// On infeasible, <paramref name="detail"/> carries a one-line reason and the
    /// caller logs/raises it once.
    /// </summary>
    private bool IsSectionStructurallyFeasible(string category, List<string> serverIds, out string detail)
    {
        int topo;
        lock (_lock) topo = _topologyVersion;

        if (_sectionFeasibility.TryGetValue(category, out var cached))
        {
            if (cached.topoVersion == topo && cached.expiry > DateTime.UtcNow)
            {
                detail = cached.detail; // cached infeasible verdict
                return false;
            }
            _sectionFeasibility.TryRemove(category, out _); // stale (topology or TTL)
        }

        var eligible = new List<string>();
        var missing = new List<string>();
        var receivers = 0;
        foreach (var serverId in serverIds)
        {
            var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (serverConfig == null) continue;
            if (!SectionMapper.HasSectionFor(serverConfig.SpreadSite, category))
            {
                missing.Add(serverConfig.Name);
                continue;
            }
            // A blacklisted site still counts toward eligibility: it can SOURCE the
            // release even though it can't receive it (see
            // CandidatePredicates.BlacklistExcludesSourceRole). Treating it as "can't
            // host this section" suppressed whole sections outright — the same
            // source/destination role conflation as the participant filter.
            eligible.Add(serverId);
            var blacklisted = _blacklist.IsBlacklisted(serverId, category)
                && !MkdFailureClassifier.IsPermanentMkdPathDenial(
                    _blacklist.Get(serverId, category)?.Reason ?? "");
            if (blacklisted) missing.Add($"{serverConfig.Name} (dest-blacklisted)");
            else receivers++;
        }

        // Still a genuine necessary condition: a race needs two mapped participants AND
        // at least one of them able to RECEIVE. Without the receiver check, a section
        // whose every destination is blacklisted would race forever and fail late.
        if (eligible.Count >= 2 && receivers >= 1)
        {
            detail = "";
            return true; // feasible — DON'T cache (let the real gate decide; avoids stale allow)
        }

        var miss = missing.Count > 0 ? $" (no [{category}] or blacklisted on: {string.Join(", ", missing)})" : "";
        detail = $"Skipped — only {eligible.Count} server(s) can host [{category}]{miss}";
        _sectionFeasibility[category] = (DateTime.UtcNow + SectionInfeasibleTtl, topo, detail);
        Log.Information("Auto-race structurally infeasible for [{Section}]: {Detail} — suppressing for {Ttl}min",
            category, detail, (int)SectionInfeasibleTtl.TotalMinutes);
        return false;
    }

    private async Task TryAutoRaceInternalAsync(string category, string releaseName,
        List<string> serverIds, string? sourceServerId, string? sourcePath)
    {
        var parsed = SceneNameParser.Parse(releaseName);
        var debug = _config.Spread.DebugMode;
        var allowed = new List<string>();
        var denials = new List<string>();

        foreach (var serverId in serverIds)
        {
            var serverConfig = _config.Servers.First(s => s.Id == serverId);
            var mapping = SectionMapper.Resolve(serverConfig.SpreadSite, category, releaseName);
            var effectiveSection = mapping?.RemoteSection ?? category;
            var tagRules = mapping?.TagRules ?? (IReadOnlyList<SkiplistRule>)Array.Empty<SkiplistRule>();

            // Blacklist check: a site that previously failed permanently for this
            // section can't RECEIVE it. Note the denial for the log, but keep the site
            // in the participant list — see CandidatePredicates.BlacklistExcludesSourceRole.
            // Dropping it here also removed it as a SOURCE, and Phase 1 only probes
            // participants, so a blacklisted site holding the release became invisible
            // and the race died with "Release not found on any server".
            // Destination exclusion still happens in SpreadJob's Phase 2 dest selection,
            // which additionally distinguishes fill-only dests and prints the fix hint.
            if (_blacklist.IsBlacklisted(serverId, category))
            {
                var entry = _blacklist.Get(serverId, category);
                var reason = entry?.Reason ?? "permanent MKD failure";
                if (!MkdFailureClassifier.IsPermanentMkdPathDenial(reason))
                    denials.Add($"{serverConfig.Name}: blacklisted as a destination ({reason}) — still usable as a source");
            }

            if (debug)
            {
                var (traceAction, trace) = _skiplist.EvaluateWithTrace(
                    releaseName, true, false, effectiveSection,
                    serverConfig.SpreadSite.Skiplist, _config.Spread.GlobalSkiplist, parsed);
                Log.Information("[DEBUG] {Server} rules trace for {Release}: action={Action}, " +
                                "evaluated {Count} rules", serverConfig.Name, releaseName, traceAction, trace.Count);
                foreach (var t in trace.Where(x => x.IsMatch || x.Result.StartsWith("MATCHED")))
                    Log.Information("[DEBUG]   MATCH: {Pattern} → {Result}", t.Pattern, t.Result);
            }

            var action = _skiplist.EvaluateTiered(releaseName, true, false,
                effectiveSection,
                serverConfig.SpreadSite.Skiplist,
                tagRules,
                _config.Spread.GlobalSkiplist,
                serverConfig.SpreadSite.Affils,
                parsed);

            if (action == SkiplistAction.Deny)
            {
                Log.Debug("Auto-race denied by rules on {Server}: {Release}", serverConfig.Name, releaseName);
                denials.Add($"{serverConfig.Name}: rules");
                continue;
            }

            // Metadata filter (per-site) — fails OPEN on error/timeout
            var metaFilter = serverConfig.SpreadSite.MetadataFilter;
            if (metaFilter.Enabled)
            {
                var verdict = await _metadataFilter.EvaluateAsync(metaFilter, releaseName, parsed, sourceServerId: sourceServerId);
                if (debug)
                    Log.Information("[DEBUG] {Server} metadata filter: allowed={Allowed} reason={Reason}",
                        serverConfig.Name, verdict.Allowed, verdict.Reason);
                if (!verdict.Allowed)
                {
                    Log.Debug("Auto-race denied by metadata filter on {Server}: {Release} ({Reason})",
                        serverConfig.Name, releaseName, verdict.Reason);
                    denials.Add($"{serverConfig.Name}: {verdict.Reason}");
                    continue;
                }
            }

            allowed.Add(serverId);
        }

        // Drop sites that have no section mapping or matching Sections key for
        // this announce category. Without this, races started with destinations
        // that could never host the release and burned a job lifecycle just to
        // fail with "Need 2+ servers — 1 unmapped (zephyr)". The participant
        // is the one we want to filter; the source may still come from a site
        // with the section (most common case is announce-on-source-server).
        //
        // We DON'T require every participant to have the section — a site that
        // has the section can still be source even if another participant can't
        // be dest. So the rule is: keep the site only if EITHER it has the
        // section OR at least one other allowed site has it.
        var sectionEligible = new List<string>();
        var sectionMissing = new List<string>();
        foreach (var serverId in allowed)
        {
            var serverConfig = _config.Servers.First(s => s.Id == serverId);
            if (SectionMapper.HasSectionFor(serverConfig.SpreadSite, category))
                sectionEligible.Add(serverId);
            else
                sectionMissing.Add(serverConfig.Name);
        }

        // If no allowed site has the section at all, the race is unwinnable —
        // skip silently with INFO instead of starting + failing as WARN.
        if (sectionEligible.Count == 0)
        {
            Log.Information("Auto-race skipped: no configured server has section [{Section}] for {Release}",
                category, releaseName);
            AutoRaceAttempted?.Invoke(category, releaseName,
                $"Skipped — no server has section [{category}]");
            return;
        }

        // Sites without the section can't be destinations, but might still
        // be useful as a source if they happen to hold the release. However,
        // for the common "announce → spread" pattern, source is in sectionEligible.
        // Replace the participant list with section-eligible-only — this is the
        // pragmatic fix for the user's setup (zephyr has no mp3/flac/x265 etc.).
        var skippedForSection = sectionMissing.Count > 0
            ? $" (no [{category}] on: {string.Join(", ", sectionMissing)})"
            : "";
        allowed = sectionEligible;

        // Destination-role preflight. Rules and section mapping can leave two valid
        // participants while every one of them is impossible as a destination (for
        // example, production has a download-only source and a peer whose affil list
        // owns the release group). Starting that job cannot transfer a byte; it only
        // creates a failed history row and a WRN. SpreadJob repeats these checks after
        // source discovery because a manual race still needs the guard, but auto-race
        // can reject the provably impossible topology before spending a race slot.
        var receiverExclusions = new List<string>();
        var viableReceiverCount = 0;
        // Live connectivity, not configuration. A server with no spread pool cannot
        // receive a byte, and SpreadJob's participant map is built from this same
        // registry — so counting an unreachable server as a viable receiver here
        // guarantees the job fails later with "No viable destinations" (v3.10.78).
        var connectedIds = GetConnectedServerIds();
        // Fail OPEN when nothing is connected yet: at startup, and in the window
        // between an unmount and pool recovery, an empty registry is indistinguishable
        // from "every site is down". Suppressing every auto-race on that ambiguity
        // would trade a noisy failure for a silent one, so reachability is only
        // allowed to EXCLUDE a server while at least one server is actually up.
        var reachabilityKnown = connectedIds.Count > 0;
        foreach (var serverId in allowed)
        {
            var serverConfig = _config.Servers.First(s => s.Id == serverId);
            var blacklistReason = _blacklist.Get(serverId, category)?.Reason;
            var destinationBlacklisted = _blacklist.IsBlacklisted(serverId, category)
                && !MkdFailureClassifier.IsPermanentMkdPathDenial(blacklistReason);
            var destinationReachable = !reachabilityKnown || connectedIds.Contains(serverId);

            if (CandidatePredicates.CanReceiveRelease(
                    releaseName, serverConfig.SpreadSite.Affils,
                    serverConfig.SpreadSite.DownloadOnly, destinationBlacklisted,
                    destinationReachable))
            {
                viableReceiverCount++;
                continue;
            }

            var why = serverConfig.SpreadSite.DownloadOnly ? "download-only"
                : destinationBlacklisted ? "destination-blacklisted"
                : !destinationReachable ? "not connected"
                : "affil-blocked";
            receiverExclusions.Add($"{serverConfig.Name}: {why}");
        }

        if (viableReceiverCount == 0)
        {
            var reason = CandidatePredicates.DescribeNoViableDestination(receiverExclusions, sectionMissing, denials, category);
            Log.Information("Auto-race skipped: {Reason} for {Release} [{Section}]",
                reason, releaseName, category);
            AutoRaceAttempted?.Invoke(category, releaseName, $"Skipped — {reason}");
            return;
        }

        // Need at least 2 participating servers for a race — source + dest.
        if (allowed.Count < 2)
        {
            var reason = denials.Count > 0
                ? $"Only {allowed.Count} server(s) allowed (denied: {string.Join(", ", denials)}){skippedForSection}"
                : $"Only {allowed.Count} server(s) allowed{skippedForSection}";
            Log.Information("Auto-race skipped: {Reason} for {Release} [{Section}]",
                reason, releaseName, category);
            AutoRaceAttempted?.Invoke(category, releaseName, reason);
            return;
        }

        // If the hinted source was filtered out, drop it so the job re-probes.
        if (sourceServerId != null && !allowed.Contains(sourceServerId))
        {
            sourceServerId = null;
            sourcePath = null;
        }

        try
        {
            var job = StartRace(category, releaseName, allowed, SpreadMode.Race, sourceServerId, sourcePath);
            if (job != null)
            {
                // Name the participants and the exclusions. A bare count made 375 failed
                // [tv-hd] races unreadable: "across 2 servers" never revealed WHICH two,
                // nor that the source had been dropped before the race began.
                var names = string.Join(", ", allowed.Select(id =>
                    _config.Servers.FirstOrDefault(s => s.Id == id)?.Name ?? id));
                var excluded = denials.Count > 0 ? $"; excluded: {string.Join("; ", denials)}" : "";
                Log.Information("Auto-race started: {Release} [{Section}] across {Count} servers ({Names}){Excluded}{SkippedForSection}",
                    releaseName, category, allowed.Count, names, excluded, skippedForSection);
                var suffix = denials.Count > 0 ? $" (skipped: {string.Join(", ", denials)})" : "";
                AutoRaceAttempted?.Invoke(category, releaseName, $"Racing on {allowed.Count} servers{suffix}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-race failed for {Release}", releaseName);
            AutoRaceAttempted?.Invoke(category, releaseName, $"Failed: {ex.Message}");
        }
    }

    public void StopJob(string jobId)
    {
        lock (_lock)
        {
            var job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
            if (job != null)
            {
                job.Stop();
                _activeRequests.Remove(job);
            }
        }
        PersistQueue();
    }

    public async Task StartFxp(string srcServerId, string srcPath,
        string dstServerId, string dstPath, CancellationToken ct)
    {
        srcPath = SanitizeFtpPath(srcPath);
        dstPath = SanitizeFtpPath(dstPath);
        using var activity = BeginDirectFxpActivity(srcServerId, dstServerId);

        FtpConnectionPool srcPool, dstPool;
        lock (_lock)
        {
            if (!_spreadPools.TryGetValue(srcServerId, out srcPool!))
                throw new InvalidOperationException($"No spread pool for source server {srcServerId}");
            if (!_spreadPools.TryGetValue(dstServerId, out dstPool!))
                throw new InvalidOperationException($"No spread pool for dest server {dstServerId}");
        }

        var mode = FxpModeDetector.Detect(srcPool, dstPool);
        var pair = await BorrowDirectFxpPair(srcServerId, t => srcPool.BorrowFor(FtpBorrowPurpose.Race, t), dstServerId, t => dstPool.BorrowFor(FtpBorrowPurpose.Race, t), ct);
        await using var srcConn = pair.Source;
        await using var dstConn = pair.Destination;

        var transfer = new FxpTransfer();
        var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
            _config.Spread.TransferTimeoutSeconds, ct,
            raceId: $"direct-{Guid.NewGuid():N}"[..16],
            srcServerId: srcServerId, dstServerId: dstServerId);

        if (!ok)
        {
            // Poison connections after failed transfer — GnuTLS session may be corrupt
            srcConn.Poison("direct FXP transfer failed");
            dstConn.Poison("direct FXP transfer failed");
            throw new IOException($"FXP transfer failed: {transfer.ErrorMessage}");
        }
    }

    /// <summary>
    /// FXP an entire directory recursively between two servers.
    /// </summary>
    public async Task StartFxpDirectory(string srcServerId, string srcPath,
        string dstServerId, string dstPath, CancellationToken ct)
    {
        srcPath = SanitizeFtpPath(srcPath);
        dstPath = SanitizeFtpPath(dstPath);
        using var activity = BeginDirectFxpActivity(srcServerId, dstServerId);

        FtpConnectionPool srcPool, dstPool;
        lock (_lock)
        {
            if (!_spreadPools.TryGetValue(srcServerId, out srcPool!))
                throw new InvalidOperationException($"No spread pool for source server {srcServerId}");
            if (!_spreadPools.TryGetValue(dstServerId, out dstPool!))
                throw new InvalidOperationException($"No spread pool for dest server {dstServerId}");
        }

        // List source directory recursively
        var files = new List<(string relativePath, string fullPath)>();
        // ListDirectory owns its borrow only until the listing completes. No
        // parent directory may retain a login while its children are scanned.
        await EnumerateDirectoryRecursive(new FtpOperations(srcPool).ListDirectory,
            srcPath, srcPath, files, 0, ct);

        var mode = FxpModeDetector.Detect(srcPool, dstPool);

        await TransferDirectoryFiles(files, async (relativePath, fullPath) =>
        {
            var destFile = dstPath.TrimEnd('/') + "/" + relativePath;

            var pair = await BorrowDirectFxpPair(srcServerId, t => srcPool.BorrowFor(FtpBorrowPurpose.Race, t), dstServerId, t => dstPool.BorrowFor(FtpBorrowPurpose.Race, t), ct);
            await using var srcConn = pair.Source;
            await using var dstConn = pair.Destination;

            var transfer = new FxpTransfer();
            // Defer directory creation until just before STOR
            var dstClient = dstConn.Client;
            var relPath = relativePath;
            transfer.BeforeStore = async storeCt =>
            {
                await dstClient.Execute($"MKD {Ftp.CpsvDataHelper.SanitizeFtpPath(dstPath)}", storeCt);
                var dirPart = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dirPart))
                {
                    var current = dstPath.TrimEnd('/');
                    foreach (var part in dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        current += "/" + part;
                        await dstClient.Execute($"MKD {Ftp.CpsvDataHelper.SanitizeFtpPath(current)}", storeCt);
                    }
                }
            };
            var ok = await transfer.ExecuteAsync(srcConn, dstConn, fullPath, destFile, mode,
                _config.Spread.TransferTimeoutSeconds, ct,
                raceId: $"direct-{Guid.NewGuid():N}"[..16],
                srcServerId: srcServerId, dstServerId: dstServerId);
            if (!ok)
            {
                srcConn.Poison("direct FXP transfer failed");
                dstConn.Poison("direct FXP transfer failed");
                return transfer.ErrorMessage ?? "Server rejected the transfer";
            }
            return null;
        });
    }

    internal static async Task TransferDirectoryFiles(
        IEnumerable<(string relativePath, string fullPath)> files,
        Func<string, string, Task<string?>> transfer)
    {
        foreach (var (relativePath, fullPath) in files)
        {
            var error = await transfer(relativePath, fullPath);
            if (error != null)
                throw new IOException($"FXP transfer failed for {fullPath}: {error}");
        }
    }

    internal static async Task EnumerateDirectoryRecursive(
        Func<string, CancellationToken, Task<FluentFTP.FtpListItem[]>> listDirectory,
        string basePath, string currentPath, List<(string relative, string full)> files,
        int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 5) return;

        var items = await listDirectory(currentPath, ct);

        foreach (var item in items)
        {
            if (item.Type == FluentFTP.FtpObjectType.Directory)
            {
                await EnumerateDirectoryRecursive(listDirectory, basePath, item.FullName, files, depth + 1, ct);
            }
            else if (item.Type == FluentFTP.FtpObjectType.File)
            {
                var relative = item.FullName;
                if (relative.StartsWith(basePath))
                    relative = relative[basePath.Length..].TrimStart('/');
                files.Add((relative, item.FullName));
            }
        }
    }

    public IReadOnlyList<string> GetConnectedServerIds()
    {
        lock (_lock) return _spreadPools.Keys.ToList();
    }

    internal static async Task<(T Source, T Destination)> BorrowDirectFxpPair<T>(
        string sourceId, Func<CancellationToken, Task<T>> borrowSource,
        string destinationId, Func<CancellationToken, Task<T>> borrowDestination,
        CancellationToken ct, TimeSpan? borrowTimeout = null) where T : class, IAsyncDisposable
    {
        ValidateDirectFxpSites(sourceId, destinationId);
        // Every direct transfer uses the same site order, including the reverse
        // direction. Otherwise one-slot pools can each be held while awaiting the other.
        var sourceFirst = StringComparer.Ordinal.Compare(sourceId, destinationId) < 0;
        var borrowFirst = sourceFirst ? borrowSource : borrowDestination;
        var borrowSecond = sourceFirst ? borrowDestination : borrowSource;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(borrowTimeout ?? TimeSpan.FromSeconds(30));
        T? first = null;
        var handedOff = false;
        try
        {
            first = await borrowFirst(deadline.Token);
            var second = await borrowSecond(deadline.Token);
            handedOff = true;
            return sourceFirst ? (first, second) : (second, first);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // The login gate reports an unavailable permit even when its wait was
            // cancelled. Preserve caller cancellation regardless of that wrapper.
            throw new OperationCanceledException("Direct FXP connection wait cancelled.", ex, ct);
        }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for direct FXP connections.", ex);
        }
        finally
        {
            // No FTP commands have been issued on the first lease yet, so returning
            // it is safe when its peer fails, times out or is cancelled.
            if (!handedOff && first != null) await first.DisposeAsync();
        }
    }

    private static void ValidateDirectFxpSites(string sourceId, string destinationId)
    {
        if (sourceId == destinationId)
            throw new ArgumentException("Direct FXP requires two distinct servers.", nameof(destinationId));
    }

    private IDisposable BeginDirectFxpActivity(string sourceId, string destinationId)
    {
        ValidateDirectFxpSites(sourceId, destinationId);
        string[] sites = [sourceId, destinationId];
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var site in sites)
                _directFxpActivity[site] = _directFxpActivity.GetValueOrDefault(site) + 1;
        }
        return new DirectFxpActivity(this, sites);
    }

    // A directory owns its activity across listing and every file, including the
    // gaps between borrows. Overlapping direct transfers release only their share.
    private sealed class DirectFxpActivity(SpreadManager owner, string[] sites) : IDisposable
    {
        private SpreadManager? _owner = owner;

        public void Dispose()
        {
            var manager = Interlocked.Exchange(ref _owner, null);
            if (manager == null) return;
            lock (manager._lock)
            {
                foreach (var site in sites)
                {
                    if (manager._directFxpActivity[site] == 1) manager._directFxpActivity.Remove(site);
                    else manager._directFxpActivity[site]--;
                }
            }
        }
    }

    // IRaceLoginLender — lets a download use this site's idle race login as a second stream.
    // Racing state is only READ here; the borrowed lane polls IsRaceIdle and hands the
    // connection back (clean Return, which wakes a waiting race Borrow) within ~1s.
    public bool IsRaceIdle(string serverId)
    {
        lock (_lock)
        {
            if (_disposed || _directFxpActivity.ContainsKey(serverId)) return false;
            if (_activeJobs.Any(j => j.Sites.ContainsKey(serverId))) return false;
            if (_startingRequests.Any(r => r.ServerIds.Contains(serverId))) return false;
            return !_raceQueue.Any(r => r.ServerIds.Contains(serverId));
        }
    }

    private static readonly TimeSpan LendLoginBudget = TimeSpan.FromSeconds(30);

    public async Task<PooledConnection?> TryBorrowAsync(string serverId, TimeSpan wait, CancellationToken ct)
    {
        FtpConnectionPool? pool;
        lock (_lock)
        {
            if (!_spreadPools.TryGetValue(serverId, out pool)) return null;
        }
        if (!IsRaceIdle(serverId) || pool.IsThrottled || !pool.UseCpsv) return null;

        try
        {
            // A login still dialling when the wait ends finishes into the pool instead of
            // being discarded, so the lane's next poll borrows it rather than dialling again.
            var conn = await GlDrive.Downloads.Segmented.BoundedLend.WaitOrReleaseAsync(
                token => pool.BorrowFor(FtpBorrowPurpose.Race, token),
                lease => lease.DisposeAsync(), wait, LendLoginBudget, ct);
            if (conn == null) return null; // nothing came free within the wait
            // A race may have been queued while we waited — give it straight back.
            if (IsRaceIdle(serverId)) return conn;
            await conn.DisposeAsync();
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // nothing came free within the wait
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Download: race login for {Server} not borrowable", serverId);
            return null;
        }
    }

    /// <summary>
    /// Search every connected server (except <paramref name="excludeIds"/>) in
    /// parallel for an exact match of <paramref name="release"/> and return the best
    /// alternate source. Candidates failing skiplist rules or section blacklist are
    /// dropped. Returns null when nothing usable is found. Reused by RequestFiller and
    /// by SpreadJob's mid-race source-migration failover.
    /// </summary>
    public Task<SearchResult?> SearchReleaseOnServers(
        string release, IReadOnlyCollection<string> excludeIds, CancellationToken ct)
        => SearchReleaseOnServers(release, excludeIds, ct, CancellationToken.None);

    public async Task<SearchResult?> SearchReleaseOnServers(
        string release, IReadOnlyCollection<string> excludeIds, CancellationToken ct, CancellationToken stopToken)
    {
        ct.ThrowIfCancellationRequested();
        stopToken.ThrowIfCancellationRequested();
        if (_getSearchService == null) return null;
        var candidates = GetConnectedServerIds()
            .Where(id => !excludeIds.Contains(id))
            .ToList();
        if (candidates.Count == 0) return null;

        var timeout = TimeSpan.FromSeconds(Math.Max(5, _config.Spread.AlternateSourceSearchTimeoutSeconds));

        var searches = candidates.Select(async id =>
        {
            var svc = _getSearchService(id);
            if (svc == null) return (SearchResult?)null;
            try
            {
                stopToken.ThrowIfCancellationRequested();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var search = await svc.SearchDetailed(release, null, cts.Token, stopToken);
                stopToken.ThrowIfCancellationRequested();
                if (search.IsPartial)
                    Log.Information("Alternate-source search incomplete on {Server}: {Count} matches, {Failed} failed listings, timed out={TimedOut}, truncated={Truncated}",
                        id, search.Results.Count, search.FailedListings, search.TimedOut, search.Truncated);
                var exact = search.Results.FirstOrDefault(r =>
                    r.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase));
                if (exact == null) return null;
                exact.ServerId = id; // ensure attribution
                return exact;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || stopToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Alternate-source search failed on {Server}", id);
                return null;
            }
        });

        var found = (await Task.WhenAll(searches)).Where(r => r != null).Select(r => r!).ToList();
        ct.ThrowIfCancellationRequested();
        stopToken.ThrowIfCancellationRequested();
        if (found.Count == 0) return null;

        // Filter by rules + blacklist; prefer the candidate with the most files.
        var serverConfigs = _config.Servers.ToDictionary(s => s.Id, s => s);
        SearchResult? best = null;
        foreach (var r in found.OrderByDescending(r => r.Files?.Count ?? 0))
        {
            if (!serverConfigs.TryGetValue(r.ServerId, out var sc)) continue;
            // A never-source site can hold the release and still must not be leeched from —
            // this is the third door into the source role, after Phase 1 discovery and
            // FindBestTransfer's route scoring.
            if (sc.SpreadSite.NeverSource) continue;
            if (_blacklist.IsBlacklisted(r.ServerId, r.Category)) continue;
            var action = _skiplist.Evaluate(release, true, false, r.ServerId, r.Category,
                sc.SpreadSite.Skiplist, _config.Spread.GlobalSkiplist);
            if (action == SkiplistAction.Deny) continue;
            best = r;
            break;
        }
        return best;
    }

    internal int? ObservedTransferLimit(string serverId) =>
        _serverGates.TryGetValue(serverId, out var gate) ? gate.CurrentLimit : null;

    private ServerGate GetOrCreateGate(string serverId) =>
        _serverGates.GetOrAdd(serverId, id =>
        {
            // Cap concurrent transfer PAIRS by the spread pool's GUARANTEED
            // priority permits (gate.Reserved), NOT Limit-1. The warmed main pool
            // pins the general permits for its whole connection lifetime, so the
            // priority spread pool can only reliably CREATE `Reserved` connections
            // connections. Allowing Limit-1 (=2) meant every 2nd transfer's source
            // borrow waited out the full 30s timeout it could never satisfy, then
            // (pre-fix) poisoned the pristine peer — collapsing the pool and
            // feeding the reinit/login-cap storm (2026-06-24: 319 superbnc->zephyr
            // borrow timeouts, 652 "login cap reached", 2354 poison-discards).
            // Reserved is the count the source can actually serve alongside its
            // 2s-cadence completion scan; 530 auto-tune can still tighten further.
            var cap = DefaultPerServerConcurrentTransfers;
            var lg = ResolveAccountLoginGate(id);
            if (lg != null) cap = Math.Max(1, Math.Min(cap, lg.Reserved));
            return new ServerGate(id, cap);
        });

    /// <summary>
    /// Resolve the account-wide login gate for a server (shared with its main +
    /// download pools via the host:port:username key). Returns null only if the
    /// server config is missing — the pool then runs ungated (prior behavior).
    /// Note: distinct from <see cref="ServerGate"/>, which caps concurrent transfer
    /// PAIRS; this gate caps total live LOGINS to the account.
    /// </summary>
    private IAccountLoginGate? ResolveAccountLoginGate(string serverId)
    {
        var sc = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (sc == null) return null;
        return ServerLoginGateRegistry.GetOrCreate(
            sc.Connection.Host, sc.Connection.Port, sc.Connection.Username,
            sc.Pool.LoginCap, sc.Pool.LoginHeadroom);
    }

    /// <summary>
    /// Acquire src + dst transfer gates for a single FXP file transfer.
    /// Ordering is sorted-by-serverId to avoid A->B / B->A deadlock when
    /// two transfers each hold one gate. On timeout, throws
    /// <see cref="TimeoutException"/>; the caller should treat the file as
    /// a transient failure and let the scoreboard reschedule.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireTransferGates(string srcId, string dstId, CancellationToken ct)
    {
        // Deterministic acquire order keeps deadlocks impossible.
        var firstId = string.CompareOrdinal(srcId, dstId) < 0 ? srcId : dstId;
        var secondId = firstId == srcId ? dstId : srcId;
        var firstGate = GetOrCreateGate(firstId);
        var secondGate = GetOrCreateGate(secondId);

        var firstHandle = await firstGate.AcquireAsync(GateAcquireTimeout, ct);
        try
        {
            var secondHandle = await secondGate.AcquireAsync(GateAcquireTimeout, ct);
            return new CombinedGateHandle(firstHandle, secondHandle);
        }
        catch
        {
            await firstHandle.DisposeAsync();
            throw;
        }
    }

    public void Dispose()
    {
        List<SpreadJob> jobs;
        lock (_lock)
        {
            if (_disposed) return;
            _shutdownQueueSnapshot = _activeRequests.Values.Select(r => r with { WasActive = true })
                .Concat(_startingRequests).Concat(_raceQueue).ToList();
            _disposed = true;
            jobs = _activeJobs.ToList();
            _raceQueue.Clear();
            _startingRequests.Clear();
        }
        GuardedFtpLogin.LoginObserved -= OnLoginObserved;
        _poolRecovery.Dispose();
        _queueWakeTimer?.Dispose();
        foreach (var job in jobs)
        {
            job.Stop(RaceStopReason.AppShutdown);
            try { SavePartialState(job, _shutdownQueueSnapshot.FirstOrDefault(r => IsSameRace(r.Section, r.ReleaseName, job.Section, job.ReleaseName))); }
            catch (Exception ex) { Log.Warning(ex, "Failed to save final race ownership during shutdown"); }
        }
        var workers = _workers.StopAsync();
        var teardown = Task.Run(async () =>
        {
            try { await workers; }
            catch (Exception ex) { Log.Warning(ex, "Spread worker failed while stopping"); }
            try { _speedTracker.Save(); }
            catch (Exception ex) { Log.Warning(ex, "Final speed history save failed"); }
            foreach (var gate in _serverGates.Values) gate.Dispose();
            _serverGates.Clear();
            _metadataFilter.Dispose();
            List<FtpConnectionPool> pools;
            lock (_lock)
            {
                pools = _spreadPools.Values.ToList();
                _spreadPools.Clear();
                _factories.Clear();
                _activeJobs.Clear();
            }
            await Task.WhenAll(pools.Select(async pool =>
            {
                try { await pool.DisposeAsync(); }
                catch (Exception ex) { Log.Warning(ex, "Spread pool dispose error"); }
            }));
        });
        // If a native call outlives the deadline, its gates remain valid until it exits.
        if (!teardown.Wait(TimeSpan.FromSeconds(5)))
            Log.Warning("Spread shutdown exceeded 5 seconds; resource disposal deferred until workers exit");
        GC.SuppressFinalize(this);
    }

    private static string SanitizeFtpPath(string path) =>
        path.Replace("\r", "").Replace("\n", "").Replace("\0", "");

    // Same release name = same content, regardless of which section label the
    // announce carried. The old section-qualified comparison let the SAME release
    // race twice concurrently when a dest site's own bot re-announced GlDrive's
    // MKD under a different section name ("-TV-" vs "tv-hd" self-echo loop,
    // 2026-06-08): the second race re-raced into the dirs the first had just
    // cleaned up, and the remnant got auto-nuked. Sections intentionally ignored.
    private static bool IsSameRace(string sectionA, string releaseA, string sectionB, string releaseB) =>
        string.Equals(releaseA, releaseB, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalize a release to a case-insensitive key for the dead-race TTL map.
    /// Mirrors IsSameRace semantics (release-only — see comment there); the
    /// section slot is kept empty for tuple-shape compatibility.
    /// </summary>
    private static (string section, string release) DeadRaceKey(string section, string release) =>
        ("", release.ToLowerInvariant());

    /// <summary>
    /// True iff this (section, release) was recently failed with a class we
    /// suppress and the cool-down hasn't expired. Outputs the matching reason
    /// for diagnostic logging. Expired entries are evicted on read.
    /// </summary>
    private bool IsRecentlyDeadRace(string section, string release, DateTime now, out string? reason, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        var key = DeadRaceKey(section, release);
        if (!_recentlyDeadRaces.TryGetValue(key, out var entry))
        {
            reason = null;
            return false;
        }
        if (entry.expiry > now)
        {
            reason = entry.reason;
            remaining = entry.expiry - now;
            return true;
        }
        _recentlyDeadRaces.TryRemove(key, out _);
        reason = null;
        return false;
    }

    /// <summary>
    /// Classify a job error message into a dead-race TTL + reason tag, or null
    /// if the message isn't a known dead-end class. Reason strings double as
    /// log tags. Case-insensitive matching throughout.
    /// </summary>
    internal static (TimeSpan ttl, string reason)? ClassifyDeadRace(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return null;

        // Check BEFORE release-not-found: an inconclusive sweep proved nothing, so
        // it must not inherit that verdict's hour-long park. Short TTL = re-probe
        // on the next announce once the pool recovers.
        if (errorMessage.Contains("Source probe inconclusive", StringComparison.OrdinalIgnoreCase))
            return (SourceScanFailedTtl, "source-probe-inconclusive");

        if (errorMessage.Contains("affil-blocked", StringComparison.OrdinalIgnoreCase))
            return (AffilBlockedTtl, "affil-blocked");

        if (errorMessage.Contains("Release not found on any server", StringComparison.OrdinalIgnoreCase))
            return (ReleaseNotFoundTtl, "release-not-found");

        // Require BOTH substrings so we don't catch unrelated "no activity"
        // mentions (e.g. log lines about idle sites that aren't job errors).
        if (errorMessage.Contains("No activity for", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("no viable transfers", StringComparison.OrdinalIgnoreCase))
            return (NoActivityTtl, "no-activity");

        if (errorMessage.Contains("Source scan never succeeded", StringComparison.OrdinalIgnoreCase))
            return (SourceScanFailedTtl, "source-scan-failed");

        // All dests fill-only: structurally unwinnable until another racer creates
        // the dir (or the siteop grants MKD). Park it so every announce/poll
        // doesn't re-spawn the same doomed race (190 in 7h on 2026-06-11).
        if (errorMessage.Contains("All destinations are fill-only", StringComparison.OrdinalIgnoreCase))
            return (NoActivityTtl, "fill-only");

        // Same shape as fill-only one branch up, and it was simply missing: a race that
        // died because every dest refused MKD for this release recorded no give-up
        // decision at all, so the next announce or /recent poll re-ran it immediately
        // (333 such failures on 2026-08-03). Keyed on (section, release) like every other
        // dead-race entry, so it can never blacklist a section — a different release in
        // the same section is a different key and races normally — and it self-expires.
        if (errorMessage.Contains("All destinations denied this release", StringComparison.OrdinalIgnoreCase))
            return (NoActivityTtl, "mkdir-denied");

        return null;
    }

}
