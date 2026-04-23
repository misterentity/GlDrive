using System.IO;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
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
    private Dictionary<string, FtpConnectionPool> _pools;
    private readonly Dictionary<string, ServerConfig> _serverConfigs;
    private readonly SpeedTracker _speedTracker;
    private readonly SkiplistEvaluator _skiplist;
    private readonly SectionBlacklistStore? _blacklist;
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

    // Directory cleanup: track created dirs and successful transfers per destination
    private readonly HashSet<string> _dirsCreated = new(); // serverId values that got MKD
    private readonly HashSet<string> _serversWithSuccessfulTransfer = new();

    // Per-destination failure backoff. When MKD or FXP fails to a dest, the dest
    // is parked in _destRetryAt until the backoff expires; FindBestTransfer skips
    // it until then. Successful transfer to that dest clears the failure count
    // and retry window. Backoff schedule is exponential so a momentarily-broken
    // dest (lost TLS, one-off 550) heals fast, but a persistently-broken dest
    // (wrong section path, path-filter) is eventually dropped for the race.
    // Replaces the old permanent-blacklist model which chain-mode combined with
    // meant a single early MKD fail killed the dest for the whole race.
    private static readonly TimeSpan[] BackoffLadder =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
    ];
    // A retry time of DateTime.MaxValue means "dropped for this race" — exceeded
    // the ladder. Checked in FindBestTransfer + the all-dests-broken fail-fast.
    private readonly Dictionary<string, int> _destFailureCount = new();
    private readonly Dictionary<string, DateTime> _destRetryAt = new();

    // Set to true when the race terminates due to a skiplist/blacklist denial.
    // Used by EmitRaceOutcome to emit "blacklisted" instead of "aborted".
    private bool _blacklisted = false;

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
        SectionBlacklistStore? blacklist = null,
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
        _blacklist = blacklist;
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

            // Pre-compute affil check once — match group name at end of release
            // (scene releases end with -GROUPNAME). Contains() caused false positives
            // where short group names like "NOMA" matched inside "Narco.Menomanites".
            _affilCache[serverId] = config.SpreadSite.Affils.Count > 0 &&
                config.SpreadSite.Affils.Any(g =>
                    releaseName.EndsWith($"-{g}", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Swap the pool dictionary. Called by SpreadManager after ReinitDeadPools
    /// replaces an exhausted pool with a fresh one — without this the job would
    /// hold a reference to the disposed pool for its entire lifetime.
    /// Must be called before RunAsync begins using pools.
    /// </summary>
    public void UpdatePools(Dictionary<string, FtpConnectionPool> pools)
    {
        _pools = pools;
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
            var parsed = SceneNameParser.Parse(ReleaseName);
            foreach (var (serverId, config) in _serverConfigs)
            {
                var siteRules = config.SpreadSite.Skiplist;
                var globalRules = _spreadConfig.GlobalSkiplist;
                var (action, trace) = _skiplist.EvaluateWithTrace(ReleaseName, true, false,
                    Section, siteRules, globalRules, parsed);
                foreach (var t in trace)
                    t.Source = $"{config.Name}/{t.Source}";
                allTrace.AddRange(trace);
                if (action == SkiplistAction.Deny)
                {
                    SkiplistTrace = allTrace;
                    var matchedRule = trace.FirstOrDefault(t => t.IsMatch);
                    SkiplistResult = $"Denied by: {matchedRule?.Pattern} (on {config.Name})";
                    _blacklisted = true;
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

                // Skip sites that have permanently failed MKD for this section.
                // Without this check, every race re-discovers the 550 the hard
                // way (5 retries per file * N files), burning minutes per race.
                if (_blacklist != null && _blacklist.IsBlacklisted(serverId, Section))
                {
                    var entry = _blacklist.Get(serverId, Section);
                    Log.Information("Spread: {Server} blacklisted for [{Section}] — {Reason} " +
                        "(first failed {When:u}, {Count} total). Skipping. " +
                        "Delete entry from section-blacklist.json to retry.",
                        config.Name, Section, entry?.Reason ?? "unknown",
                        entry?.FirstFailedAt ?? DateTime.UtcNow, entry?.FailureCount ?? 0);
                    continue;
                }

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
                    var sectionBase = sectionMatch.Value.TrimEnd('/');

                    // Probe for glftpd dated directory (MMDD format, e.g. /mp3/0409/)
                    // If the section root has a dated subfolder for today, use it.
                    var destPath = await ProbeDatedDirectory(serverId, sectionBase, ct)
                        is { } datedBase
                            ? datedBase + "/" + ReleaseName
                            : sectionBase + "/" + ReleaseName;

                    sitePaths[serverId] = destPath;
                    Log.Information("Spread: {Server} is DESTINATION at [{Section}] {Path}",
                        config.Name, sectionMatch.Key, destPath);
                }
                else
                {
                    // Promoted from Debug to Information — the user needs to see
                    // when a server is silently being excluded from a race
                    // because it has no section mapping for the announced
                    // category. Without this, mp3 races with only one
                    // destination (and no other visible reason) look like a bug.
                    Log.Information("Spread: {Server} has no matching section for [{Section}] — " +
                        "not participating in this race (add a '{Section}' entry to its section map)",
                        config.Name, Section);
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

                // All non-source destinations dropped for this race (retryAt ==
                // MaxValue, meaning they blew the backoff ladder). Fail fast
                // instead of idling to the hard timeout.
                int viableDestCount;
                lock (_ownershipLock)
                {
                    viableDestCount = sitePaths.Keys
                        .Count(id => !sourceServers.Contains(id)
                                  && !_serverConfigs[id].SpreadSite.DownloadOnly
                                  && !IsDestDropped(id));
                }
                if (viableDestCount == 0)
                {
                    string droppedName;
                    lock (_ownershipLock)
                    {
                        droppedName = string.Join(", ", _destRetryAt
                            .Where(kv => kv.Value == DateTime.MaxValue)
                            .Select(kv => _serverConfigs.TryGetValue(kv.Key, out var c) ? c.Name : kv.Key));
                    }
                    bool hadTransfersBl;
                    lock (_ownershipLock)
                        hadTransfersBl = _serversWithSuccessfulTransfer.Count > 0;
                    if (hadTransfersBl)
                    {
                        State = SpreadJobState.Completed;
                        Completed?.Invoke(this);
                        Log.Information("Spread completed (partial): {Release} — all remaining destinations dropped after repeated failures ({Dropped})",
                            ReleaseName, droppedName);
                    }
                    else
                    {
                        SetFailed($"All destinations dropped after repeated failures — {droppedName}");
                    }
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

                    // A dest sitting inside its backoff window is pending work,
                    // not idle — the retry is scheduled. Hard-cap the total
                    // time we'll wait on backoffs (3min, above the 2min tier
                    // of the ladder) so a permanently-dropped dest can't
                    // pin the race forever, but otherwise keep lastActivity
                    // fresh. Without this, a 30s backoff fires its retry
                    // only AFTER the 15s idle timer has killed the race.
                    var idleSeconds = (DateTime.UtcNow - lastActivity).TotalSeconds;
                    var nextBackoff = NextBackoffExpiry();
                    if (nextBackoff is { } bAt && bAt > DateTime.UtcNow && idleSeconds < 180)
                    {
                        lastActivity = DateTime.UtcNow;
                        idleSeconds = 0;
                    }

                    if (activeCount == 0 && idleSeconds > 15)
                    {
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

                        if (missingFiles > 0 && _completionRetries < 1)
                        {
                            _completionRetries++;
                            lock (_failureLock) _failureCounts.Clear();
                            // Also clear per-dest backoff — the sweep's whole
                            // job is to undo transient failure state so the
                            // retry actually has a chance.
                            lock (_ownershipLock)
                            {
                                _destFailureCount.Clear();
                                _destRetryAt.Clear();
                            }
                            _forceScan = true;
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

                            Log.Information("Spread completion sweep: {Missing} files still missing, " +
                                "resetting failures and reinitializing pools — {Release}",
                                missingFiles, ReleaseName);
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
                            SetFailed("No activity for 15 seconds, no viable transfers");
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

                // Brief pause between scheduling — 50ms is enough for the
                // background transfer Task.Run to actually claim a slot and
                // register itself before the next iteration scores again.
                // Was 500ms which throttled parallel scheduling too hard:
                // with MaxUploadSlots=N, it took N*500ms = 1.5s just to fill
                // slots. Dropped to 50ms so the parallel-slot capacity
                // actually gets used on fast sites.
                await Task.Delay(50, token);
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
            // Clean up any destination directory we created where the release
            // didn't finish (either zero files or partial). SITE WIPE -r removes
            // the directory WITHOUT deducting user credits — glftpd's whole
            // point of SITE WIPE vs plain DELE/RMD. Left-behind partial
            // releases get auto-nuked by dirscript/zipscript which costs the
            // user credits and triggers ratio penalties, so we always clean up
            // our own incomplete work before leaving.
            await CleanupIncompleteDirs(sitePaths);
            EmitRaceOutcome(State == SpreadJobState.Completed ? "complete"
                          : _blacklisted                       ? "blacklisted"
                          :                                      "aborted");
        }
    }

    private void EmitRaceOutcome(string result)
    {
        try
        {
            var recorder = App.TelemetryRecorder;
            if (recorder is null) return;

            List<GlDrive.AiAgent.RaceParticipant> participants;
            lock (_progressLock)
            {
                participants = _siteProgress.Values.Select(s => new GlDrive.AiAgent.RaceParticipant(
                    ServerId: s.ServerId,
                    Role: s.IsSource ? "src" : "dst",  // NOTE: may misclassify in multi-source topologies; see _knownSourceServerId
                    Bytes: s.BytesTransferred,
                    Files: s.FilesOwned,
                    AvgKbps: s.SpeedBps / 1024.0,  // NOTE: instantaneous speed; often 0 at race end. Use transfers stream for avg.
                    AbortReason: null
                )).ToList();
            }

            int filesTotal;
            int filesExpected;
            lock (_ownershipLock)
            {
                filesTotal    = _fileInfos.Count;
                filesExpected = _expectedFileCount;
            }

            recorder.Record(GlDrive.AiAgent.TelemetryStream.Races, new GlDrive.AiAgent.RaceOutcomeEvent
            {
                RaceId = Id,
                Section = Section ?? "",
                Release = ReleaseName ?? "",
                StartedAt = StartedAt.ToString("O"),
                EndedAt = DateTime.UtcNow.ToString("O"),
                Participants = participants,
                Winner = null,
                FxpMode = Mode.ToString(),
                ScoreBreakdown = new Dictionary<string, int>(),
                Result = result,
                FilesExpected = filesExpected,
                FilesTotal = filesTotal
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "EmitRaceOutcome failed for race {RaceId}", Id);
        }
    }

    /// <summary>
    /// Remove release directories on destinations where we created them (MKD)
    /// but the release didn't fully land. Uses glftpd's SITE WIPE -r which
    /// removes a directory WITHOUT deducting user credits — that's the whole
    /// point of wipe vs delete. Any destination with owned &lt; total is
    /// considered incomplete and wiped. The source server and any destination
    /// that successfully received every file are left alone.
    /// </summary>
    private async Task CleanupIncompleteDirs(Dictionary<string, string> sitePaths)
    {
        HashSet<string> created;
        Dictionary<string, int> ownedCounts;
        int total;
        lock (_ownershipLock)
        {
            created = [.._dirsCreated];
            ownedCounts = new Dictionary<string, int>(_serverFileCount);
            total = _expectedFileCount > 0 ? _expectedFileCount : _fileInfos.Count;
        }

        foreach (var serverId in created)
        {
            var owned = ownedCounts.GetValueOrDefault(serverId);
            // If the dest has every file the release needs, it's complete.
            // Leave it alone (don't wipe a good release!).
            if (total > 0 && owned >= total) continue;

            if (!sitePaths.TryGetValue(serverId, out var path)) continue;
            if (!_pools.TryGetValue(serverId, out var pool)) continue;

            var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
            var sanitized = Ftp.CpsvDataHelper.SanitizeFtpPath(path);

            try
            {
                await using var conn = await pool.Borrow(CancellationToken.None);

                // Prefer SITE WIPE -r: removes dir + contents without credit
                // deduction. This is the critical difference — if we use DELE
                // or plain RMD, the user gets nuked for leaving incomplete
                // files behind AND pays the credit penalty for deleting their
                // own upload. SITE WIPE is glftpd's explicit "no penalty"
                // removal command.
                var wipeReply = await conn.Client.Execute($"SITE WIPE -r {sanitized}", CancellationToken.None);
                if (wipeReply.Success ||
                    (wipeReply.Message ?? "").Contains("wiped", StringComparison.OrdinalIgnoreCase) ||
                    (wipeReply.Message ?? "").Contains("removed", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Spread cleanup: SITE WIPE -r {Path} on {Server} ({Owned}/{Total} files — {Reason})",
                        path, serverName, owned, total, owned == 0 ? "empty" : "partial");
                    continue;
                }

                // Fall back to plain RMD when SITE WIPE isn't available or
                // denied. Only useful when the dir is already empty — RMD on
                // a non-empty dir will fail and glftpd will nuke us anyway,
                // but it's worth trying as a last resort.
                Log.Warning("Spread cleanup: SITE WIPE failed on {Server} ({Code} {Msg}), trying RMD",
                    serverName, wipeReply.Code, wipeReply.Message);
                var rmdReply = await conn.Client.Execute($"RMD {sanitized}", CancellationToken.None);
                if (rmdReply.Success)
                {
                    Log.Information("Spread cleanup: RMD {Path} on {Server} (fallback)", path, serverName);
                }
                else
                {
                    Log.Warning("Spread cleanup: could not remove {Path} on {Server} — site may nuke you ({Code} {Msg})",
                        path, serverName, rmdReply.Code, rmdReply.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spread cleanup: failed to wipe {Path} on {Server} — partial release left behind, site may nuke",
                    path, serverName);
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

            _mainPools.TryGetValue(serverId, out var mainPool);
            _pools.TryGetValue(serverId, out var spreadPool);

            if (mainPool == null && spreadPool == null)
            {
                Log.Warning("Spread scan: no pool for {Server}", serverName);
                return;
            }

            // Try main pool first (has keepalive + reconnect), fall back to
            // dedicated spread pool if the main pool borrow times out. Main
            // pool is shared with filesystem/search/downloads, so during a race
            // burst its 3-4 slots can saturate and the old single-pool path
            // would abandon the scan with "OperationCanceledException". That
            // leaves _fileInfos empty, FindBestTransfer returns null, and the
            // race dies at the 60s inactivity timer with "no viable transfers"
            // — even though zephyr was ready to receive.
            var files = new List<SpreadFileInfo>();
            var scanDone = false;
            Exception? lastError = null;

            if (mainPool != null)
            {
                try
                {
                    Log.Information("Spread scan: listing {Server} at {Path} (using main pool)...",
                        serverName, basePath);
                    await ScanDirectoryRecursive(mainPool, basePath, basePath, files, 0, ct);
                    scanDone = true;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // Borrow timeout on main pool — don't blow away the whole scan,
                    // try the spread pool as a fallback. The fresh buffer must be
                    // cleared because a partial recursive scan may have appended
                    // items before hitting the timeout.
                    Log.Warning("Spread scan: main pool exhausted for {Server}, falling back to spread pool",
                        serverName);
                    files.Clear();
                    lastError = ex;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    files.Clear();
                }
            }

            if (!scanDone && spreadPool != null)
            {
                try
                {
                    Log.Information("Spread scan: listing {Server} at {Path} (using spread pool fallback)...",
                        serverName, basePath);
                    await ScanDirectoryRecursive(spreadPool, basePath, basePath, files, 0, ct);
                    scanDone = true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (scanDone)
            {
                Log.Information("Spread scan: {Server} returned {Count} files", serverName, files.Count);
                lock (scanLock) results.Add((serverId, files));
            }
            else
            {
                Log.Warning(lastError, "Spread scan FAILED for {Server} at {Path} (both pools unavailable)",
                    serverName, basePath);
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

        // Reconcile FilesTotal across ALL sites after the scan cycle. ProcessFiles
        // stamps each site with `_fileInfos.Count` as it observed it — so sites
        // processed first in the loop see a smaller count than sites processed
        // later. Without this pass, the first-processed site could flip to
        // IsComplete=true just because its own tiny file set matched the
        // partial _fileInfos snapshot.
        int finalTotal;
        lock (_ownershipLock)
        {
            finalTotal = _expectedFileCount > 0 ? _expectedFileCount : _fileInfos.Count;
        }
        lock (_progressLock)
        {
            foreach (var progress in _siteProgress.Values)
            {
                progress.FilesTotal = finalTotal;
                progress.IsComplete = progress.FilesOwned >= finalTotal && finalTotal > 0;
            }
        }
        ProgressChanged?.Invoke(this);
    }

    /// <summary>
    /// Detects glftpd zipscript "-MISSING-*" placeholder files. Zipscript drops
    /// a tiny stub with this prefix when the SFV declares a file that the site
    /// does not actually hold. These are inverse signals — the site LACKS the
    /// real file — and must never be counted as owned.
    /// </summary>
    private static bool IsMissingPlaceholder(string name, long size)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Common forms: "-missing-foo.rar", "-MISSING-foo.rar". Some configs also
        // use a ".missing" suffix on the real filename.
        if (name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase)) return true;
        // Belt-and-suspenders: the stub is always tiny. Real release files are
        // always multi-KB. A 0-byte file whose name starts with "-" is almost
        // certainly a zipscript marker.
        if (size == 0 && name.StartsWith('-')) return true;
        return false;
    }

    private async Task ScanDirectoryRecursive(FtpConnectionPool pool, string basePath,
        string currentPath, List<SpreadFileInfo> files, int depth, CancellationToken ct)
    {
        if (depth > 3) return; // Max recursion depth

        // Borrow timeout — don't wait forever if pool is exhausted. 20s gives
        // the main pool enough time to free a slot under heavy race load
        // without making scan cycles feel sluggish. If the borrow still times
        // out, ScanSites will retry on the spread pool.
        using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borrowCts.CancelAfter(TimeSpan.FromSeconds(20));
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

                // Skip glftpd zipscript -MISSING placeholders. When zipscript validates
                // an SFV and finds a declared file absent, it drops a 0-byte
                // "-MISSING-<filename>" stub in its place. These are not real files —
                // counting them as "owned" makes destinations falsely show 100%
                // complete when they in fact hold no real data.
                if (IsMissingPlaceholder(item.Name, item.Size)) continue;

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
        var skippedBackoff = 0;
        Dictionary<string, DateTime> retrySnapshot;
        lock (_ownershipLock) retrySnapshot = new Dictionary<string, DateTime>(_destRetryAt);
        var now = DateTime.UtcNow;
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

                        // Per-destination backoff. The dest recently failed and
                        // is parked until retryAt; MaxValue means dropped from
                        // this race entirely (blew the backoff ladder).
                        if (retrySnapshot.TryGetValue(dstId, out var until) && now < until)
                        { skippedBackoff++; continue; }

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
                Log.Warning("FindBestTransfer: {Files} files, 0 candidates. Skipped: owned={Owned} downloadOnly={DL} affil={Affil} slots={Slots} failures={Fail} backoff={BO}",
                    _fileInfos.Count, skippedOwned, skippedDownloadOnly, skippedAffil,
                    skippedSlots, skippedFailures, skippedBackoff);
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

            // Wait for both — if either throws we still want to extract the one that
            // succeeded so the finally block can dispose it and return it to the pool.
            // Previously `srcConn = await srcTask; dstConn = await dstTask;` would
            // orphan dstConn when srcTask threw, leaking a pool slot.
            try { await Task.WhenAll(srcTask, dstTask); }
            catch { /* fall through — extract via IsCompletedSuccessfully */ }

            if (srcTask.IsCompletedSuccessfully) srcConn = srcTask.Result;
            if (dstTask.IsCompletedSuccessfully) dstConn = dstTask.Result;

            // If either side failed, rethrow its exception so the catch blocks below
            // run their failure bookkeeping. The succeeded side (if any) is now held
            // on srcConn/dstConn and will be disposed by the finally block.
            if (!srcTask.IsCompletedSuccessfully) await srcTask;
            if (!dstTask.IsCompletedSuccessfully) await dstTask;

            var dstPath = dstBasePath.TrimEnd('/') + "/" + file.Name;
            var srcPath = file.FullPath;

            var transfer = new FxpTransfer();
            // Defer directory creation until just before STOR — prevents empty dirs
            // when PASV/PORT negotiation or connection setup fails
            var dstClient = dstConn!.Client;
            var fileName = file.Name;
            transfer.BeforeStore = async storeCt =>
            {
                await EnsureDirectoryExists(dstClient, dstId, dstBasePath, fileName, storeCt);
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

            var ok = await transfer.ExecuteAsync(srcConn!, dstConn, srcPath, dstPath, mode,
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
                    _serversWithSuccessfulTransfer.Add(dstId);
                    // A single success proves the dest is working — clear any
                    // accumulated failure count + backoff window so future
                    // transient 550s don't immediately re-trigger the ladder.
                    _destFailureCount.Remove(dstId);
                    _destRetryAt.Remove(dstId);
                }
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

                // Push the dest onto the backoff ladder. Both MKD and FXP
                // failures flow through here — a destination whose data
                // channel keeps dying (GnuTLS corruption, BNC dropouts)
                // deserves the same adaptive pause as one that rejects MKD.
                // _failureCounts (per file/src/dst) is the per-pair retry
                // limit; _destFailureCount is the per-dest backoff schedule.
                RegisterDestFailure(dstId, dstBasePath, transfer.ErrorMessage,
                    IsMkdError(transfer.ErrorMessage));
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
            }
            lock (_progressLock)
            {
                _siteProgress[srcId].ActiveTransfers--;
                _siteProgress[dstId].ActiveTransfers--;
            }

            ProgressChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Heuristic: does this FXP error message look like a directory-creation
    /// failure? If so we treat it as a destination-level problem (permission,
    /// path-filter, missing section) rather than a per-file transfer glitch.
    /// </summary>
    private static bool IsMkdError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        return errorMessage.Contains("MKD failed", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("make directories", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("path-filter", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Record a failure for a destination and park it on the backoff ladder.
    /// Each failure picks the next step (3s → 10s → 30s → 2min); one past the
    /// end of the ladder marks the dest as dropped for the race (retryAt =
    /// MaxValue). Successful transfer to the dest clears the failure count so
    /// a later transient 550 doesn't jump straight to the end of the ladder.
    /// Caller should pass true for <paramref name="isMkd"/> on MKD-class
    /// failures so the log line reflects the root cause.
    /// </summary>
    private void RegisterDestFailure(string dstId, string dstBasePath, string? errorMessage, bool isMkd)
    {
        int newCount;
        DateTime retryAt;
        bool justDropped;
        bool coalesced;
        string dstName;
        lock (_ownershipLock)
        {
            // Coalesce concurrent failures: with SpreadPoolSize=3 and three
            // parallel transfers all hitting MKD in the same ~100ms window,
            // a naive bump-per-failure advances the ladder 3 steps at once
            // (3s → 10s → 30s) when really we only took ONE retry cycle.
            // If the dest is already inside an active backoff window, the
            // ladder has already been advanced for this cycle — log and
            // return without bumping again. Ladder only advances when a
            // NEW attempt (post-retryAt) fails.
            var now = DateTime.UtcNow;
            if (_destRetryAt.TryGetValue(dstId, out var currentRetryAt)
                && currentRetryAt != DateTime.MaxValue
                && now < currentRetryAt)
            {
                coalesced = true;
                newCount = _destFailureCount.GetValueOrDefault(dstId);
                retryAt = currentRetryAt;
                justDropped = false;
                dstName = _serverConfigs.TryGetValue(dstId, out var cfg0) ? cfg0.Name : dstId;
            }
            else
            {
                coalesced = false;
                _destFailureCount.TryGetValue(dstId, out var prev);
                newCount = prev + 1;
                _destFailureCount[dstId] = newCount;
                if (newCount > BackoffLadder.Length)
                {
                    retryAt = DateTime.MaxValue;
                    justDropped = !_destRetryAt.TryGetValue(dstId, out var prevAt)
                        || prevAt != DateTime.MaxValue;
                }
                else
                {
                    retryAt = now + BackoffLadder[newCount - 1];
                    justDropped = false;
                }
                _destRetryAt[dstId] = retryAt;
                dstName = _serverConfigs.TryGetValue(dstId, out var cfg) ? cfg.Name : dstId;
            }
        }

        if (coalesced)
        {
            Log.Debug("Spread: concurrent failure on {Dst} (still backing off, ladder unchanged) — {Err}",
                dstName, errorMessage ?? "(no message)");
            return;
        }

        if (justDropped)
        {
            Log.Warning("Spread: destination {Dst} dropped after {Count} failures at {Path} " +
                "— last error: {Err}. Remaining destinations continue.",
                dstName, newCount, dstBasePath, errorMessage ?? "(no message)");
            _forceScan = true;
        }
        else
        {
            var waitSeconds = (retryAt - DateTime.UtcNow).TotalSeconds;
            Log.Information("Spread: destination {Dst} backing off {Wait:F0}s (failure #{Count}, {Kind}) — {Err}",
                dstName, waitSeconds, newCount, isMkd ? "MKD" : "FXP", errorMessage ?? "(no message)");
        }
    }

    /// <summary>
    /// Return the soonest retryAt among non-dropped dests, or null if none
    /// are in backoff. Used to extend the idle timer past a pending backoff —
    /// killing a race because the 15s idle timer fires 2s before a 17s backoff
    /// expires is a waste of the whole race.
    /// </summary>
    private DateTime? NextBackoffExpiry()
    {
        lock (_ownershipLock)
        {
            DateTime? earliest = null;
            foreach (var (_, at) in _destRetryAt)
            {
                if (at == DateTime.MaxValue) continue; // dropped
                if (earliest == null || at < earliest) earliest = at;
            }
            return earliest;
        }
    }

    private bool IsDestDropped(string dstId)
    {
        lock (_ownershipLock)
        {
            return _destRetryAt.TryGetValue(dstId, out var until) && until == DateTime.MaxValue;
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
    /// Create a directory on glftpd. glftpd speaks plain RFC 959 MKD — there is
    /// no SITE MKD command (that is a ProFTPD mod_site_misc extension and will
    /// always return 500 "Command not understood" on glftpd, which just adds
    /// noise and swallows the real reply). cbftp, the reference race engine,
    /// only ever fires plain MKD against an absolute path.
    ///
    /// Semantics:
    ///   CWD &lt;path&gt;          → fast path: already exists
    ///   MKD &lt;path&gt;          → 257 = created, 2xx = OK
    ///   reply text "File exist" / "already exist" → treat as success (cbftp)
    ///   550 with "No such file" → parent missing, recurse once
    ///   anything else → fail, log actual reply code + message
    /// </summary>
    private static async Task<bool> TryMakeDir(FluentFTP.AsyncFtpClient client, string path, CancellationToken ct)
    {
        var (ok, _, _) = await TryMakeDirWithResult(client, path, ct);
        return ok;
    }

    /// <summary>
    /// Same as <see cref="TryMakeDir"/> but returns the FTP reply code + message
    /// on failure so the caller can classify the error (permanent vs transient)
    /// and drive the section blacklist. Logs the failure once here so callers
    /// don't need to.
    /// </summary>
    private static async Task<(bool ok, string code, string msg)> TryMakeDirWithResult(
        FluentFTP.AsyncFtpClient client, string path, CancellationToken ct)
    {
        var (ok, code, msg) = await TryMakeDirCore(client, path, ct, depth: 0);
        if (!ok)
            Log.Warning("MKD failed for {Path}: {Code} {Msg}", path, code, msg);
        return (ok, code, msg);
    }

    private static async Task<(bool ok, string code, string msg)> TryMakeDirCore(
        FluentFTP.AsyncFtpClient client, string path, CancellationToken ct, int depth)
    {
        if (depth > 4)
            return (false, "ERR", "parent recursion depth exceeded");

        var sanitized = Ftp.CpsvDataHelper.SanitizeFtpPath(path);

        // Fast path: directory already exists. CWD is cheap and idempotent on
        // glftpd. We don't care about the resulting working-dir state since
        // all subsequent FXP commands use absolute paths.
        var cwdReply = await client.Execute($"CWD {sanitized}", ct);
        if (cwdReply.Success)
            return (true, cwdReply.Code ?? "250", "exists (CWD)");

        // Create it.
        var reply = await client.Execute($"MKD {sanitized}", ct);
        if (IsMkdSuccess(reply))
            return (true, reply.Code ?? "257", reply.Message ?? "created");

        var mkdCode = reply.Code ?? "";
        var mkdMsg = reply.Message ?? "";

        // 550 + "no such file" style message → the parent doesn't exist yet.
        // This is the legit recursive case — walk up one level, MKD the parent,
        // then retry the original path. One level is usually enough since glftpd
        // sections are shallow (/mp3/0415/Release.Name is only 2 deep), but the
        // recursion handles deeper nesting up to depth 4.
        if (mkdCode == "550" && LooksLikeMissingParent(mkdMsg))
        {
            var parent = GetParentPath(sanitized);
            if (!string.IsNullOrEmpty(parent) && parent != "/" && parent != sanitized)
            {
                var parentResult = await TryMakeDirCore(client, parent, ct, depth + 1);
                if (parentResult.ok)
                {
                    var retry = await client.Execute($"MKD {sanitized}", ct);
                    if (IsMkdSuccess(retry))
                        return (true, retry.Code ?? "257", retry.Message ?? "created after parent");
                    return (false, retry.Code ?? "", retry.Message ?? "(no reply text)");
                }
                return (false, parentResult.code, $"parent MKD failed: {parentResult.msg}");
            }
        }

        return (false, mkdCode, mkdMsg);
    }

    /// <summary>
    /// cbftp's MKD response check: accept 257 / any 2xx as success, and also
    /// accept 550 when the message body indicates the directory already exists.
    /// Different glftpd versions + themes use slightly different phrasings.
    /// </summary>
    private static bool IsMkdSuccess(FluentFTP.FtpReply reply)
    {
        if (reply.Success) return true;
        var msg = reply.Message ?? "";
        return msg.Contains("File exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("already exist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMissingParent(string msg) =>
        msg.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Not a directory", StringComparison.OrdinalIgnoreCase);

    private static string? GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? null : trimmed[..i];
    }

    /// <summary>
    /// Probe for a glftpd dated subdirectory (MMDD format) under the section root.
    /// Sections like 0DAY and MP3 use dated dirs (e.g. /mp3/0409/).
    /// Returns the dated path if found, null otherwise.
    /// </summary>
    private async Task<string?> ProbeDatedDirectory(string serverId, string sectionBase, CancellationToken ct)
    {
        // Only probe sections that commonly use dated dirs
        var sectionName = sectionBase.TrimStart('/').Split('/')[0];
        if (!sectionName.Equals("mp3", StringComparison.OrdinalIgnoreCase) &&
            !sectionName.Equals("0day", StringComparison.OrdinalIgnoreCase))
            return null;

        FtpConnectionPool? pool = null;
        if (_mainPools.TryGetValue(serverId, out var mainPool)) pool = mainPool;
        else if (_pools.TryGetValue(serverId, out var spreadPool)) pool = spreadPool;
        if (pool == null) return null;

        var datePath = sectionBase + "/" + DateTime.UtcNow.ToString("MMdd");
        try
        {
            using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            borrowCts.CancelAfter(TimeSpan.FromSeconds(10));
            await using var conn = await pool.Borrow(borrowCts.Token);
            if (await conn.Client.DirectoryExists(datePath, ct))
            {
                Log.Information("Spread: dated directory found: {Path}", datePath);
                return datePath;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread: dated dir probe failed for {Path}", datePath);
        }
        return null;
    }

    private async Task EnsureDirectoryExists(FluentFTP.AsyncFtpClient client, string dstId,
        string basePath, string relativePath, CancellationToken ct)
    {
        // TryMakeDirWithResult handles missing-parent recursion internally.
        // On failure we classify the reply code+message: permanent (550 path-filter,
        // permission denied, etc.) → add to section blacklist so future races skip
        // this dst at selection. Transient (timeout, 4xx, GnuTLS hiccup) → just throw
        // and let the existing destFailure backoff ladder handle it.
        var (ok, code, msg) = await TryMakeDirWithResult(client, basePath, ct);
        if (!ok)
        {
            RecordIfPermanent(dstId, basePath, code, msg);
            throw new IOException($"MKD failed for {basePath}");
        }

        // If file is in a subdirectory (e.g. "CD1/file.rar"), create nested dirs.
        var dirPart = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dirPart)) return;

        var parts = dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = basePath.TrimEnd('/');
        foreach (var part in parts)
        {
            current += "/" + part;
            var (okSub, codeSub, msgSub) = await TryMakeDirWithResult(client, current, ct);
            if (!okSub)
            {
                RecordIfPermanent(dstId, current, codeSub, msgSub);
                throw new IOException($"MKD failed for {current}");
            }
        }
    }

    private void RecordIfPermanent(string dstId, string path, string code, string msg)
    {
        if (_blacklist == null) return;
        if (!MkdFailureClassifier.IsPermanent(code, msg)) return;
        var name = _serverConfigs.TryGetValue(dstId, out var cfg) ? cfg.Name : dstId;
        _blacklist.RecordPermanentFailure(dstId, name, Section, path, $"{code} {msg}".Trim());
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
