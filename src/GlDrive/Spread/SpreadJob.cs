using System.IO;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public enum SpreadJobState { Running, Completed, Failed, Stopped }

public class SiteProgress
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int FilesOwned { get; set; }
    public int FilesTotal { get; set; }
    public long BytesTransferred { get; set; }
    public int ActiveTransfers { get; set; }
    public double SpeedBps { get; set; }
    public bool IsComplete { get; set; }
    public bool IsSource { get; set; }
}

public class SpreadJob : IDisposable
{
    private readonly SpreadConfig _spreadConfig;
    private readonly Dictionary<string, FtpConnectionPool> _pools;
    private readonly Dictionary<string, ServerConfig> _serverConfigs;
    private readonly SpeedTracker _speedTracker;
    private readonly SkiplistEvaluator _skiplist;
    private readonly CancellationTokenSource _cts = new();

    // Split locks by concern to reduce contention
    private readonly Lock _ownershipLock = new();   // _fileOwnership, _fileInfos, _expectedFileCount
    private readonly Lock _progressLock = new();    // _siteProgress, _activeTransfers
    private readonly Lock _failureLock = new();     // _failureCounts

    // File tracking (OrdinalIgnoreCase: FTP servers may return different casing)
    private readonly Dictionary<string, HashSet<string>> _fileOwnership = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpreadFileInfo> _fileInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkiplistAction> _fileActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _serverFileCount = new(); // per-server owned file count
    private readonly Dictionary<string, SiteProgress> _siteProgress = new();
    private int _expectedFileCount;
    private (string serverId, string path)? _pendingSfv;

    // Transfer tracking (file name component uses OrdinalIgnoreCase)
    private readonly Dictionary<(string file, string src, string dst), int> _failureCounts =
        new(new FileRouteTupleComparer());
    private readonly Dictionary<string, ActiveTransferInfo> _activeTransfers = new();
    private readonly HashSet<(string fileName, string dstId)> _inFlightFiles =
        new(new FileDstTupleComparer());

    // Chain mode: only one route (src→dst) active per release at a time.
    // Prevents connection exhaustion from parallel multi-site transfers.
    // Once current route has no more files to send, picks next hop.
    private (string srcId, string dstId)? _activeRoute;

    // Directory cleanup: track created dirs and successful transfers per destination
    private readonly HashSet<string> _dirsCreated = new(); // serverId values that got MKD
    private readonly HashSet<string> _serversWithSuccessfulTransfer = new();

    // Skiplist evaluation trace (captured in Phase 0 for history popup)
    public List<SkiplistTraceEntry>? SkiplistTrace { get; private set; }
    public string SkiplistResult { get; private set; } = "Allowed";

    // Scan debouncing
    private DateTime _lastScanTime = DateTime.MinValue;
    private Task? _backgroundScan;
    private volatile bool _forceScan;
    private bool _isNuked;
    private int _completionRetries;

    // Pre-computed affil checks (computed once per job)
    private readonly Dictionary<string, bool> _affilCache = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string ReleaseName { get; }
    public string Section { get; }
    public SpreadMode Mode { get; }
    public SpreadJobState State { get; private set; } = SpreadJobState.Running;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, SiteProgress> Sites => _siteProgress;
    public IReadOnlyList<ActiveTransferInfo> ActiveTransferList
    {
        get { lock (_progressLock) return _activeTransfers.Values.ToList(); }
    }

    public event Action<SpreadJob>? ProgressChanged;
    public event Action<SpreadJob>? Completed;
    public event Action<SpreadJob, string>? Error;

    private readonly string? _knownSourceServerId;
    private readonly string? _knownSourcePath;
    private readonly Dictionary<string, FtpConnectionPool> _mainPools;

