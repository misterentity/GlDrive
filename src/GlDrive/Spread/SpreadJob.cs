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

    // File tracking
    private readonly Dictionary<string, HashSet<string>> _fileOwnership = new();
    private readonly Dictionary<string, SpreadFileInfo> _fileInfos = new();
    private readonly Dictionary<string, SkiplistAction> _fileActions = new(); // per-file skiplist action
    private readonly Dictionary<string, int> _serverFileCount = new(); // per-server owned file count
    private readonly Dictionary<string, SiteProgress> _siteProgress = new();
    private int _expectedFileCount;
    private (string serverId, string path)? _pendingSfv;

    // Transfer tracking
    private readonly Dictionary<(string file, string src, string dst), int> _failureCounts = new();
    private readonly Dictionary<string, ActiveTransferInfo> _activeTransfers = new();

    // Scan debouncing
    private DateTime _lastScanTime = DateTime.MinValue;
    private Task? _backgroundScan;
    private const double ScanIntervalSeconds = 5.0;
    private bool _isNuked;

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

        try
        {
            // Phase 1: Discover which servers already have the release
            var sitePaths = new Dictionary<string, string>();
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

                    // Create the directory on the destination server
                    if (_pools.TryGetValue(serverId, out var pool))
                    {
                        try
                        {
                            await using var conn = await pool.Borrow(ct);
                            await conn.Client.CreateDirectory(destPath, true, ct);
                            Log.Information("Spread: created {Path} on {Server}", destPath, config.Name);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Spread: failed to create {Path} on {Server}", destPath, config.Name);
                        }
                    }
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
                // 1. Background scan with debounce — don't block the scoring loop
                if (_backgroundScan == null || _backgroundScan.IsCompleted)
                {
                    if ((DateTime.UtcNow - _lastScanTime).TotalSeconds >= ScanIntervalSeconds)
                    {
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
                    if (IsJobComplete())
                    {
                        State = SpreadJobState.Completed;
                        Completed?.Invoke(this);
                        return;
                    }

                    int activeCount;
                    lock (_progressLock)
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);

                    if (activeCount == 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                    {
                        SetFailed("No activity for 60 seconds, no viable transfers");
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
                if (TryClaimSlots(srcId, dstId))
                {
                    _ = ExecuteTransfer(file, srcId, dstId, sitePaths[dstId], token);
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
        if (pool.UseCpsv)
            items = await CpsvDataHelper.ListDirectory(conn.Client, currentPath, pool.ControlHost, ct);
        else
            items = await conn.Client.GetListing(currentPath, FtpListOption.AllFiles, ct);

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
                Log.Warning("FindBestTransfer: {Files} files, 0 candidates. Skipped: owned={Owned} downloadOnly={DL} affil={Affil} slots={Slots} failures={Fail}",
                    _fileInfos.Count, skippedOwned, skippedDownloadOnly, skippedAffil, skippedSlots, skippedFailures);
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
            // Borrow both connections in parallel
            var srcTask = srcPool.Borrow(ct);
            var dstTask = dstPool.Borrow(ct);
            srcConn = await srcTask;
            dstConn = await dstTask;

            // Ensure dest directory tree exists (handles subdirs like CD1/, Sample/)
            await EnsureDirectoryExists(dstConn.Client, dstBasePath, file.Name, ct);

            var dstPath = dstBasePath.TrimEnd('/') + "/" + file.Name;
            var srcPath = file.FullPath;

            var transfer = new FxpTransfer();
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

                Log.Information("FXP complete: {File} ({Src} -> {Dst})", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            }
            else
            {
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
        catch (Exception ex)
        {
            Log.Warning(ex, "FXP transfer error: {File} ({Src} -> {Dst})", file.Name, srcId, dstId);

            // After a failed transfer, disconnect the connections instead of returning them
            // to the pool — their command/response state may be desynced
            try { srcConn?.Client.Disconnect(); } catch { }
            try { dstConn?.Client.Disconnect(); } catch { }
        }
        finally
        {
            if (srcConn != null) await srcConn.DisposeAsync();
            if (dstConn != null) await dstConn.DisposeAsync();

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

    private static async Task EnsureDirectoryExists(FluentFTP.AsyncFtpClient client,
        string basePath, string relativePath, CancellationToken ct)
    {
        // MKD the base release directory
        await client.Execute($"MKD {basePath}", ct);

        // If file is in a subdirectory (e.g. "CD1/file.rar"), create nested dirs
        var dirPart = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dirPart)) return;

        var parts = dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = basePath.TrimEnd('/');
        foreach (var part in parts)
        {
            current += "/" + part;
            await client.Execute($"MKD {current}", ct);
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
