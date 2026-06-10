using System.Collections.Concurrent;
using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public class SpreadManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, FtpConnectionPool> _spreadPools = new();
    private readonly Dictionary<string, FtpClientFactory> _factories = new();
    private readonly List<SpreadJob> _activeJobs = new();
    private readonly Queue<PendingRace> _raceQueue = new();
    private readonly SpeedTracker _speedTracker = new();
    private readonly SkiplistEvaluator _skiplist = new();
    private readonly RaceHistoryStore _history = new();
    private readonly SectionBlacklistStore _blacklist = new();
    private readonly MetadataFilterService _metadataFilter;
    private readonly Lock _lock = new();
    private bool _disposed;

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
                    InCooldown: pool.IsInCooldown));
            }
        }
        return list;
    }
    public SectionBlacklistStore Blacklist => _blacklist;

    public event Action<SpreadJob>? JobStarted;
    public event Action<SpreadJob>? JobCompleted;
    public event Action<SpreadJob>? JobProgressChanged;

    public SpreadManager(AppConfig config)
    {
        _config = config;
        _metadataFilter = new MetadataFilterService(config);
        _history.Load();
        _blacklist.Load();
        _speedTracker.EnablePersistence(
            Path.Combine(ConfigManager.AppDataPath, "spread-speed-history.json"));
    }

    public async Task InitializePool(string serverId, FtpClientFactory factory, CancellationToken ct)
    {
        if (_disposed) return;

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
        if (poolSize <= 0) return;

        // Share the account-wide login gate with the main + download pools for this
        // server (same host:port:username key) so the spread pool's logins count
        // against the SAME account cap — this is the linchpin that makes the cap
        // truly account-wide instead of per-pool.
        var loginGate = ResolveAccountLoginGate(serverId);
        // priorityLogins: true — draw from the gate's reserved FXP permit so the main
        // pool can't starve racing out of every login (the v3.7.2 root-cause fix).
        var pool = new FtpConnectionPool(factory, poolSize, loginGate, priorityLogins: true);
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
        try
        {
            await pool.Initialize(ct);
            lock (_lock)
            {
                _spreadPools[serverId] = pool;
                _factories[serverId] = factory;
                _topologyVersion++; // invalidate the section-feasibility cache
            }
            // Ensure the gate exists from the moment the pool comes up.
            GetOrCreateGate(serverId);
            Log.Information("Spread pool initialized for {ServerId} (size={Size}, gate={Gate})",
                serverId, poolSize, DefaultPerServerConcurrentTransfers);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize spread pool for {ServerId} — racing DISABLED for this server " +
                            "until restart (login-cap starvation? FXP permit is reserved as of v3.7.2)", serverId);
            await pool.DisposeAsync();
        }
    }

    public async Task DisposePool(string serverId)
    {
        FtpConnectionPool? pool;
        lock (_lock)
        {
            _spreadPools.Remove(serverId, out pool);
            _factories.Remove(serverId);
            _topologyVersion++; // invalidate the section-feasibility cache
        }

        if (pool != null)
            await pool.DisposeAsync();
    }

    public SpreadJob? StartRace(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        // Sanitize inputs
        releaseName = SanitizeFtpPath(releaseName);
        section = SanitizeFtpPath(section);

        // Dedup the participant list — callers may pass the same server twice
        // (e.g. RequestFiller passing [sourceId, requesterId] where they match,
        // or UI code merging lists). A dup would crash StartRaceInternal's
        // ToDictionary with ArgumentException.
        serverIds = serverIds.Distinct().ToList();

        var maxRaces = Math.Max(_config.Spread.MaxConcurrentRaces, 1);
        lock (_lock)
        {
            // Dedup: skip if this release is already racing or queued.
            if (_activeJobs.Any(j => IsSameRace(j.Section, j.ReleaseName, section, releaseName)))
            {
                Log.Information("Race already active, skipping duplicate: {Release}", releaseName);
                return null;
            }
            if (_raceQueue.Any(q => IsSameRace(q.Section, q.ReleaseName, section, releaseName)))
            {
                Log.Information("Race already queued, skipping duplicate: {Release}", releaseName);
                return null;
            }

            // Issue #4 (+ v1.66 expansion): short-circuit re-queues of a release
            // that recently failed with a known dead-end class (affil-blocked,
            // release-not-found, no-activity). The TTL map is populated by the
            // job.Error handler via ClassifyDeadRace.
            if (IsRecentlyDeadRace(section, releaseName, out var deadReason))
            {
                Log.Debug("Spread: skipping {Release} — recently failed: {Reason}", releaseName, deadReason);
                return null;
            }

            if (_activeJobs.Count >= maxRaces)
            {
                _raceQueue.Enqueue(new PendingRace(section, releaseName, serverIds.ToList(), mode, knownSourceServerId, knownSourcePath));
                Log.Information("Race queued (max concurrent {Max}): {Release}", maxRaces, releaseName);
                return null;
            }
        }

        return StartRaceInternal(section, releaseName, serverIds, mode,
            knownSourceServerId, knownSourcePath);
    }

    private SpreadJob StartRaceInternal(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        Dictionary<string, FtpConnectionPool> pools;
        Dictionary<string, ServerConfig> configs;

        lock (_lock)
        {
            pools = serverIds
                .Where(id => _spreadPools.ContainsKey(id))
                .ToDictionary(id => id, id => _spreadPools[id]);

            configs = serverIds
                .Where(id => _config.Servers.Any(s => s.Id == id))
                .ToDictionary(id => id, id => _config.Servers.First(s => s.Id == id));
        }

        if (pools.Count < 2)
            throw new InvalidOperationException(
                $"Need at least 2 connected spread pools (have {pools.Count}). " +
                $"Connected pools: [{string.Join(", ", _spreadPools.Keys)}], " +
                $"requested: [{string.Join(", ", serverIds)}]");

        // Collect main server pools for scanning (spread pools are for FXP only)
        var mainPools = new Dictionary<string, FtpConnectionPool>();
        if (_getMainPool != null)
        {
            foreach (var id in serverIds)
            {
                var mainPool = _getMainPool(id);
                if (mainPool != null) mainPools[id] = mainPool;
            }
        }

        var job = new SpreadJob(section, releaseName, mode, _config.Spread,
            pools, mainPools, configs, _speedTracker, _skiplist, _blacklist,
            knownSourceServerId, knownSourcePath);

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
            Log.Warning("Spread job error: {Release} — {Error}", j.ReleaseName, msg);

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
        job.SourceSearch = async (release, exclude, ct) =>
        {
            var r = await SearchReleaseOnServers(release, exclude, ct);
            return r == null ? null : (r.ServerId, r.RemotePath, r.Category);
        };
        job.ServerConfigResolver = id => _config.Servers.FirstOrDefault(s => s.Id == id);

        lock (_lock) _activeJobs.Add(job);
        JobStarted?.Invoke(job);

        var sids = serverIds;
        _ = Task.Run(async () =>
        {
            try
            {
                // Re-initialize dead spread pools before running.
                // Pools die when all connections are poisoned/discarded (GnuTLS crashes,
                // network errors) and there's no keepalive/reconnect for spread pools.
                await ReinitDeadPools(sids);

                // Re-capture pool snapshot AFTER reinit — ReinitDeadPools may have
                // replaced an exhausted undersized pool with a brand new instance,
                // and the job's original snapshot would point at the disposed pool.
                Dictionary<string, FtpConnectionPool> fresh;
                lock (_lock)
                {
                    fresh = sids
                        .Where(id => _spreadPools.ContainsKey(id))
                        .ToDictionary(id => id, id => _spreadPools[id]);
                }

                if (fresh.Count < 2)
                {
                    // A participating server was unmounted between StartRace() and here —
                    // not enough pools to race. Fail the job cleanly instead of running
                    // with stale references.
                    Log.Warning("Spread job {Release}: only {Count} pools available after reinit, aborting",
                        job.ReleaseName, fresh.Count);
                    return;
                }

                job.UpdatePools(fresh);

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
                lock (_lock) wasActive = _activeJobs.Remove(job);
                if (wasActive)
                {
                    RecordHistory(job);
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
            // sites). Destinations = non-source sites. FilesDelivered = best dest's
            // owned count. CleanComplete = every dest got the full set.
            var filesTotal = job.Sites.Values.Any() ? job.Sites.Values.Max(s => s.FilesTotal) : 0;
            var dests = job.Sites.Values.Where(s => !s.IsSource).ToList();
            var filesDelivered = dests.Count > 0 ? dests.Max(s => s.FilesOwned) : 0;
            var cleanComplete = job.State == SpreadJobState.Completed
                && filesTotal > 0
                && dests.Count > 0
                && dests.All(s => s.FilesOwned >= filesTotal);

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
                SkiplistTrace = job.SkiplistTrace,
                FilesTotal = filesTotal,
                FilesDelivered = filesDelivered,
                CleanComplete = cleanComplete,
                Score = job.Score,
                FailureCategory = job.State == SpreadJobState.Failed
                    ? SpreadJob.ClassifyFailure(job.LastError)
                    : "",
                DestinationState = job.DestinationStateSummary(),
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
            }

            if (!pool.IsExhausted) continue;

            var serverName = _config.Servers.FirstOrDefault(s => s.Id == id)?.Name ?? id;
            Log.Warning("Spread pool exhausted for {Server} — reinitializing", serverName);
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
                        await pool.DisposeAsync();
                        var newPool = new FtpConnectionPool(factory, neededSize, ResolveAccountLoginGate(id), priorityLogins: true);
                        newPool.ConfigureHealth(_config.Spread.ValidateConnectionOnBorrow, _config.Spread.SpreadKeepaliveSeconds);
                        await newPool.Initialize(CancellationToken.None);
                        lock (_lock) _spreadPools[id] = newPool;
                        Log.Information("Spread pool replaced for {Server} (old size={Old}, new size={New})",
                            serverName, pool.MaxSize, neededSize);
                        continue;
                    }
                }

                await pool.Reinitialize(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to reinitialize spread pool for {Server}", serverName);
            }
        }
    }

    private void DequeueNextRace()
    {
        PendingRace? next;
        lock (_lock)
        {
            var maxRaces = Math.Max(_config.Spread.MaxConcurrentRaces, 1);
            if (_raceQueue.Count == 0 || _activeJobs.Count >= maxRaces)
                return;
            next = _raceQueue.Dequeue();
        }

        try
        {
            StartRaceInternal(next.Section, next.ReleaseName, next.ServerIds, next.Mode,
                next.KnownSourceServerId, next.KnownSourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start queued race: {Release}", next.ReleaseName);
        }
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
            // logs" actually shows why racing is dead.
            int livePools;
            lock (_lock) livePools = _spreadPools.Count;
            Log.Warning("Auto-race skipped for {Release} [{Section}]: only {Count} connected spread pool(s) " +
                        "(need 2). Live FXP pools={Live} — if 0, spread pool init failed (check login cap).",
                releaseName, category, serverIds.Count, livePools);
            AutoRaceAttempted?.Invoke(category, releaseName,
                $"Skipped — only {serverIds.Count} connected server(s) with FXP pool (live pools={livePools})");
            return;
        }

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
        _ = Task.Run(async () =>
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
        foreach (var serverId in serverIds)
        {
            var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (serverConfig == null) continue;
            // MKD path denials don't make a section infeasible — the dest joins
            // as fill-only (see TryAutoRaceInternalAsync blacklist exception).
            if (_blacklist.IsBlacklisted(serverId, category)
                && !MkdFailureClassifier.IsPermanentMkdPathDenial(_blacklist.Get(serverId, category)?.Reason ?? ""))
            { missing.Add(serverConfig.Name); continue; }
            if (SectionMapper.HasSectionFor(serverConfig.SpreadSite, category))
                eligible.Add(serverId);
            else
                missing.Add(serverConfig.Name);
        }

        if (eligible.Count >= 2)
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

            // Blacklist check: sites that previously failed MKD permanently for
            // this section (550 path-filter, permission denied) are dropped here
            // so we don't waste borrow timeouts rediscovering the same NO every
            // race. SpreadJob also enforces this at dest selection for manually
            // started (non-auto) races. EXCEPTION: an MKD *path* denial only
            // means the account can't CREATE dirs — SpreadJob admits such dests
            // as fill-only (they receive once another racer creates the dir),
            // so they must stay in the participant list here.
            if (_blacklist.IsBlacklisted(serverId, category))
            {
                var entry = _blacklist.Get(serverId, category);
                var reason = entry?.Reason ?? "permanent MKD failure";
                if (!MkdFailureClassifier.IsPermanentMkdPathDenial(reason))
                {
                    denials.Add($"{serverConfig.Name}: blacklisted ({reason})");
                    continue;
                }
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
                var verdict = await _metadataFilter.EvaluateAsync(metaFilter, releaseName, parsed);
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
                Log.Information("Auto-race started: {Release} [{Section}] across {Count} servers",
                    releaseName, category, allowed.Count);
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
        SpreadJob? job;
        lock (_lock)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.Stop();
    }

    public async Task StartFxp(string srcServerId, string srcPath,
        string dstServerId, string dstPath, CancellationToken ct)
    {
        srcPath = SanitizeFtpPath(srcPath);
        dstPath = SanitizeFtpPath(dstPath);

        FtpConnectionPool srcPool, dstPool;
        lock (_lock)
        {
            if (!_spreadPools.TryGetValue(srcServerId, out srcPool!))
                throw new InvalidOperationException($"No spread pool for source server {srcServerId}");
            if (!_spreadPools.TryGetValue(dstServerId, out dstPool!))
                throw new InvalidOperationException($"No spread pool for dest server {dstServerId}");
        }

        var mode = FxpModeDetector.Detect(srcPool, dstPool);
        await using var srcConn = await srcPool.Borrow(ct);
        await using var dstConn = await dstPool.Borrow(ct);

        var transfer = new FxpTransfer();
        var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
            _config.Spread.TransferTimeoutSeconds, ct,
            raceId: $"direct-{Guid.NewGuid():N}"[..16],
            srcServerId: srcServerId, dstServerId: dstServerId);

        if (!ok)
        {
            // Poison connections after failed transfer — GnuTLS session may be corrupt
            srcConn.Poisoned = true;
            dstConn.Poisoned = true;
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
        await EnumerateDirectoryRecursive(srcPool, srcPath, srcPath, files, 0, ct);

        var mode = FxpModeDetector.Detect(srcPool, dstPool);

        foreach (var (relativePath, fullPath) in files)
        {
            var destFile = dstPath.TrimEnd('/') + "/" + relativePath;

            await using var srcConn = await srcPool.Borrow(ct);
            await using var dstConn = await dstPool.Borrow(ct);

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
                srcConn.Poisoned = true;
                dstConn.Poisoned = true;
            }
        }
    }

    private static async Task EnumerateDirectoryRecursive(FtpConnectionPool pool,
        string basePath, string currentPath, List<(string relative, string full)> files,
        int depth, CancellationToken ct)
    {
        if (depth > 5) return;

        await using var conn = await pool.Borrow(ct);

        FluentFTP.FtpListItem[] items;
        if (pool.UseCpsv)
            items = await CpsvDataHelper.ListDirectory(conn.Client, currentPath, pool.ControlHost, ct);
        else
            items = await conn.Client.GetListing(currentPath, FluentFTP.FtpListOption.AllFiles, ct);

        foreach (var item in items)
        {
            if (item.Type == FluentFTP.FtpObjectType.Directory)
            {
                await EnumerateDirectoryRecursive(pool, basePath, item.FullName, files, depth + 1, ct);
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

    /// <summary>
    /// Search every connected server (except <paramref name="excludeIds"/>) in
    /// parallel for an exact match of <paramref name="release"/> and return the best
    /// alternate source. Candidates failing skiplist rules or section blacklist are
    /// dropped. Returns null when nothing usable is found. Reused by RequestFiller and
    /// by SpreadJob's mid-race source-migration failover.
    /// </summary>
    public async Task<SearchResult?> SearchReleaseOnServers(
        string release, IReadOnlyCollection<string> excludeIds, CancellationToken ct)
    {
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
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var results = await svc.Search(release, null, cts.Token);
                var exact = results.FirstOrDefault(r =>
                    r.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase));
                if (exact == null) return null;
                exact.ServerId = id; // ensure attribution
                return exact;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Alternate-source search failed on {Server}", id);
                return null;
            }
        });

        var found = (await Task.WhenAll(searches)).Where(r => r != null).Select(r => r!).ToList();
        if (found.Count == 0) return null;

        // Filter by rules + blacklist; prefer the candidate with the most files.
        var serverConfigs = _config.Servers.ToDictionary(s => s.Id, s => s);
        SearchResult? best = null;
        foreach (var r in found.OrderByDescending(r => r.Files?.Count ?? 0))
        {
            if (!serverConfigs.TryGetValue(r.ServerId, out var sc)) continue;
            if (_blacklist.IsBlacklisted(r.ServerId, r.Category)) continue;
            var action = _skiplist.Evaluate(release, true, false, r.ServerId, r.Category,
                sc.SpreadSite.Skiplist, _config.Spread.GlobalSkiplist);
            if (action == SkiplistAction.Deny) continue;
            best = r;
            break;
        }
        return best;
    }

    private ServerGate GetOrCreateGate(string serverId) =>
        _serverGates.GetOrAdd(serverId, id =>
        {
            // Cap concurrent transfer PAIRS strictly below the account's login
            // budget so one permit always remains for scans/cleanup. With
            // usable=3 logins and a gate of 3, active transfers consumed every
            // permit: mid-race source LISTs starved for 25+ minutes and new
            // transfer borrows piled into 30s timeouts (My.Family 2026-06-10,
            // ended 19/22 with 5 undelivered). usable-1 keeps the engine's
            // eyes open; 530 auto-tune can still tighten further.
            var cap = DefaultPerServerConcurrentTransfers;
            var lg = ResolveAccountLoginGate(id);
            if (lg != null) cap = Math.Max(1, Math.Min(cap, lg.Limit - 1));
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
        if (_disposed) return;
        _disposed = true;

        // Flush speed history before tearing down — debounced saves may have
        // skipped recent transfers, and the whole point of persistence is
        // having the data available on next launch.
        try { _speedTracker.Save(); }
        catch (Exception ex) { Log.Debug(ex, "SpreadManager.Dispose: speed-tracker save failed"); }

        foreach (var gate in _serverGates.Values)
        {
            try { gate.Dispose(); } catch { }
        }
        _serverGates.Clear();

        _metadataFilter.Dispose();

        List<SpreadJob> jobs;
        lock (_lock)
        {
            jobs = _activeJobs.ToList();
            _activeJobs.Clear();
            _raceQueue.Clear();
        }
        foreach (var job in jobs)
            job.Stop();

        List<FtpConnectionPool> pools;
        lock (_lock)
        {
            pools = _spreadPools.Values.ToList();
            _spreadPools.Clear();
            _factories.Clear();
        }

        // Dispose pools on the threadpool and block briefly — fire-and-forget
        // risks leaving native GnuTLS sessions open past process exit.
        Task.Run(async () =>
        {
            foreach (var pool in pools)
            {
                try { await pool.DisposeAsync(); }
                catch (Exception ex) { Log.Debug(ex, "Spread pool dispose error"); }
            }
        }).Wait(TimeSpan.FromSeconds(5));

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
    private bool IsRecentlyDeadRace(string section, string release, out string? reason)
    {
        var key = DeadRaceKey(section, release);
        if (!_recentlyDeadRaces.TryGetValue(key, out var entry))
        {
            reason = null;
            return false;
        }
        if (entry.expiry > DateTime.UtcNow)
        {
            reason = entry.reason;
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
    private static (TimeSpan ttl, string reason)? ClassifyDeadRace(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return null;

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

        return null;
    }

    private record PendingRace(string Section, string ReleaseName, List<string> ServerIds, SpreadMode Mode,
        string? KnownSourceServerId = null, string? KnownSourcePath = null);
}