    public SpreadJob(string section, string releaseName, SpreadMode mode,
        SpreadConfig spreadConfig,
        Dictionary<string, FtpConnectionPool> pools,
        Dictionary<string, FtpConnectionPool> mainPools,
        Dictionary<string, ServerConfig> serverConfigs,
        SpeedTracker speedTracker, SkiplistEvaluator skiplist,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        Section = section;
        ReleaseName = releaseName;
        Mode = mode;
        _spreadConfig = spreadConfig;
        _pools = pools;
        _serverConfigs = serverConfigs;
        _speedTracker = speedTracker;
        _skiplist = skiplist;
        _knownSourceServerId = knownSourceServerId;
        _knownSourcePath = knownSourcePath;
        _mainPools = mainPools;

        foreach (var (serverId, pool) in pools)
        {
            var config = serverConfigs[serverId];
            _siteProgress[serverId] = new SiteProgress
            {
                ServerId = serverId,
                ServerName = config.Name
            };

            // Pre-compute affil check once
            _affilCache[serverId] = config.SpreadSite.Affils.Count > 0 &&
                config.SpreadSite.Affils.Any(g =>
                    releaseName.Contains(g, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var sitePaths = new Dictionary<string, string>();

        try
        {
            // Phase 0: Check release name against directory-level skiplist rules
            // This prevents spreading releases that match deny patterns like *GERMAN*, *CADCAM*, etc.
            // Capture the full evaluation trace for the history detail popup.
            var allTrace = new List<SkiplistTraceEntry>();
            foreach (var (serverId, config) in _serverConfigs)
            {
                var siteRules = config.SpreadSite.Skiplist;
                var globalRules = _spreadConfig.GlobalSkiplist;
                var (action, trace) = _skiplist.EvaluateWithTrace(ReleaseName, true, false,
                    Section, siteRules, globalRules);
                foreach (var t in trace)
                    t.Source = $"{config.Name}/{t.Source}";
                allTrace.AddRange(trace);
                if (action == SkiplistAction.Deny)
                {
                    SkiplistTrace = allTrace;
                    var matchedRule = trace.FirstOrDefault(t => t.IsMatch);
                    SkiplistResult = $"Denied by: {matchedRule?.Pattern} (on {config.Name})";
                    SetFailed($"Release denied by skiplist on {config.Name}: {ReleaseName}");
                    return;
                }
            }
            SkiplistTrace = allTrace;
            SkiplistResult = "Allowed";

            // Phase 1: Discover which servers already have the release
            var sourceServers = new HashSet<string>();

            // If we have a known source (from notification/search), use it directly
            if (!string.IsNullOrEmpty(_knownSourceServerId) && !string.IsNullOrEmpty(_knownSourcePath)
                && _pools.ContainsKey(_knownSourceServerId))
            {
                sitePaths[_knownSourceServerId] = _knownSourcePath;
                sourceServers.Add(_knownSourceServerId);
                var srcName = _serverConfigs.TryGetValue(_knownSourceServerId, out var srcCfg) ? srcCfg.Name : _knownSourceServerId;
                Log.Information("Spread: known source {Server} at {Path}", srcName, _knownSourcePath);
            }

            // Probe remaining servers for the release
            foreach (var (serverId, config) in _serverConfigs)
            {
                if (sitePaths.ContainsKey(serverId)) continue; // Already known
                if (!_pools.TryGetValue(serverId, out var pool)) continue;

                // Probe all section paths AND the notification watch path
                var pathsToProbe = config.SpreadSite.Sections.Values.ToList();
                if (!string.IsNullOrEmpty(config.Notifications.WatchPath))
                {
                    // Watch path categories: /recent/tv-hd, /recent/x265, etc.
                    // Try the section hint as a subdirectory of the watch path
                    var watchBase = config.Notifications.WatchPath.TrimEnd('/');
                    var normSection = Section.ToLowerInvariant().Replace("_", "-");
                    pathsToProbe.Add($"{watchBase}/{normSection}");
                }

                foreach (var basePath in pathsToProbe.Distinct())
                {
                    try
                    {
                        var probePath = basePath.TrimEnd('/') + "/" + ReleaseName;
                        await using var conn = await pool.Borrow(ct);
                        if (await conn.Client.DirectoryExists(probePath, ct))
                        {
                            sitePaths[serverId] = probePath;
                            sourceServers.Add(serverId);
                            Log.Information("Spread: {Server} HAS release at {Path}", config.Name, probePath);
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }

            if (sourceServers.Count == 0)
            {
                SetFailed("Release not found on any server — check release name and section paths");
                return;
            }

            // Phase 2: For servers that DON'T have the release, create the destination path
            // using the section mapping (spread TO them)
            foreach (var (serverId, config) in _serverConfigs)
            {
                if (sitePaths.ContainsKey(serverId)) continue; // Already has it
                if (config.SpreadSite.DownloadOnly) continue; // Can't upload to download-only

                // Find the best section path on this server
                var sectionMatch = config.SpreadSite.Sections
                    .FirstOrDefault(kvp => kvp.Key.Equals(Section, StringComparison.OrdinalIgnoreCase));

                // If no exact match, try fuzzy
                if (string.IsNullOrEmpty(sectionMatch.Value))
                {
                    var normSection = Section.ToLowerInvariant().Replace("-", "").Replace("_", "");
                    sectionMatch = config.SpreadSite.Sections
                        .FirstOrDefault(kvp => kvp.Key.ToLowerInvariant().Replace("-", "").Replace("_", "") == normSection);
                }

                // Last resort: try substring match
                if (string.IsNullOrEmpty(sectionMatch.Value))
                {
                    var normSection = Section.ToLowerInvariant().Replace("-", "").Replace("_", "");
                    sectionMatch = config.SpreadSite.Sections
                        .FirstOrDefault(kvp =>
                        {
                            var normKey = kvp.Key.ToLowerInvariant().Replace("-", "").Replace("_", "");
                            return normKey.Contains(normSection) || normSection.Contains(normKey);
                        });
                }

                if (!string.IsNullOrEmpty(sectionMatch.Value))
                {
                    var destPath = sectionMatch.Value.TrimEnd('/') + "/" + ReleaseName;
                    sitePaths[serverId] = destPath;
                    Log.Information("Spread: {Server} is DESTINATION at [{Section}] {Path}",
                        config.Name, sectionMatch.Key, destPath);
                    // Directory is created lazily in ExecuteTransfer → EnsureDirectoryExists
                    // only when we actually have a file to transfer
                }
                else
                {
                    Log.Debug("Spread: {Server} has no matching section for [{Section}]", config.Name, Section);
                }
            }

            if (sitePaths.Count < 2)
            {
                SetFailed($"Need 2+ servers — found release on {sourceServers.Count}, " +
                    $"no destination servers have a matching section for [{Section}]");
                return;
            }

            // Phase 2b: Check if any destination is actually reachable (not blocked by affil)
            // A server is a viable destination if it doesn't already have the release,
            // isn't downloadOnly, and isn't affil-blocked for this release.
            var viableDestinations = sitePaths.Keys
                .Where(id => !sourceServers.Contains(id))
                .Where(id => !_serverConfigs[id].SpreadSite.DownloadOnly)
                .Where(id => !_affilCache.GetValueOrDefault(id))
                .ToList();

            if (viableDestinations.Count == 0)
            {
                var affilBlocked = sitePaths.Keys
                    .Where(id => !sourceServers.Contains(id) && _affilCache.GetValueOrDefault(id))
                    .Select(id => _serverConfigs[id].Name);
                SetFailed($"No viable destinations — all targets are affil-blocked ({string.Join(", ", affilBlocked)}) for [{Section}] {ReleaseName}");
                return;
            }

            Log.Information("Spread starting: {Release} [{Section}] — {Sources} source(s), {Total} total servers",
                ReleaseName, Section, sourceServers.Count, sitePaths.Count);

            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hardTimeout.CancelAfter(TimeSpan.FromSeconds(_spreadConfig.HardTimeoutSeconds));
            var token = hardTimeout.Token;

            var lastActivity = DateTime.UtcNow;
            var scorer = new SpreadScorer(_speedTracker);
            var consecutiveEmpty = 0;

            while (!token.IsCancellationRequested && State == SpreadJobState.Running)
            {
                // 1. Background scan with adaptive debounce — faster during active racing
                if (_backgroundScan == null || _backgroundScan.IsCompleted)
                {
                    // Adaptive interval: 2s during active transfers, 5s when idle
                    int activeCount;
                    lock (_progressLock)
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                    var scanInterval = activeCount > 0 ? 2.0 : 5.0;

                    if (_forceScan || (DateTime.UtcNow - _lastScanTime).TotalSeconds >= scanInterval)
                    {
                        _forceScan = false;
                        _lastScanTime = DateTime.UtcNow;
                        _backgroundScan = ScanSites(sitePaths, token);
                    }
                }

                // Check for nuked release
                if (_isNuked)
                {
                    SetFailed("Release is NUKED — aborting race");
                    return;
                }

                // 2. Parse SFV if discovered (non-blocking — fire and forget)
                if (_pendingSfv is { } sfv && _expectedFileCount == 0)
                {
                    _pendingSfv = null;
                    _ = ParseSfvForCount(sfv.serverId, sfv.path);
                }

                // 3. Find best transfer — pre-compute speed map outside lock
                var transfer = FindBestTransfer(sitePaths, scorer);

                if (transfer == null)
                {
                    // Chain mode: clear route if no more files for it, so next
                    // iteration can pick a new route (e.g. B->C after A->B done)
                    if (_activeRoute is { } ar)
                    {
                        bool hasInFlight;
                        lock (_ownershipLock)
                            hasInFlight = _inFlightFiles.Any(f => f.dstId == ar.dstId);
                        if (!hasInFlight)
                        {
                            var srcName = _serverConfigs.TryGetValue(ar.srcId, out var sc) ? sc.Name : ar.srcId;
                            var dstName = _serverConfigs.TryGetValue(ar.dstId, out var dc) ? dc.Name : ar.dstId;
                            Log.Information("Spread chain: route {Src} -> {Dst} finished, picking next hop",
                                srcName, dstName);
                            _activeRoute = null;
                            _forceScan = true; // Rescan immediately — new files may have appeared
                            continue; // Re-evaluate immediately with no route lock
                        }
                    }

                    if (IsJobComplete())
                    {
                        State = SpreadJobState.Completed;
                        Completed?.Invoke(this);
                        return;
                    }

                    int activeCount;
                    int trackedTransfers;
                    lock (_progressLock)
                    {
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                        trackedTransfers = _activeTransfers.Count;

                        // Stale slot detection: if ActiveTransfers claims slots are used
                        // but no actual transfers are tracked, the counts are leaked
                        if (activeCount > 0 && trackedTransfers == 0 &&
                            (DateTime.UtcNow - lastActivity).TotalSeconds > 120)
                        {
                            Log.Warning("Stale slots detected: {Active} claimed but {Tracked} tracked — resetting",
                                activeCount, trackedTransfers);
                            foreach (var progress in _siteProgress.Values)
                                progress.ActiveTransfers = 0;
                            activeCount = 0;
                        }
                    }

                    if (activeCount == 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                    {
                        // Completion sweep: if destinations are still missing files,
                        // reset failure counters and try again (up to 3 retries)
                        int missingFiles;
                        lock (_ownershipLock)
                        {
                            missingFiles = 0;
                            foreach (var (serverId, _) in _siteProgress)
                            {
                                if (_serverConfigs[serverId].SpreadSite.DownloadOnly) continue;
                                var owned = _serverFileCount.GetValueOrDefault(serverId);
                                var total = _fileInfos.Count;
                                if (owned < total) missingFiles += total - owned;
                            }
                        }

                        if (missingFiles > 0 && _completionRetries < 3)
                        {
                            _completionRetries++;
                            lock (_failureLock) _failureCounts.Clear();
                            _forceScan = true;
                            _activeRoute = null;
                            lastActivity = DateTime.UtcNow;
                            consecutiveEmpty = 0;

                            // Reinitialize exhausted spread pools — kill ghosts and get fresh connections
                            foreach (var (serverId, pool) in _pools)
                            {
                                try
                                {
                                    await pool.Reinitialize(token);
                                }
                                catch (Exception ex)
                                {
                                    Log.Debug(ex, "Completion sweep: pool reinit failed for {Server}", serverId);
                                }
                            }

                            Log.Information("Spread completion sweep {Retry}/3: {Missing} files still missing on destinations, " +
                                "resetting failures and reinitializing pools — {Release}",
                                _completionRetries, missingFiles, ReleaseName);
                            continue;
                        }

                        // If we transferred any files, this is a partial completion, not a failure
                        bool hadTransfers;
                        lock (_ownershipLock)
                            hadTransfers = _serversWithSuccessfulTransfer.Count > 0;

                        if (hadTransfers)
                        {
                            State = SpreadJobState.Completed;
                            Completed?.Invoke(this);
                            Log.Information("Spread completed{Partial}: {Release} — {Missing} files undelivered",
                                missingFiles > 0 ? " (partial)" : "", ReleaseName, missingFiles);
                        }
                        else
                        {
                            SetFailed("No activity for 60 seconds, no viable transfers");
                        }
                        return;
                    }

                    // Adaptive backoff when idle
                    consecutiveEmpty++;
                    var delay = Math.Min(consecutiveEmpty * 1000, 5000);
                    await Task.Delay(delay, token);
                    continue;
                }

                lastActivity = DateTime.UtcNow;
                consecutiveEmpty = 0;

                // 4. Claim slots atomically before starting transfer
                var (file, srcId, dstId) = transfer.Value;

                // Chain mode: lock onto this route if none active
                if (_activeRoute == null)
                {
                    _activeRoute = (srcId, dstId);
                    var srcName = _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId;
                    var dstName = _serverConfigs.TryGetValue(dstId, out var dc) ? dc.Name : dstId;
                    Log.Information("Spread chain: locked route {Src} -> {Dst} for {Release}",
                        srcName, dstName, ReleaseName);
                }

                if (TryClaimSlots(srcId, dstId))
                {
                    // Mark file as in-flight to prevent duplicate transfers to same dest
                    lock (_ownershipLock) _inFlightFiles.Add((file.Name, dstId));

                    // Wrap with per-transfer hard timeout to prevent indefinite hangs
                    _ = Task.Run(async () =>
                    {
                        using var xferTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        xferTimeout.CancelAfter(TimeSpan.FromSeconds(
                            _spreadConfig.TransferTimeoutSeconds > 0
                                ? _spreadConfig.TransferTimeoutSeconds * 3  // 3x the normal timeout
                                : 180));
                        await ExecuteTransfer(file, srcId, dstId, sitePaths[dstId], xferTimeout.Token);
                    }, token);
                }

                // Brief pause between scheduling — enough time for next score round
                await Task.Delay(500, token);
            }

            if (State == SpreadJobState.Running)
                State = SpreadJobState.Stopped;
        }
        catch (OperationCanceledException)
        {
            if (State == SpreadJobState.Running)
                State = SpreadJobState.Stopped;
        }
        catch (Exception ex)
        {
            SetFailed(ex.Message);
        }
        finally
        {
            // Clean up empty directories on destinations where no files were transferred
            _ = CleanupEmptyDirs(sitePaths);
        }
    }

    /// <summary>
    /// Remove release directories on destinations where we created them (MKD) but
    /// never successfully transferred any files. Prevents empty folder litter.
    /// </summary>
    private async Task CleanupEmptyDirs(Dictionary<string, string> sitePaths)
    {
        HashSet<string> created, succeeded;
        lock (_ownershipLock)
        {
            created = [.._dirsCreated];
            succeeded = [.._serversWithSuccessfulTransfer];
        }

        foreach (var serverId in created)
        {
            if (succeeded.Contains(serverId)) continue; // Had successful transfers — keep dir
            if (!sitePaths.TryGetValue(serverId, out var path)) continue;
            if (!_pools.TryGetValue(serverId, out var pool)) continue;

            var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
            try
            {
                await using var conn = await pool.Borrow(CancellationToken.None);
                await conn.Client.Execute($"RMD {Ftp.CpsvDataHelper.SanitizeFtpPath(path)}", CancellationToken.None);
                Log.Information("Spread cleanup: removed empty dir {Path} on {Server}", path, serverName);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spread cleanup: failed to remove {Path} on {Server}", path, serverName);
            }
        }
    }

    private bool TryClaimSlots(string srcId, string dstId)
    {
        lock (_progressLock)
        {
            var srcProgress = _siteProgress[srcId];
            var dstProgress = _siteProgress[dstId];
            var srcConfig = _serverConfigs[srcId];
            var dstConfig = _serverConfigs[dstId];

            if (srcProgress.ActiveTransfers >= srcConfig.SpreadSite.MaxDownloadSlots) return false;
            if (dstProgress.ActiveTransfers >= dstConfig.SpreadSite.MaxUploadSlots) return false;

            srcProgress.ActiveTransfers++;
            dstProgress.ActiveTransfers++;
            return true;
        }
    }

    private async Task ScanSites(Dictionary<string, string> sitePaths, CancellationToken ct)
    {
        Log.Information("Spread scan starting for {Count} servers: {Paths}",
            sitePaths.Count, string.Join(", ", sitePaths.Select(kv =>
            {
                var name = _serverConfigs.TryGetValue(kv.Key, out var c) ? c.Name : kv.Key;
                return $"{name}:{kv.Value}";
            })));

        var results = new List<(string serverId, List<SpreadFileInfo> files)>();
        var scanLock = new Lock();

        var tasks = sitePaths.Select(async kvp =>
        {
            var (serverId, basePath) = kvp;
            var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
            try
            {
                // Prefer main server pool for scanning (has keepalive/reconnect)
                // Fall back to spread pool if main pool unavailable
                FtpConnectionPool? pool = null;
                if (_mainPools.TryGetValue(serverId, out var mainPool))
                    pool = mainPool;
                else if (_pools.TryGetValue(serverId, out var spreadPool))
                    pool = spreadPool;

                if (pool == null)
                {
                    Log.Warning("Spread scan: no pool for {Server}", serverName);
                    return;
                }
                Log.Information("Spread scan: listing {Server} at {Path} (using {PoolType} pool)...",
                    serverName, basePath, _mainPools.ContainsKey(serverId) ? "main" : "spread");
                var files = new List<SpreadFileInfo>();
                await ScanDirectoryRecursive(pool, basePath, basePath, files, 0, ct);
                Log.Information("Spread scan: {Server} returned {Count} files", serverName, files.Count);
                lock (scanLock) results.Add((serverId, files));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spread scan FAILED for {Server} at {Path}", serverName, basePath);
            }
        });

        await Task.WhenAll(tasks);

        // Process all results under lock once
        lock (_ownershipLock)
        {
            foreach (var (serverId, files) in results)
            {
                var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
                Log.Information("Spread scan: {Server} found {Count} files at {Path}",
                    serverName, files.Count, sitePaths.GetValueOrDefault(serverId, "?"));
                ProcessFiles(serverId, files);
            }

            if (results.Count == 0)
                Log.Warning("Spread scan: ALL scans failed or returned 0 results");
        }
    }

    private async Task ScanDirectoryRecursive(FtpConnectionPool pool, string basePath,
        string currentPath, List<SpreadFileInfo> files, int depth, CancellationToken ct)
    {
        if (depth > 3) return; // Max recursion depth

        // Timeout on borrow — don't wait forever if pool is exhausted
        using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borrowCts.CancelAfter(TimeSpan.FromSeconds(15));
        await using var conn = await pool.Borrow(borrowCts.Token);

        FtpListItem[] items;
        try
        {
            if (pool.UseCpsv)
                items = await CpsvDataHelper.ListDirectory(conn.Client, currentPath, pool.ControlHost, ct);
            else
                items = await conn.Client.GetListing(currentPath, FtpListOption.AllFiles, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation mid-read poisons the GnuTLS stream — discard this connection
            conn.Poisoned = true;
            throw;
        }
        catch (IOException)
        {
            conn.Poisoned = true;
            throw;
        }

        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.Directory)
            {
                // Check for nuke markers
                foreach (var marker in _spreadConfig.NukeMarkers)
                {
                    if (item.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        _isNuked = true;
                        return;
                    }
                }

                // Recurse into subdirectories (Sample/, Subs/, CD1/, etc.)
                await ScanDirectoryRecursive(pool, basePath, item.FullName, files, depth + 1, ct);
            }
            else if (item.Type == FtpObjectType.File)
            {
                // Check for nuke marker files
                foreach (var marker in _spreadConfig.NukeMarkers)
                {
                    if (item.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        _isNuked = true;
                        return;
                    }
                }

                // Store relative path from release root for subdir support
                var relativePath = item.FullName;
                if (relativePath.StartsWith(basePath))
                    relativePath = relativePath[basePath.Length..].TrimStart('/');
                else
                    relativePath = item.Name;

                files.Add(new SpreadFileInfo
                {
                    Name = relativePath, // e.g. "CD1/track01.mp3" or "file.rar"
                    FullPath = item.FullName,
                    Size = item.Size
                });
            }
        }
    }

    private void ProcessFiles(string serverId, List<SpreadFileInfo> files)
    {
        // Called inside _ownershipLock
        var serverConfig = _serverConfigs[serverId];
        var siteRules = serverConfig.SpreadSite.Skiplist;
        var globalRules = _spreadConfig.GlobalSkiplist;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.Name);
            var action = _skiplist.Evaluate(fileName, false, true,
                serverId, Section, siteRules, globalRules);
            if (action == SkiplistAction.Deny) continue;

            if (!_fileOwnership.TryGetValue(file.Name, out var owners))
            {
                owners = new HashSet<string>();
                _fileOwnership[file.Name] = owners;
            }
            if (owners.Add(serverId))
            {
                _serverFileCount.TryGetValue(serverId, out var cnt);
                _serverFileCount[serverId] = cnt + 1;
            }

            if (!_fileInfos.ContainsKey(file.Name))
            {
                _fileInfos[file.Name] = file;
                if (action is SkiplistAction.Unique or SkiplistAction.Similar)
                    _fileActions[file.Name] = action;
            }

            if (fileName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && _expectedFileCount == 0)
                _pendingSfv = (serverId, file.FullPath);
        }

        // Snapshot counts under ownership lock, then update progress outside it
        var owned = _serverFileCount.GetValueOrDefault(serverId);
        var total = _expectedFileCount > 0 ? _expectedFileCount : _fileInfos.Count;

        lock (_progressLock)
        {
            if (_siteProgress.TryGetValue(serverId, out var progress))
            {
                progress.FilesOwned = owned;
                progress.FilesTotal = total;
                progress.IsSource = owned > 0;
                progress.IsComplete = owned >= total && total > 0;
            }
        }

        ProgressChanged?.Invoke(this);
    }

    private async Task ParseSfvForCount(string serverId, string sfvPath)
    {
        try
        {
            if (!_pools.TryGetValue(serverId, out var pool)) return;
            await using var conn = await pool.Borrow(_cts.Token);

            byte[] data;
            if (pool.UseCpsv)
                data = await CpsvDataHelper.DownloadFile(conn.Client, sfvPath, pool.ControlHost, _cts.Token);
            else
            {
                using var ms = new MemoryStream();
                await conn.Client.DownloadStream(ms, sfvPath, token: _cts.Token);
                data = ms.ToArray();
            }

            var content = System.Text.Encoding.UTF8.GetString(data);
            var lineCount = 0;
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r').TrimStart();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith(';'))
                    lineCount++;
            }

            lock (_ownershipLock)
            {
                _expectedFileCount = lineCount + 1; // +1 for the SFV itself
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to parse SFV from {Path}", sfvPath);
        }
    }

    private (SpreadFileInfo file, string srcId, string dstId)? FindBestTransfer(
        Dictionary<string, string> sitePaths, SpreadScorer scorer)
    {
        var elapsed = DateTime.UtcNow - StartedAt;

        // Pre-compute speed map OUTSIDE lock
        var maxSpeed = 1.0;
        foreach (var src in _pools.Keys)
        {
            foreach (var dst in _pools.Keys)
            {
                if (src == dst) continue;
                var speed = _speedTracker.GetAverageSpeed(src, dst);
                if (speed > maxSpeed) maxSpeed = speed;
            }
        }

        // Snapshot progress and failure data OUTSIDE ownership lock to avoid lock inversion
        Dictionary<string, int> activeTransferSnapshot;
        lock (_progressLock)
        {
            activeTransferSnapshot = _siteProgress.ToDictionary(kv => kv.Key, kv => kv.Value.ActiveTransfers);
        }

        Dictionary<(string, string, string), int> failureSnapshot;
        lock (_failureLock)
        {
            failureSnapshot = new(_failureCounts);
        }

        SpreadFileInfo? bestFile = null;
        string? bestSrc = null, bestDst = null;
        int bestScore = -1;
        var candidateCount = 0;
        var skippedDownloadOnly = 0;
        var skippedAffil = 0;
        var skippedSlots = 0;
        var skippedFailures = 0;
        var skippedOwned = 0;

        lock (_ownershipLock)
        {
            if (_fileInfos.Count == 0)
            {
                Log.Debug("FindBestTransfer: _fileInfos is empty — scan found no files");
                return null;
            }

            var maxFileSize = 1L;
            foreach (var fi in _fileInfos.Values)
                if (fi.Size > maxFileSize) maxFileSize = fi.Size;

            // SFV-first enforcement: find destinations that still need their SFV
            // glftpd requires the SFV before any rar/data files for zipscript tracking
            var sfvFile = _fileInfos.Keys.FirstOrDefault(f =>
                f.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));
            HashSet<string>? destsNeedingSfv = null;
            if (sfvFile != null && _fileOwnership.TryGetValue(sfvFile, out var sfvOwners))
            {
                destsNeedingSfv = new(StringComparer.Ordinal);
                foreach (var (dstId, _) in sitePaths)
                {
                    if (!sfvOwners.Contains(dstId))
                        destsNeedingSfv.Add(dstId);
                }
            }

            // Pre-build per-dest extension/basename sets for Unique/Similar checks
            Dictionary<string, HashSet<string>>? destExtensions = null;
            Dictionary<string, HashSet<string>>? destBaseNames = null;
            if (_fileActions.Count > 0)
            {
                destExtensions = new(StringComparer.Ordinal);
                destBaseNames = new(StringComparer.Ordinal);
                foreach (var (fn, owners2) in _fileOwnership)
                {
                    foreach (var sid in owners2)
                    {
                        if (!destExtensions.TryGetValue(sid, out var exts))
                        {
                            exts = new(StringComparer.OrdinalIgnoreCase);
                            destExtensions[sid] = exts;
                        }
                        exts.Add(Path.GetExtension(fn));

                        if (!destBaseNames.TryGetValue(sid, out var bases))
                        {
                            bases = new(StringComparer.OrdinalIgnoreCase);
                            destBaseNames[sid] = bases;
                        }
                        bases.Add(Path.GetFileNameWithoutExtension(fn));
                    }
                }
            }

            foreach (var (fileName, owners) in _fileOwnership)
            {
                if (!_fileInfos.TryGetValue(fileName, out var fileInfo)) continue;

                foreach (var srcId in owners)
                {
                    foreach (var (dstId, _) in sitePaths)
                    {
                        if (srcId == dstId) continue;
                        if (owners.Contains(dstId)) { skippedOwned++; continue; }
                        if (_inFlightFiles.Contains((fileName, dstId))) { skippedOwned++; continue; }

                        // Chain mode: if a route is active with in-flight transfers, only
                        // consider that route. Prevents multi-site connection exhaustion.
                        if (_activeRoute is { } route &&
                            (srcId != route.srcId || dstId != route.dstId))
                            continue;

                        // SFV-first: block non-SFV files until SFV is delivered to this dest
                        if (destsNeedingSfv != null && destsNeedingSfv.Contains(dstId) &&
                            !fileName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) &&
                            !fileName.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var dstConfig = _serverConfigs[dstId];
                        if (dstConfig.SpreadSite.DownloadOnly) { skippedDownloadOnly++; continue; }
                        if (_affilCache.GetValueOrDefault(dstId)) { skippedAffil++; continue; }

                        // Check slots from snapshot (no nested lock)
                        var dstActive = activeTransferSnapshot.GetValueOrDefault(dstId);
                        var srcActive = activeTransferSnapshot.GetValueOrDefault(srcId);
                        if (dstActive >= dstConfig.SpreadSite.MaxUploadSlots ||
                            srcActive >= _serverConfigs[srcId].SpreadSite.MaxDownloadSlots)
                        { skippedSlots++; continue; }

                        // Check Unique/Similar skiplist actions (O(1) via pre-built sets)
                        if (_fileActions.TryGetValue(fileName, out var skipAction))
                        {
                            if (skipAction == SkiplistAction.Unique)
                            {
                                var ext = Path.GetExtension(fileName);
                                if (destExtensions!.TryGetValue(dstId, out var exts) && exts.Contains(ext))
                                    continue;
                            }
                            else if (skipAction == SkiplistAction.Similar)
                            {
                                var baseName = Path.GetFileNameWithoutExtension(fileName);
                                if (destBaseNames!.TryGetValue(dstId, out var bases) && bases.Contains(baseName))
                                    continue;
                            }
                        }

                        // Check failure backoff from snapshot (no nested lock)
                        if (failureSnapshot.TryGetValue((fileName, srcId, dstId), out var fails) && fails >= 5)
                        { skippedFailures++; continue; }

                        candidateCount++;

                        var ownedPercent = _pools.Count > 0
                            ? owners.Count / (double)_pools.Count
                            : 0;

                        var score = scorer.Score(fileInfo, srcId, dstId,
                            dstConfig.SpreadSite.Priority, ownedPercent,
                            maxFileSize, maxSpeed, elapsed, Mode);

                        // Track best inline — no list allocation
                        if (score > bestScore || (score == bestScore && Random.Shared.Next(2) == 0))
                        {
                            bestScore = score;
                            bestFile = fileInfo;
                            bestSrc = srcId;
                            bestDst = dstId;
                        }
                    }
                }
            }
        }

        if (bestFile == null || bestSrc == null || bestDst == null)
        {
            if (_fileInfos.Count > 0 && candidateCount == 0)
            {
                Log.Warning("FindBestTransfer: {Files} files, 0 candidates. Skipped: owned={Owned} downloadOnly={DL} affil={Affil} slots={Slots} failures={Fail}",
                    _fileInfos.Count, skippedOwned, skippedDownloadOnly, skippedAffil, skippedSlots, skippedFailures);
                if (skippedSlots > 0 && skippedOwned == 0)
                    Log.Warning("FindBestTransfer slot details: {SlotInfo}",
                        string.Join(", ", activeTransferSnapshot.Select(kv =>
                        {
                            var cfg = _serverConfigs.GetValueOrDefault(kv.Key);
                            return $"{cfg?.Name ?? kv.Key}: active={kv.Value} up={cfg?.SpreadSite.MaxUploadSlots} down={cfg?.SpreadSite.MaxDownloadSlots}";
                        })));
            }
            return null;
        }
        return (bestFile, bestSrc, bestDst);
    }

    private async Task ExecuteTransfer(SpreadFileInfo file, string srcId, string dstId,
        string dstBasePath, CancellationToken ct)
    {
        var srcPool = _pools[srcId];
        var dstPool = _pools[dstId];
        var mode = FxpModeDetector.Detect(srcPool, dstPool);

        // Slots already claimed by TryClaimSlots

        PooledConnection? srcConn = null;
        PooledConnection? dstConn = null;

        try
        {
            // Borrow with timeout — if pool is exhausted (all connections poisoned,
            // server refusing new ones due to ghost connections), Borrow blocks forever
            // on ReadAsync. Without a timeout, ActiveTransfers never decrements and
            // all slots appear permanently exhausted.
            using var borrowTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            borrowTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var srcTask = srcPool.Borrow(borrowTimeout.Token);
            var dstTask = dstPool.Borrow(borrowTimeout.Token);
            srcConn = await srcTask;
            dstConn = await dstTask;

            var dstPath = dstBasePath.TrimEnd('/') + "/" + file.Name;
            var srcPath = file.FullPath;

            var transfer = new FxpTransfer();
            // Defer directory creation until just before STOR — prevents empty dirs
            // when PASV/PORT negotiation or connection setup fails
            var dstClient = dstConn.Client;
            var fileName = file.Name;
            transfer.BeforeStore = async storeCt =>
            {
                await EnsureDirectoryExists(dstClient, dstBasePath, fileName, storeCt);
                lock (_ownershipLock) _dirsCreated.Add(dstId);
            };
            var startTime = DateTime.UtcNow;
            var transferKey = $"{file.Name}|{srcId}->{dstId}";

            var info = new ActiveTransferInfo
            {
                FileName = file.Name,
                FileSize = file.Size,
                SourceName = _serverConfigs[srcId].Name,
                DestName = _serverConfigs[dstId].Name
            };

            lock (_progressLock) _activeTransfers[transferKey] = info;

            long lastReportedBytes = 0;
            transfer.BytesTransferred += totalBytes =>
            {
                var delta = totalBytes - lastReportedBytes;
                lastReportedBytes = totalBytes;
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

                lock (_progressLock)
                {
                    _siteProgress[dstId].BytesTransferred += delta;
                    if (elapsed > 0)
                        _siteProgress[dstId].SpeedBps = totalBytes / elapsed;
                    info.BytesTransferred = totalBytes;
                    info.SpeedBps = elapsed > 0 ? totalBytes / elapsed : 0;
                }
                // Fire event outside lock
                ProgressChanged?.Invoke(this);
            };

            // TYPE I is sent inside FxpTransfer — don't send it here too
            // (double TYPE I causes response queue desync on BNC servers)

            var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
                _spreadConfig.TransferTimeoutSeconds, ct);

            lock (_progressLock) _activeTransfers.Remove(transferKey);

            if (ok)
            {
                var duration = DateTime.UtcNow - startTime;
                _speedTracker.RecordTransfer(srcId, dstId, file.Size, duration);

                lock (_ownershipLock)
                {
                    if (_fileOwnership.TryGetValue(file.Name, out var owners) && owners.Add(dstId))
                    {
                        _serverFileCount.TryGetValue(dstId, out var cnt);
                        _serverFileCount[dstId] = cnt + 1;
                    }
                }

                lock (_ownershipLock) _serversWithSuccessfulTransfer.Add(dstId);
                _forceScan = true; // Rescan — new files likely appeared on source from other racers
                Log.Information("FXP complete: {File} ({Src} -> {Dst})", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            }
            else
            {
                // Poison connections after failed transfer — FluentFTP.GnuTLS has a bug
                // where GnuTlsRecordSend error codes (-10 etc.) corrupt the session state
                // internally. The managed ArgumentOutOfRangeException is caught, but the
                // native GnuTLS session is left in an invalid state. If these connections
                // are returned to the pool, the next borrower will crash in Read() with an
                // unrecoverable native exception that kills the process.
                if (srcConn != null) srcConn.Poisoned = true;
                if (dstConn != null) dstConn.Poisoned = true;

                lock (_failureLock)
                {
                    var failKey = (file.Name, srcId, dstId);
                    _failureCounts.TryGetValue(failKey, out var count);
                    _failureCounts[failKey] = count + 1;
                }
                Log.Warning("FXP failed: {File} ({Src} -> {Dst}): {Error}", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name, transfer.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Job cancelled — don't log as error
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;
        }
        catch (OperationCanceledException)
        {
            // Borrow timeout — pool exhausted, likely ghost connections on server
            Log.Warning("FXP borrow timeout: {File} ({Src} -> {Dst}) — pool exhausted, " +
                "server may have ghost connections (try !username login to kill them)",
                file.Name, _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;

            lock (_failureLock)
            {
                var failKey = (file.Name, srcId, dstId);
                _failureCounts.TryGetValue(failKey, out var count);
                _failureCounts[failKey] = count + 1;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FXP transfer error: {File} ({Src} -> {Dst})", file.Name, srcId, dstId);

            // Mark connections as poisoned so pool discards them instead of reusing
            // (GnuTLS stream may be corrupt after failed/cancelled transfer)
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;
        }
        finally
        {
            if (srcConn != null) await srcConn.DisposeAsync();
            if (dstConn != null) await dstConn.DisposeAsync();

            lock (_ownershipLock)
            {
                _inFlightFiles.Remove((file.Name, dstId));

                // Chain mode: clear route when last in-flight transfer on it completes
                if (_activeRoute is { } ar && ar.dstId == dstId &&
                    !_inFlightFiles.Any(f => f.dstId == dstId))
                {
                    _activeRoute = null;
                    _forceScan = true; // Rescan for next hop
                }
            }
            lock (_progressLock)
            {
                _siteProgress[srcId].ActiveTransfers--;
                _siteProgress[dstId].ActiveTransfers--;
            }

            ProgressChanged?.Invoke(this);
        }
    }

    private bool IsJobComplete()
    {
        lock (_ownershipLock)
        {
            if (_fileInfos.Count == 0) return false;
            if (_expectedFileCount > 0 && _fileInfos.Count < _expectedFileCount) return false;

            foreach (var (serverId, _) in _siteProgress)
            {
                if (_serverConfigs[serverId].SpreadSite.DownloadOnly) continue;
                var owned = _serverFileCount.GetValueOrDefault(serverId);
                if (owned < _fileInfos.Count) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Try MKD first, then SITE MKD as fallback (glftpd often blocks MKD in section dirs
    /// but allows SITE MKD). Returns true if dir was created or already exists.
    /// </summary>
    private static async Task<bool> TryMakeDir(FluentFTP.AsyncFtpClient client, string path, CancellationToken ct)
    {
        var sanitized = Ftp.CpsvDataHelper.SanitizeFtpPath(path);

        // First check if directory already exists via CWD
        var cwdReply = await client.Execute($"CWD {sanitized}", ct);
        if (cwdReply.Success)
            return true;

        var reply = await client.Execute($"MKD {sanitized}", ct);
        if (reply.Success || reply.Message.Contains("exists", StringComparison.OrdinalIgnoreCase))
            return true;

        // Fallback: SITE MKD (glftpd allows this when MKD is blocked)
        reply = await client.Execute($"SITE MKD {sanitized}", ct);
        if (reply.Success || reply.Message.Contains("exists", StringComparison.OrdinalIgnoreCase)
                          || reply.Message.Contains("created", StringComparison.OrdinalIgnoreCase))
            return true;

        // Fallback: CWD to parent + relative MKD (some glftpd configs only allow relative paths)
        var trimmed = sanitized.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var parent = trimmed[..lastSlash];
            var dirName = trimmed[(lastSlash + 1)..];
            var cwdParent = await client.Execute($"CWD {parent}", ct);
            if (cwdParent.Success)
            {
                reply = await client.Execute($"MKD {dirName}", ct);
                if (reply.Success || reply.Message.Contains("exists", StringComparison.OrdinalIgnoreCase))
                    return true;

                reply = await client.Execute($"SITE MKD {dirName}", ct);
                if (reply.Success || reply.Message.Contains("exists", StringComparison.OrdinalIgnoreCase)
                                  || reply.Message.Contains("created", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        Log.Warning("MKD failed for {Path}: all methods tried (MKD, SITE MKD, relative MKD). Last reply: {Code} {Msg}",
            path, reply.Code, reply.Message);
        return false;
    }

    private static async Task EnsureDirectoryExists(FluentFTP.AsyncFtpClient client,
        string basePath, string relativePath, CancellationToken ct)
    {
        if (!await TryMakeDir(client, basePath, ct))
        {
            // Try creating parent directory first (section dir might not exist yet)
            var parentPath = basePath.TrimEnd('/');
            var lastSlash = parentPath.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var parent = parentPath[..lastSlash];
                await TryMakeDir(client, parent, ct);
                // Retry the base path after parent created
                if (!await TryMakeDir(client, basePath, ct))
                    throw new IOException($"MKD failed for {basePath} (tried MKD + SITE MKD)");
            }
            else
            {
                throw new IOException($"MKD failed for {basePath} (tried MKD + SITE MKD)");
            }
        }

        // If file is in a subdirectory (e.g. "CD1/file.rar"), create nested dirs
        var dirPart = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dirPart)) return;

        var parts = dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = basePath.TrimEnd('/');
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!await TryMakeDir(client, current, ct))
                throw new IOException($"MKD failed for {current} (tried MKD + SITE MKD)");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        if (State == SpreadJobState.Running)
            State = SpreadJobState.Stopped;
    }

    private void SetFailed(string message)
    {
        State = SpreadJobState.Failed;
        Error?.Invoke(this, message);
        Log.Warning("Spread job failed: {Release} — {Error}", ReleaseName, message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class ActiveTransferInfo
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string SourceName { get; set; } = "";
    public string DestName { get; set; } = "";
    public long BytesTransferred { get; set; }
    public double SpeedBps { get; set; }
    public double ProgressPercent => FileSize > 0 ? BytesTransferred * 100.0 / FileSize : 0;
}

/// <summary>Case-insensitive comparer for (fileName, dstId) tuples.</summary>
internal sealed class FileDstTupleComparer : IEqualityComparer<(string fileName, string dstId)>
{
    public bool Equals((string fileName, string dstId) x, (string fileName, string dstId) y) =>
        string.Equals(x.fileName, y.fileName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.dstId, y.dstId, StringComparison.Ordinal);

    public int GetHashCode((string fileName, string dstId) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.fileName),
            StringComparer.Ordinal.GetHashCode(obj.dstId));
}

/// <summary>Case-insensitive comparer for (file, src, dst) tuples.</summary>
internal sealed class FileRouteTupleComparer : IEqualityComparer<(string file, string src, string dst)>
{
    public bool Equals((string file, string src, string dst) x, (string file, string src, string dst) y) =>
        string.Equals(x.file, y.file, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.src, y.src, StringComparison.Ordinal) &&
        string.Equals(x.dst, y.dst, StringComparison.Ordinal);

    public int GetHashCode((string file, string src, string dst) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.file),
            StringComparer.Ordinal.GetHashCode(obj.src),
            StringComparer.Ordinal.GetHashCode(obj.dst));
}
