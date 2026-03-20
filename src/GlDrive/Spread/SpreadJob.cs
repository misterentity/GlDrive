using System.IO;
using System.Text.RegularExpressions;
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
    private readonly Lock _lock = new();

    // File tracking: fileName -> set of serverIds that have it
    private readonly Dictionary<string, HashSet<string>> _fileOwnership = new();
    private readonly Dictionary<string, SpreadFileInfo> _fileInfos = new();
    private readonly Dictionary<string, SiteProgress> _siteProgress = new();
    private int _expectedFileCount;

    // Transfer failure tracking: "file|src|dst" -> failure count
    private readonly Dictionary<string, int> _failureCounts = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string ReleaseName { get; }
    public string Section { get; }
    public SpreadMode Mode { get; }
    public SpreadJobState State { get; private set; } = SpreadJobState.Running;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, SiteProgress> Sites => _siteProgress;

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
        }
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;

        try
        {
            // Resolve section paths
            var sitePaths = new Dictionary<string, string>();
            foreach (var (serverId, config) in _serverConfigs)
            {
                if (config.SpreadSite.Sections.TryGetValue(Section, out var sectionPath))
                {
                    var releasePath = sectionPath.TrimEnd('/') + "/" + ReleaseName;
                    sitePaths[serverId] = releasePath;
                }
            }

            if (sitePaths.Count < 2)
            {
                SetFailed($"Need at least 2 servers with section '{Section}' configured");
                return;
            }

            // Hard timeout
            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hardTimeout.CancelAfter(TimeSpan.FromSeconds(_spreadConfig.HardTimeoutSeconds));
            var token = hardTimeout.Token;

            var lastActivity = DateTime.UtcNow;
            var scorer = new SpreadScorer(_speedTracker);

            // Main spread loop
            while (!token.IsCancellationRequested && State == SpreadJobState.Running)
            {
                // 1. Scan all sites for files
                await ScanSites(sitePaths, token);

                // 2. Find transfers to execute
                var transfer = FindBestTransfer(sitePaths, scorer);

                if (transfer == null)
                {
                    // Check completion
                    if (IsJobComplete())
                    {
                        State = SpreadJobState.Completed;
                        Completed?.Invoke(this);
                        return;
                    }

                    // No viable transfers — check for stall
                    var activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                    if (activeCount == 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                    {
                        SetFailed("No activity for 60 seconds, no viable transfers");
                        return;
                    }

                    await Task.Delay(2000, token);
                    continue;
                }

                lastActivity = DateTime.UtcNow;

                // 3. Execute the transfer
                var (file, srcId, dstId) = transfer.Value;
                _ = ExecuteTransfer(file, srcId, dstId, sitePaths[dstId], token);

                // Brief pause to let scoring settle
                await Task.Delay(100, token);
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

    private async Task ScanSites(Dictionary<string, string> sitePaths, CancellationToken ct)
    {
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

                ProcessListing(serverId, items, path);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spread scan failed for {Server} at {Path}", serverId, path);
            }
        });

        await Task.WhenAll(tasks);
    }

    private void ProcessListing(string serverId, FtpListItem[] items, string basePath)
    {
        var serverConfig = _serverConfigs[serverId];
        var siteRules = serverConfig.SpreadSite.Skiplist;
        var globalRules = _spreadConfig.GlobalSkiplist;

        lock (_lock)
        {
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

                // Parse SFV to get expected file count
                if (item.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && _expectedFileCount == 0)
                    _ = ParseSfvForCount(serverId, item.FullName);
            }

            // Update site progress
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
            var lines = content.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith(';'))
                .ToList();

            lock (_lock)
            {
                // SFV has "filename checksum" lines — count = expected file count + 1 (the SFV itself)
                _expectedFileCount = lines.Count + 1; // +1 for the SFV file
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
        var candidates = new List<(SpreadFileInfo file, string srcId, string dstId, int score)>();

        lock (_lock)
        {
            var maxFileSize = _fileInfos.Values.Max(f => f.Size);
            var allSpeeds = _pools.Keys
                .SelectMany(src => _pools.Keys.Where(dst => dst != src)
                    .Select(dst => _speedTracker.GetAverageSpeed(src, dst)))
                .Where(s => s > 0);
            var maxSpeed = allSpeeds.Any() ? allSpeeds.Max() : 1.0;

            foreach (var (fileName, owners) in _fileOwnership)
            {
                if (!_fileInfos.TryGetValue(fileName, out var fileInfo)) continue;

                foreach (var srcId in owners)
                {
                    foreach (var (dstId, _) in sitePaths)
                    {
                        if (srcId == dstId) continue;
                        if (owners.Contains(dstId)) continue; // dest already has it

                        var dstConfig = _serverConfigs[dstId];
                        if (dstConfig.SpreadSite.DownloadOnly) continue;

                        // Check slot limits
                        var dstProgress = _siteProgress[dstId];
                        if (dstProgress.ActiveTransfers >= dstConfig.SpreadSite.MaxUploadSlots) continue;

                        var srcProgress = _siteProgress[srcId];
                        if (srcProgress.ActiveTransfers >= _serverConfigs[srcId].SpreadSite.MaxDownloadSlots) continue;

                        // Check failure backoff
                        var failKey = $"{fileName}|{srcId}|{dstId}";
                        if (_failureCounts.TryGetValue(failKey, out var fails) && fails >= 2)
                        {
                            var delayMs = fails >= 3 ? 10000 : 3000;
                            // Skip if we'd be retrying too soon (simplified check)
                            if (fails >= 5) continue;
                        }

                        var ownedPercent = _pools.Count > 0
                            ? owners.Count / (double)_pools.Count
                            : 0;

                        var score = scorer.Score(fileInfo, srcId, dstId,
                            dstConfig.SpreadSite.Priority, ownedPercent,
                            maxFileSize, maxSpeed, elapsed, Mode);

                        candidates.Add((fileInfo, srcId, dstId, score));
                    }
                }
            }
        }

        if (candidates.Count == 0) return null;

        // Sort by score descending, random tiebreaker
        var rng = Random.Shared;
        candidates.Sort((a, b) =>
        {
            var cmp = b.score.CompareTo(a.score);
            return cmp != 0 ? cmp : rng.Next(-1, 2);
        });

        var best = candidates[0];
        return (best.file, best.srcId, best.dstId);
    }

    private async Task ExecuteTransfer(SpreadFileInfo file, string srcId, string dstId,
        string dstBasePath, CancellationToken ct)
    {
        var srcPool = _pools[srcId];
        var dstPool = _pools[dstId];
        var mode = FxpModeDetector.Detect(srcPool, dstPool);

        lock (_lock)
        {
            _siteProgress[srcId].ActiveTransfers++;
            _siteProgress[dstId].ActiveTransfers++;
        }

        PooledConnection? srcConn = null;
        PooledConnection? dstConn = null;

        try
        {
            srcConn = await srcPool.Borrow(ct);
            dstConn = await dstPool.Borrow(ct);

            // Ensure dest directory exists
            var dstDirReply = await dstConn.Client.Execute($"MKD {dstBasePath}", ct);
            // Ignore error — directory may already exist

            var dstPath = dstBasePath.TrimEnd('/') + "/" + file.Name;
            var srcPath = file.FullPath;

            var transfer = new FxpTransfer();
            var startTime = DateTime.UtcNow;

            transfer.BytesTransferred += bytes =>
            {
                lock (_lock)
                {
                    _siteProgress[dstId].BytesTransferred += bytes;
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed > 0)
                        _siteProgress[dstId].SpeedBps = bytes / elapsed;
                }
                ProgressChanged?.Invoke(this);
            };

            var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
                _spreadConfig.TransferTimeoutSeconds, ct);

            if (ok)
            {
                var duration = DateTime.UtcNow - startTime;
                _speedTracker.RecordTransfer(srcId, dstId, file.Size, duration);

                lock (_lock)
                {
                    if (_fileOwnership.TryGetValue(file.Name, out var owners))
                        owners.Add(dstId);
                }

                Log.Information("FXP complete: {File} ({Src} -> {Dst})", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            }
            else
            {
                var failKey = $"{file.Name}|{srcId}|{dstId}";
                lock (_lock)
                {
                    _failureCounts.TryGetValue(failKey, out var count);
                    _failureCounts[failKey] = count + 1;
                }
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

            lock (_lock)
            {
                _siteProgress[srcId].ActiveTransfers--;
                _siteProgress[dstId].ActiveTransfers--;
            }

            ProgressChanged?.Invoke(this);
        }
    }

    private bool IsJobComplete()
    {
        lock (_lock)
        {
            if (_fileInfos.Count == 0) return false;
            if (_expectedFileCount > 0 && _fileInfos.Count < _expectedFileCount) return false;

            foreach (var (serverId, progress) in _siteProgress)
            {
                if (_serverConfigs[serverId].SpreadSite.DownloadOnly) continue;
                if (progress.FilesOwned < _fileInfos.Count) return false;
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
