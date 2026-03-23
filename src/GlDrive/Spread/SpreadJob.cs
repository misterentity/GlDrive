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
    private readonly Dictionary<string, SiteProgress> _siteProgress = new();
    private int _expectedFileCount;
    private (string serverId, string path)? _pendingSfv;

    // Transfer tracking
    private readonly Dictionary<string, int> _failureCounts = new();
    private readonly Dictionary<string, ActiveTransferInfo> _activeTransfers = new();

    // Scan debouncing
    private DateTime _lastScanTime = DateTime.MinValue;
    private Task? _backgroundScan;
    private const double ScanIntervalSeconds = 5.0;

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

    public SpreadJob(string section, string releaseName, SpreadMode mode,
        SpreadConfig spreadConfig,
        Dictionary<string, FtpConnectionPool> pools,
        Dictionary<string, ServerConfig> serverConfigs,
        SpeedTracker speedTracker, SkiplistEvaluator skiplist)
    {
        Section = section;
        ReleaseName = releaseName;
        Mode = mode;
        _spreadConfig = spreadConfig;
        _pools = pools;
        _serverConfigs = serverConfigs;
        _speedTracker = speedTracker;
        _skiplist = skiplist;

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
            var sitePaths = new Dictionary<string, string>();
            foreach (var (serverId, config) in _serverConfigs)
            {
                if (config.SpreadSite.Sections.TryGetValue(Section, out var sectionPath))
                    sitePaths[serverId] = sectionPath.TrimEnd('/') + "/" + ReleaseName;
            }

            if (sitePaths.Count < 2)
            {
                SetFailed($"Need at least 2 servers with section '{Section}' configured");
                return;
            }

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
        // Scan all sites in parallel, collect results
        var results = new List<(string serverId, FtpListItem[] items)>();
        var scanLock = new Lock();

        var tasks = sitePaths.Select(async kvp =>
        {
            var (serverId, path) = kvp;
            try
            {
                if (!_pools.TryGetValue(serverId, out var pool)) return;
                await using var conn = await pool.Borrow(ct);

                FtpListItem[] items;
                if (pool.UseCpsv)
                    items = await CpsvDataHelper.ListDirectory(conn.Client, path, pool.ControlHost, ct);
                else
                    items = await conn.Client.GetListing(path, FtpListOption.AllFiles, ct);

                lock (scanLock) results.Add((serverId, items));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spread scan failed for {Server} at {Path}", serverId, path);
            }
        });

        await Task.WhenAll(tasks);

        // Process all results under lock once
        lock (_ownershipLock)
        {
            foreach (var (serverId, items) in results)
                ProcessListing(serverId, items);
        }
    }

    private void ProcessListing(string serverId, FtpListItem[] items)
    {
        // Called inside _ownershipLock
        var serverConfig = _serverConfigs[serverId];
        var siteRules = serverConfig.SpreadSite.Skiplist;
        var globalRules = _spreadConfig.GlobalSkiplist;

        foreach (var item in items)
        {
            if (item.Type != FtpObjectType.File) continue;

            var action = _skiplist.Evaluate(item.Name, false, true,
                serverId, Section, siteRules, globalRules);
            if (action == SkiplistAction.Deny) continue;

            if (!_fileOwnership.TryGetValue(item.Name, out var owners))
            {
                owners = new HashSet<string>();
                _fileOwnership[item.Name] = owners;
            }
            owners.Add(serverId);

            if (!_fileInfos.ContainsKey(item.Name))
            {
                _fileInfos[item.Name] = new SpreadFileInfo
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    Size = item.Size
                };
            }

            if (item.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && _expectedFileCount == 0)
                _pendingSfv = (serverId, item.FullName);
        }

        // Update site progress
        lock (_progressLock)
        {
            if (_siteProgress.TryGetValue(serverId, out var progress))
            {
                progress.FilesOwned = _fileOwnership.Count(kv => kv.Value.Contains(serverId));
                progress.FilesTotal = _expectedFileCount > 0 ? _expectedFileCount : _fileInfos.Count;
                progress.IsSource = progress.FilesOwned > 0;
                progress.IsComplete = progress.FilesOwned >= progress.FilesTotal && progress.FilesTotal > 0;
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

        SpreadFileInfo? bestFile = null;
        string? bestSrc = null, bestDst = null;
        int bestScore = -1;

        lock (_ownershipLock)
        {
            if (_fileInfos.Count == 0) return null;

            var maxFileSize = 1L;
            foreach (var fi in _fileInfos.Values)
                if (fi.Size > maxFileSize) maxFileSize = fi.Size;

            foreach (var (fileName, owners) in _fileOwnership)
            {
                if (!_fileInfos.TryGetValue(fileName, out var fileInfo)) continue;

                foreach (var srcId in owners)
                {
                    foreach (var (dstId, _) in sitePaths)
                    {
                        if (srcId == dstId) continue;
                        if (owners.Contains(dstId)) continue;

                        var dstConfig = _serverConfigs[dstId];
                        if (dstConfig.SpreadSite.DownloadOnly) continue;
                        if (_affilCache.GetValueOrDefault(dstId)) continue;

                        // Check slots under progress lock
                        bool slotsAvailable;
                        lock (_progressLock)
                        {
                            var dstProgress = _siteProgress[dstId];
                            var srcProgress = _siteProgress[srcId];
                            slotsAvailable =
                                dstProgress.ActiveTransfers < dstConfig.SpreadSite.MaxUploadSlots &&
                                srcProgress.ActiveTransfers < _serverConfigs[srcId].SpreadSite.MaxDownloadSlots;
                        }
                        if (!slotsAvailable) continue;

                        // Check failure backoff
                        lock (_failureLock)
                        {
                            var failKey = $"{fileName}|{srcId}|{dstId}";
                            if (_failureCounts.TryGetValue(failKey, out var fails) && fails >= 5)
                                continue;
                        }

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

        if (bestFile == null || bestSrc == null || bestDst == null) return null;
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

            // Ensure dest directory exists
            await dstConn.Client.Execute($"MKD {dstBasePath}", ct);

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

            // Pipeline TYPE I commands in parallel
            await Task.WhenAll(
                srcConn.Client.Execute("TYPE I", ct),
                dstConn.Client.Execute("TYPE I", ct));

            var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
                _spreadConfig.TransferTimeoutSeconds, ct);

            lock (_progressLock) _activeTransfers.Remove(transferKey);

            if (ok)
            {
                var duration = DateTime.UtcNow - startTime;
                _speedTracker.RecordTransfer(srcId, dstId, file.Size, duration);

                lock (_ownershipLock)
                {
                    if (_fileOwnership.TryGetValue(file.Name, out var owners))
                        owners.Add(dstId);
                }

                Log.Information("FXP complete: {File} ({Src} -> {Dst})", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            }
            else
            {
                lock (_failureLock)
                {
                    var failKey = $"{file.Name}|{srcId}|{dstId}";
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
                var owned = _fileOwnership.Count(kv => kv.Value.Contains(serverId));
                if (owned < _fileInfos.Count) return false;
            }
            return true;
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
