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
    private readonly MetadataFilterService _metadataFilter;
    private readonly Lock _lock = new();
    private bool _disposed;

    public Func<string, FtpConnectionPool?>? _getMainPool;

    public event Action<string, string, string>? AutoRaceAttempted; // section, release, result

    public IReadOnlyList<SpreadJob> ActiveJobs
    {
        get { lock (_lock) return _activeJobs.ToList(); }
    }

    public RaceHistoryStore History => _history;

    public event Action<SpreadJob>? JobStarted;
    public event Action<SpreadJob>? JobCompleted;
    public event Action<SpreadJob>? JobProgressChanged;

    public SpreadManager(AppConfig config)
    {
        _config = config;
        _metadataFilter = new MetadataFilterService(config);
        _history.Load();
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
        var poolSize = Math.Max(_config.Spread.SpreadPoolSize, 1);
        if (poolSize <= 0) return;

        var pool = new FtpConnectionPool(factory, poolSize);
        try
        {
            await pool.Initialize(ct);
            lock (_lock)
            {
                _spreadPools[serverId] = pool;
                _factories[serverId] = factory;
            }
            Log.Information("Spread pool initialized for {ServerId} (size={Size})", serverId, poolSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize spread pool for {ServerId}", serverId);
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

            if (_activeJobs.Count >= maxRaces)
            {
                _raceQueue.Enqueue(new PendingRace(section, releaseName, serverIds.ToList(), mode));
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
            pools, mainPools, configs, _speedTracker, _skiplist,
            knownSourceServerId, knownSourcePath);

        job.ProgressChanged += j => JobProgressChanged?.Invoke(j);
        job.Completed += j =>
        {
            lock (_lock) _activeJobs.Remove(j);
            RecordHistory(j);
            JobCompleted?.Invoke(j);
            DequeueNextRace();
        };
        job.Error += (j, msg) =>
        {
            lock (_lock) _activeJobs.Remove(j);
            RecordHistory(j);
            Log.Warning("Spread job error: {Release} — {Error}", j.ReleaseName, msg);
            DequeueNextRace();
        };

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
                SkiplistTrace = job.SkiplistTrace
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
                var neededSize = Math.Max(_config.Spread.SpreadPoolSize, 1);

                if (pool.MaxSize < neededSize)
                {
                    // Pool is undersized — replace it entirely
                    FtpClientFactory? factory;
                    lock (_lock) _factories.TryGetValue(id, out factory);
                    if (factory != null)
                    {
                        await pool.DisposeAsync();
                        var newPool = new FtpConnectionPool(factory, neededSize);
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
            StartRaceInternal(next.Section, next.ReleaseName, next.ServerIds, next.Mode);
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
            AutoRaceAttempted?.Invoke(category, releaseName, $"Skipped — <2 connected servers with sections");
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

        // Need at least 2 participating servers for a race — source + dest.
        if (allowed.Count < 2)
        {
            var reason = denials.Count > 0
                ? $"Only {allowed.Count} server(s) allowed (denied: {string.Join(", ", denials)})"
                : $"Only {allowed.Count} server(s) allowed";
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
            _config.Spread.TransferTimeoutSeconds, ct);

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
                _config.Spread.TransferTimeoutSeconds, ct);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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

    private static bool IsSameRace(string sectionA, string releaseA, string sectionB, string releaseB) =>
        string.Equals(releaseA, releaseB, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(sectionA, sectionB, StringComparison.OrdinalIgnoreCase);

    private record PendingRace(string Section, string ReleaseName, List<string> ServerIds, SpreadMode Mode);
}
