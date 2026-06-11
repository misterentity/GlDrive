using System.IO;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public enum SpreadJobState { Running, Paused, Completed, Failed, Stopped }

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerConfig> _serverConfigs;
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
    private (string serverId, string path, string relName)? _pendingSfv;
    // SFVs already counted into _expectedFileCount, keyed by RELEASE-RELATIVE path
    // (file.Name), NOT the full site path: the same SFV exists at a different full
    // path on every site, and keying by full path counted it once per site after we
    // FXPed it to a dest — doubling the expected total ((17+1)*2=36 for a 20-file
    // release on 2026-06-08), which blocked completion and made cleanup SITE WIPE a
    // zipscript-complete dir. Distinct SFVs (CD1/x.sfv vs CD2/y.sfv) still sum.
    // Guarded by _ownershipLock.
    private readonly HashSet<string> _parsedSfvs = new(StringComparer.OrdinalIgnoreCase);

    // True once ANY source listing has completed (even with 0 files). While false,
    // the engine cannot distinguish "release has no files" from "every source scan
    // failed (pool starvation)" — 29 of 35 races died as no-activity on 2026-06-08
    // with exclusively FAILED source scans, one of them 12s before its first
    // successful listing would have found 22 files.
    private volatile bool _sourceScanSucceeded;

    // Dests whose release dir is CONFIRMED to exist (a scan saw at least one
    // entry in it). Lifts the MKD-denial gate for fill-only dests: a site that
    // may not CREATE the dir can still FILL it once another racer creates it
    // (EnsureDirectoryExists short-circuits on CWD, no MKD sent). Guarded by
    // _ownershipLock.
    private readonly HashSet<string> _destDirConfirmed = new(StringComparer.Ordinal);

    // GlDrive's OWN successful (non-dupe) deliveries per dest. Compared against
    // the scan-derived owned count in cleanup: owned > delivered means files we
    // did NOT send are present (other racers / dupe-skips), i.e. the dir is part
    // of a shared race and must not be wiped. Guarded by _ownershipLock.
    private readonly Dictionary<string, int> _destDelivered = new();

    // Last skip breakdown from a zero-candidate FindBestTransfer pass — surfaced
    // in the no-activity failure message so race history explains WHY nothing
    // moved (a section-blacklist regression zero-dispatched every race for hours
    // on 2026-06-10 with no log trace at INF level).
    private volatile string? _lastSkipSummary;
    private DateTime _lastSkipLogAt;

    // Dests admitted as fill-only (MKD denied; can only receive into a dir some
    // other racer creates). While their dir is UNCONFIRMED they must not count
    // as pending/missing work: on 2026-06-11 every completed zephyr race sat
    // idle waiting on fill-only SYN, ran pointless recovery sweeps for its
    // "missing" files, and recorded itself partial. Guarded by _ownershipLock.
    private readonly HashSet<string> _fillOnlyDests = new(StringComparer.Ordinal);

    // Caller must hold _ownershipLock.
    private bool IsUnopenedFillOnlyNoLock(string id)
        => _fillOnlyDests.Contains(id) && !_destDirConfirmed.Contains(id);

    /// <summary>Fill-only dests whose release dir never appeared — excluded from
    /// history's delivered/clean-complete accounting (they never participated).</summary>
    public IReadOnlyCollection<string> UnopenedFillOnlyDests
    {
        get { lock (_ownershipLock) return _fillOnlyDests.Where(id => !_destDirConfirmed.Contains(id)).ToList(); }
    }

    // Per-destination zipscript signals captured on each scan (see CompletionDetector).
    // _destSawMarker[id] = a completion marker was visible in id's release dir this scan.
    // _destHasMissingStub[id] = a -MISSING- stub was visible this scan.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _destSawMarker = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _destHasMissingStub = new();

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

    // Issue #6: per-dest set of base paths denied by glftpd's dirscript ("553
    // MKD Denied by dirscript"). dirscript runs at MKD time and applies a
    // per-section path filter; if it has denied a path once for this dest, it
    // will deny it every time, so re-attempting the MKD just spams the log
    // (~26 MKD-denied warnings/day in production). Per-job scope only — when
    // the SpreadJob disposes, the set vanishes. All access is guarded by
    // _ownershipLock (the main FindBestTransfer scan loop already holds it).
    private readonly Dictionary<string, HashSet<string>> _destDirscriptDenied =
        new(StringComparer.Ordinal);

    // PRD R2: server IDs we've already logged the auto-download-only flag for,
    // so the "auto-flagged download-only" line prints once per server per race.
    private readonly HashSet<string> _autoDownloadOnlyLogged = new(StringComparer.Ordinal);

    // Sources that returned "Insufficient credits" on RETR. A leeched-out source
    // can't be downloaded FROM, and credits don't replenish mid-race, so once a
    // source 550s for credits we stop selecting it as a transfer source for the
    // rest of this race instead of laddering the blameless destination toward a
    // drop. Per-job scope; NOT persisted (credits return across days, unlike a
    // permission denial). Guarded by _ownershipLock.
    private readonly HashSet<string> _sourceCreditDenied = new(StringComparer.Ordinal);

    // Sources confirmed to have LOST the release mid-race (moved/archived/deleted).
    // Purged from _fileOwnership and never reselected. Guarded by _ownershipLock.
    private readonly HashSet<string> _sourceMigratedAway = new(StringComparer.Ordinal);
    // Single-flight guard for the failover search (0/1 via Interlocked).
    private int _failoverInFlight;
    // The live source set (mirrors RunAsync's local sourceServers + failover additions).
    // Read under _ownershipLock. Used to exclude sources from dest-completion eval.
    private readonly HashSet<string> _sourceServersField = new(StringComparer.Ordinal);
    // Extra source paths discovered by failover (serverId -> release path). Scanned by
    // ScanSites IN ADDITION to sitePaths. ConcurrentDictionary so failover can add from
    // any thread without racing the background scan's enumeration. Never mutate sitePaths.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _extraSourcePaths = new();
    // Snapshot of sitePaths set once at RunAsync start, for read-only access from the
    // failover task (sitePaths itself is never mutated, so reads are safe).
    private Dictionary<string, string>? _sitePathsRef;

    // Set to true when the race terminates due to a skiplist/blacklist denial.
    // Used by EmitRaceOutcome to emit "blacklisted" instead of "aborted".
    private bool _blacklisted = false;

    // section→folder learning: captures the SectionMapper.Resolution that picked
    // each destination's path (null when the fuzzy/substring fallback was used).
    // Lets EmitRaceOutcome attribute the chosen RemoteSection + TriggerRegex so
    // the nightly agent can tell which learned mapping actually routed the race.
    // Keyed by destination serverId. Written only on the single RunAsync setup
    // thread (Phase 2), read in EmitRaceOutcome — no concurrent mutation.
    private readonly Dictionary<string, (string remoteSection, string path, string? triggerRegex)> _resolvedDestRoutes =
        new(StringComparer.Ordinal);

    // Skiplist evaluation trace (captured in Phase 0 for history popup)
    public List<SkiplistTraceEntry>? SkiplistTrace { get; private set; }
    public string SkiplistResult { get; private set; } = "Allowed";

    // Scan debouncing
    private DateTime _lastScanTime = DateTime.MinValue;
    private Task? _backgroundScan;
    private volatile bool _forceScan;
    private volatile bool _isNuked;
    private int _completionRetries;

    // Per-destination completion lifecycle (see CompletionDetector). Keyed by dest
    // serverId. _destAllFilesAt stamps when a dest first held the full file set, so
    // the await timer can expire it. Both written under _ownershipLock during the
    // post-scan reconcile and read in the RunAsync completion gate.
    private readonly Dictionary<string, DestState> _destStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _destAllFilesAt = new(StringComparer.Ordinal);

    // Cooperative pause state. _pausedFlag is checked at the top of every
    // RunAsync tick — when set, the loop awaits _pauseSignal instead of
    // dispatching new transfers. In-flight transfers are NOT aborted; they
    // finish naturally. Resume clears the flag and sets the signal so the
    // waiting loop wakes up. _pauseSignal is initially set (not paused).
    private volatile bool _pausedFlag;
    private readonly System.Threading.ManualResetEventSlim _pauseSignal = new(true);

    // Pre-computed affil checks (computed once per job)
    private readonly Dictionary<string, bool> _affilCache = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string ReleaseName { get; }
    public string Section { get; }
    public SpreadMode Mode { get; }
    public SpreadJobState State { get; private set; } = SpreadJobState.Running;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, SiteProgress> Sites => _siteProgress;

    /// <summary>
    /// Wired by SpreadManager so each transfer respects the global per-server
    /// concurrency gate. Acquires src+dst slots before STOR/RETR; the returned
    /// disposable is released in ExecuteTransfer's finally block. Null when
    /// no global gating is configured (legacy callers / unit tests).
    /// </summary>
    public Func<string, string, CancellationToken, Task<IAsyncDisposable>>? AcquireTransferGates { get; set; }

    // True when this race was triggered by an auto-race (announce listener
    // or notification poll) rather than a manual New Race click. The
    // constructor flags it via the optional knownSourceServerId param —
    // auto-race always provides a source hint; manual races don't.
    public bool IsAutoRace => _knownSourceServerId != null;

    // Pred heuristic: scene "predder" sections live under /pre/ or contain
    // the "pre" segment, and the release name often has PRED tag noise.
    // Surface for the UI card chip without committing to a strict format.
    public bool IsPred =>
        Section.Contains("/pre/", StringComparison.OrdinalIgnoreCase) ||
        Section.StartsWith("/pre", StringComparison.OrdinalIgnoreCase) ||
        ReleaseName.Contains("-PRE-", StringComparison.OrdinalIgnoreCase) ||
        ReleaseName.EndsWith("-PRE", StringComparison.OrdinalIgnoreCase);

    // Race score 0-65535 derived from best destination's transfer progress.
    // Matches the design's scoreboard style (p3.png shows scores like
    // 65,536 / 58,430 / 42,160 / 38,211 per active race). The "best dest"
    // is the destination with the most files owned so far; that ratio
    // scaled to 0-65535 makes a stable, monotonically increasing number
    // for the duration of the race.
    public int Score
    {
        get
        {
            var sites = _siteProgress.Values;
            if (sites.Count == 0) return 0;
            int maxTotal = 0;
            int maxDestOwned = 0;
            foreach (var s in sites)
            {
                if (s.FilesTotal > maxTotal) maxTotal = s.FilesTotal;
                if (!s.IsSource && s.FilesOwned > maxDestOwned) maxDestOwned = s.FilesOwned;
            }
            if (maxTotal == 0) return 0;
            return (int)Math.Min(65535L, (long)maxDestOwned * 65535L / maxTotal);
        }
    }
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
        _serverConfigs = new System.Collections.Concurrent.ConcurrentDictionary<string, ServerConfig>(serverConfigs);
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

    /// <summary>
    /// Resolves a spread pool from the live SpreadManager registry.
    /// Set by SpreadManager so the completion sweep reinits through _spreadPools
    /// (the authoritative registry) rather than the job's stale _pools snapshot.
    /// </summary>
    public Func<string, FtpConnectionPool?>? LivePoolResolver { get; set; }

    /// <summary>Set by SpreadManager: search connected sites (excluding given ids) for
    /// an alternate source of this release. Returns (serverId, path, category) or null.</summary>
    public Func<string, IReadOnlyCollection<string>, CancellationToken, Task<(string serverId, string path, string category)?>>? SourceSearch { get; set; }

    /// <summary>Set by SpreadManager: resolve a server's config from the live registry.
    /// Needed when an alternate source (not an original participant) is spliced in —
    /// _serverConfigs only holds the original race participants. Returns null if unknown.</summary>
    public Func<string, ServerConfig?>? ServerConfigResolver { get; set; }

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
            var blacklistedDests = new List<(string Name, string Reason)>();
            var unmappedDests = new List<string>();
            var downloadOnlyDests = new List<string>();
            var fillOnlyDests = new List<string>();
            foreach (var (serverId, config) in _serverConfigs)
            {
                if (sitePaths.ContainsKey(serverId)) continue; // Already has it
                if (config.SpreadSite.DownloadOnly)
                {
                    downloadOnlyDests.Add(config.Name);
                    continue;
                }

                // PRD R2 — self-healing auto-download-only. A server that has
                // permanently failed uploads across >=3 distinct sections almost
                // certainly can't receive uploads at all (leech-only BNC). Stop
                // discovering this section-by-section: treat it as download-only.
                // Cleared automatically when any upload succeeds (ClearBlacklistOnSuccess
                // removes the entry, dropping the distinct count back below 3).
                if (_blacklist != null && _blacklist.DistinctActiveSectionCount(serverId) >= 3)
                {
                    if (_autoDownloadOnlyLogged.Add(serverId))
                        Log.Information("Spread: {Server} auto-flagged download-only — permanent upload " +
                            "denials across {Count} sections; excluding as a destination until an upload succeeds",
                            config.Name, _blacklist.DistinctActiveSectionCount(serverId));
                    downloadOnlyDests.Add(config.Name);
                    continue;
                }

                // Skip sites that have permanently failed MKD for this section.
                // Without this check, every race re-discovers the 550 the hard
                // way (5 retries per file * N files), burning minutes per race.
                // EXCEPTION — fill-only: an MKD *path* denial ("Not allowed to
                // make directories here") means the account can't CREATE dirs,
                // not that it can't receive files. SYN denied every MKD yet
                // accepted 51 files into a dir another racer had created
                // (2026-06-07). Keep such dests in the race; the per-job denial
                // set (pre-seeded below) prevents MKD spam and the dir-confirmed
                // gate in FindBestTransfer opens them once the dir appears.
                if (_blacklist != null && _blacklist.IsBlacklisted(serverId, Section))
                {
                    var entry = _blacklist.Get(serverId, Section);
                    var reason = entry?.Reason ?? "unknown";
                    if (MkdFailureClassifier.IsPermanentMkdPathDenial(reason))
                    {
                        Log.Information("Spread: {Server} is FILL-ONLY for [{Section}] (MKD denied: {Reason}) — " +
                            "will receive once the release dir exists", config.Name, Section, reason);
                        fillOnlyDests.Add(serverId);
                        // fall through to normal path resolution below
                    }
                    else
                    {
                        Log.Information("Spread: {Server} blacklisted for [{Section}] — {Reason} " +
                            "(first failed {When:u}, {Count} total). Skipping. " +
                            "Delete entry from section-blacklist.json or wait {Ttl} days from {Last:u} to auto-retry.",
                            config.Name, Section, reason,
                            entry?.FirstFailedAt ?? DateTime.UtcNow, entry?.FailureCount ?? 0,
                            (int)SectionBlacklistStore.EntryTtl.TotalDays,
                            entry?.LastFailedAt ?? DateTime.UtcNow);
                        blacklistedDests.Add((config.Name, reason));
                        continue;
                    }
                }

                // section→folder learning: consult the learned SectionMappings FIRST so
                // a trigger-matched mapping routes the destination — today these mappings
                // are inert because RunAsync never calls SectionMapper.Resolve. Strictly
                // additive: only short-circuits when Resolve returns a usable path; every
                // null path (no mapping / no trigger match / empty resolved path) falls
                // through to the original exact→fuzzy→substring chain unchanged.
                var resolution = SectionMapper.Resolve(config.SpreadSite, Section, ReleaseName);
                if (resolution is { } res)
                {
                    // Prefer the mapping's explicit Path; else look up the resolved
                    // RemoteSection key in Sections (case-insensitive) like the rest of
                    // the resolver does. Empty → fall through to the legacy matching.
                    var mappedBase = res.Mapping is { Path.Length: > 0 } m
                        ? m.Path
                        : config.SpreadSite.Sections
                            .FirstOrDefault(kvp => kvp.Key.Equals(res.RemoteSection, StringComparison.OrdinalIgnoreCase))
                            .Value;

                    if (!string.IsNullOrEmpty(mappedBase))
                    {
                        var sectionBase = mappedBase.TrimEnd('/');
                        var destPath = await ProbeDatedDirectory(serverId, sectionBase, ct)
                            is { } datedBase
                                ? datedBase + "/" + ReleaseName
                                : sectionBase + "/" + ReleaseName;

                        sitePaths[serverId] = destPath;
                        // Capture the route so EmitRaceOutcome can attribute it; null
                        // triggerRegex when the mapping used the catch-all default.
                        var trig = res.Mapping?.TriggerRegex;
                        _resolvedDestRoutes[serverId] = (res.RemoteSection, destPath,
                            string.IsNullOrEmpty(trig) || trig == ".*" ? null : trig);
                        Log.Information("Spread: {Server} is DESTINATION via SectionMapping [{Irc} -> {Remote}] {Path}",
                            config.Name, Section, res.RemoteSection, destPath);
                        continue;
                    }
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
                    unmappedDests.Add(config.Name);
                }
            }

            // Pre-seed the per-job MKD-denial set for fill-only dests so the race
            // never attempts the doomed MKD; the dir-confirmed gate in
            // FindBestTransfer admits candidates once a scan sees the dir exist.
            if (fillOnlyDests.Count > 0)
            {
                lock (_ownershipLock)
                {
                    foreach (var id in fillOnlyDests)
                    {
                        if (!sitePaths.TryGetValue(id, out var p)) continue;
                        _fillOnlyDests.Add(id);
                        if (!_destDirscriptDenied.TryGetValue(id, out var set))
                            _destDirscriptDenied[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(p);
                    }
                }

                // A race whose EVERY destination is fill-only can't move a single
                // byte until some other racer creates the dirs — running it just
                // burns a race slot, 1-3 min of scans, and a junk history entry
                // (190 such races in 7h on 2026-06-11, all SYN-only music). Fail
                // fast; the dead-race TTL stops announce re-fires.
                var realDests = sitePaths.Keys
                    .Where(id => !sourceServers.Contains(id)
                              && !_serverConfigs[id].SpreadSite.DownloadOnly)
                    .ToList();
                if (realDests.Count > 0 && realDests.All(id => fillOnlyDests.Contains(id)))
                {
                    SetFailed("All destinations are fill-only (MKD denied) — no site can create the release dir");
                    return;
                }
            }

            if (sitePaths.Count < 2)
            {
                // Build a specific diagnosis instead of pretending no section
                // exists — when most candidate dests are blacklisted from
                // earlier MKD failures (e.g. wrong section path → 550) the
                // user has no way to see that without this hint.
                var parts = new List<string>();
                if (blacklistedDests.Count > 0)
                    parts.Add($"{blacklistedDests.Count} blacklisted ({string.Join(", ", blacklistedDests.Select(b => $"{b.Name}: {b.Reason}"))})");
                if (unmappedDests.Count > 0)
                    parts.Add($"{unmappedDests.Count} unmapped ({string.Join(", ", unmappedDests)})");
                if (downloadOnlyDests.Count > 0)
                    parts.Add($"{downloadOnlyDests.Count} download-only ({string.Join(", ", downloadOnlyDests)})");
                var diagnosis = parts.Count > 0 ? " — " + string.Join("; ", parts) : "";
                SetFailed($"Need 2+ servers — found release on {sourceServers.Count}, no eligible destination for [{Section}]{diagnosis}");
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
                // Build a truthful diagnosis. viableDestinations is empty for one of
                // three reasons that the old "all targets are affil-blocked ()"
                // message conflated — most commonly the release already exists on
                // every candidate site (all sources), which printed empty parens
                // because the affil list only contains NON-source servers.
                var nonSource = sitePaths.Keys
                    .Where(id => !sourceServers.Contains(id))
                    .ToList();
                string reason;
                if (nonSource.Count == 0)
                {
                    reason = $"release already present on all {sourceServers.Count} candidate site(s) — " +
                             "no new destination to spread to";
                }
                else
                {
                    var downOnly = nonSource
                        .Where(id => _serverConfigs[id].SpreadSite.DownloadOnly)
                        .Select(id => _serverConfigs[id].Name)
                        .ToList();
                    var affilBlocked = nonSource
                        .Where(id => _affilCache.GetValueOrDefault(id))
                        .Select(id => _serverConfigs[id].Name)
                        .ToList();
                    var parts = new List<string>();
                    if (downOnly.Count > 0) parts.Add($"download-only ({string.Join(", ", downOnly)})");
                    if (affilBlocked.Count > 0) parts.Add($"affil-blocked ({string.Join(", ", affilBlocked)})");
                    reason = parts.Count > 0
                        ? "all destinations excluded — " + string.Join("; ", parts)
                        : "all destinations excluded (no reason recorded)";
                }
                SetFailed($"No viable destinations for [{Section}] {ReleaseName} — {reason}");
                return;
            }

            Log.Information("Spread starting: {Release} [{Section}] — {Sources} source(s), {Total} total servers",
                ReleaseName, Section, sourceServers.Count, sitePaths.Count);

            lock (_ownershipLock)
            {
                _sourceServersField.Clear();
                foreach (var s in sourceServers) _sourceServersField.Add(s);
            }
            _sitePathsRef = sitePaths;

            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // HardTimeoutSeconds budgets the transfer phase; add the completion-wait
            // window so the global timer can't kill a legitimate await phase, while a
            // wedged race still terminates at this absolute ceiling.
            var ceilingSeconds = _spreadConfig.HardTimeoutSeconds
                + (_spreadConfig.WaitForDestinationComplete
                    ? _spreadConfig.DestinationCompletionWaitMinutes * 60
                    : 0);
            hardTimeout.CancelAfter(TimeSpan.FromSeconds(ceilingSeconds));
            var token = hardTimeout.Token;

            var lastActivity = DateTime.UtcNow;
            var scorer = new SpreadScorer(_speedTracker);
            var consecutiveEmpty = 0;

            while (!token.IsCancellationRequested
                && (State == SpreadJobState.Running || State == SpreadJobState.Paused))
            {
                // Cooperative pause check — block dispatch (but not in-flight
                // transfers) until Resume() sets the signal or Stop() cancels.
                if (_pausedFlag)
                {
                    await Task.Run(() => _pauseSignal.Wait(token), token);
                    if (token.IsCancellationRequested) break;
                    continue;
                }

                // 1. Background scan with adaptive debounce — faster during active racing
                if (_backgroundScan == null || _backgroundScan.IsCompleted)
                {
                    // Adaptive interval: 2s during active transfers, 5s idle, and the
                    // (slower) completion-refresh cadence while only awaiting zipscript.
                    int activeCount;
                    lock (_progressLock)
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                    var scanInterval = activeCount > 0 ? 2.0 : 5.0;
                    if (activeCount == 0 && _spreadConfig.WaitForDestinationComplete && AnyDestAwaiting())
                        scanInterval = Math.Max(scanInterval, _spreadConfig.CompletionRefreshIntervalSeconds);

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
                                  && !IsDestDroppedNoLock(id));
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
                if (_pendingSfv is { } sfv)
                {
                    _pendingSfv = null;
                    _ = ParseSfvForCount(sfv.serverId, sfv.path, sfv.relName);
                }

                // 3. Find best transfer — pre-compute speed map outside lock
                var transfer = FindBestTransfer(sitePaths, scorer);

                if (transfer == null)
                {
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

                    var done = _spreadConfig.WaitForDestinationComplete
                        ? AllDestinationsTerminal(sitePaths, sourceServers)
                        : IsJobComplete();
                    if (done)
                    {
                        // Fire-and-forget transfers may still be in flight — let
                        // them land so their results are counted (and not wiped
                        // as "missing") before exiting. Bounded by the per-
                        // transfer hard timeout, so this cannot spin forever.
                        if (activeCount > 0)
                        {
                            await Task.Delay(1000, token);
                            continue;
                        }
                        // This exit was previously silent — the only trace of a
                        // race ending was the tray notification.
                        Log.Information("Spread race complete: {Release} — {Summary}",
                            ReleaseName, DestinationStateSummary());
                        State = SpreadJobState.Completed;
                        Completed?.Invoke(this);
                        return;
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
                    // A server in a BNC cooldown is pending work too — its 90s
                    // window will expire and candidates resume. Treat it like an
                    // active backoff so the 60s idle timer doesn't fail the race
                    // out from under a known, self-healing cooldown. Same 180s
                    // hard cap prevents a permanently-refusing BNC pinning forever.
                    var anyCooldown = AnyPoolInCooldown();
                    // A dest still inside its completion-wait window is pending work,
                    // not idle — don't let the 60s idle timer fail the race while we
                    // legitimately wait for zipscript. Bounded by the await budget.
                    var awaitingCompletion = _spreadConfig.WaitForDestinationComplete && AnyDestAwaiting();
                    var awaitCapSeconds = _spreadConfig.DestinationCompletionWaitMinutes * 60 + 60;
                    if (((nextBackoff is { } bAt && bAt > DateTime.UtcNow) || anyCooldown
                         || (awaitingCompletion && idleSeconds < awaitCapSeconds))
                        && idleSeconds < Math.Max(180, awaitCapSeconds))
                    {
                        lastActivity = DateTime.UtcNow;
                        idleSeconds = 0;
                    }

                    // cbftp's race-level inactivity timeout is 60s (MAX_CHECKS_BEFORE_TIMEOUT * tick).
                    // Previous 15s killed races that were just waiting on a slow LIST,
                    // a BNC pool reinit, or a longer backoff tier to expire. cbftp also
                    // allows two timeout cycles: first one CLEARS retry state and tries
                    // again, second actually aborts — we follow that pattern below via
                    // _completionRetries (now allowed up to 2).
                    if (activeCount == 0 && idleSeconds > 60)
                    {
                        int missingFiles;
                        lock (_ownershipLock)
                        {
                            missingFiles = 0;
                            foreach (var (serverId, _) in _siteProgress)
                            {
                                if (_serverConfigs[serverId].SpreadSite.DownloadOnly) continue;
                                // An unopened fill-only dest isn't missing work —
                                // nothing can be sent there until someone else
                                // creates the dir. Counting it kept finished races
                                // idling and sweeping for unreachable files.
                                if (IsUnopenedFillOnlyNoLock(serverId)) continue;
                                var owned = _serverFileCount.GetValueOrDefault(serverId);
                                var total = _fileInfos.Count;
                                if (owned < total) missingFiles += total - owned;
                            }
                        }

                        // Also sweep when the source has NEVER been listed
                        // successfully: missingFiles is 0 only because _fileInfos
                        // is empty, and the sweep's pool reinit is exactly the
                        // remedy for the starved scans that caused that.
                        if ((missingFiles > 0 || !_sourceScanSucceeded) && _completionRetries < 2)
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

                            // Reinitialize exhausted spread pools via live registry so a
                            // pool replaced by ReinitDeadPools is not missed here.
                            var poolIds = _pools.Keys.ToList();
                            var freshPools = new Dictionary<string, FtpConnectionPool>();
                            foreach (var serverId in poolIds)
                            {
                                var livePool = LivePoolResolver?.Invoke(serverId) ?? _pools.GetValueOrDefault(serverId);
                                if (livePool == null) continue;
                                try
                                {
                                    await livePool.Reinitialize(token);
                                }
                                catch (Exception ex)
                                {
                                    Log.Debug(ex, "Completion sweep: pool reinit failed for {Server}", serverId);
                                }
                                freshPools[serverId] = livePool;
                            }
                            if (freshPools.Count >= 2) UpdatePools(freshPools);

                            if (_sourceScanSucceeded)
                                Log.Information("Spread completion sweep: {Missing} files still missing, " +
                                    "resetting failures and reinitializing pools — {Release}",
                                    missingFiles, ReleaseName);
                            else
                                Log.Information("Spread completion sweep: source never scanned successfully, " +
                                    "reinitializing pools for another attempt — {Release}", ReleaseName);
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
                        else if (!_sourceScanSucceeded)
                        {
                            // Distinct class: the race didn't fail for lack of work,
                            // we never managed to LIST the source at all. Gets a
                            // shorter dead-race TTL so a retry can catch the release
                            // once pool pressure clears.
                            SetFailed("Source scan never succeeded — pools unavailable (login cap?)");
                        }
                        else
                        {
                            var skips = _lastSkipSummary is { } s ? $" (last pass: {s})" : "";
                            SetFailed($"No activity for 60 seconds, no viable transfers{skips}");
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
            // Let in-flight fire-and-forget transfers settle before the cleanup
            // decision — late deliveries used to land AFTER the wipe judged the
            // dir partial and were destroyed (200MB on 2026-06-08). On cancel
            // paths the linked transfer tokens abort quickly, so this drains
            // fast; 60s caps the wait either way.
            var drainDeadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < drainDeadline)
            {
                int inFlight;
                lock (_progressLock) inFlight = _activeTransfers.Count;
                if (inFlight == 0) break;
                await Task.Delay(1000);
            }

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

            // section→folder learning: attribute the primary destination's resolved
            // route so the agent can correlate (announce section -> chosen folder) with
            // race success. Picks the first SectionMapping-resolved destination; null
            // for every field when only the fuzzy/substring fallback routed this race.
            (string remoteSection, string path, string? triggerRegex)? primaryRoute =
                _resolvedDestRoutes.Count > 0 ? _resolvedDestRoutes.Values.First() : null;

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
                FilesTotal = filesTotal,
                // New optional learning fields (nulls omitted on write — back-compat).
                ResolvedRemoteSection = primaryRoute?.remoteSection,
                DestFolderPath = primaryRoute?.path,
                WasAutoRaced = IsAutoRace,
                MatchedTriggerRegex = primaryRoute?.triggerRegex
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
        Dictionary<string, DestState> destStates;
        Dictionary<string, int> deliveredCounts;
        int total;
        lock (_ownershipLock)
        {
            created = [.._dirsCreated];
            ownedCounts = new Dictionary<string, int>(_serverFileCount);
            destStates = new Dictionary<string, DestState>(_destStates);
            deliveredCounts = new Dictionary<string, int>(_destDelivered);
            total = _expectedFileCount > 0 ? _expectedFileCount : _fileInfos.Count;
        }

        foreach (var serverId in created)
        {
            var owned = ownedCounts.GetValueOrDefault(serverId);
            // If the dest has every file the release needs, it's complete.
            // Leave it alone (don't wipe a good release!).
            if (total > 0 && owned >= total) continue;

            // The dest's zipscript declared it complete (marker seen). NEVER wipe
            // it, even when our own count disagrees — an inflated expected total
            // (SFV double-count) made this path SITE WIPE a validated-complete
            // 3.2GB release on 2026-06-08, which then got re-raced and auto-nuked.
            if (destStates.GetValueOrDefault(serverId) == DestState.Complete)
            {
                Log.Information("Spread cleanup: skipping {Server} — dest is zipscript-complete ({Owned}/{Total} per our count)",
                    _serverConfigs.TryGetValue(serverId, out var c) ? c.Name : serverId, owned, total);
                continue;
            }

            // Files we did NOT deliver are present (other racers uploaded, or our
            // STORs were dupe-skipped against theirs) — the dir belongs to a live
            // shared race. Wiping it would destroy other traders' work; leave it
            // for their zipscript to finish.
            var deliveredHere = deliveredCounts.GetValueOrDefault(serverId);
            if (owned > deliveredHere)
            {
                Log.Information("Spread cleanup: skipping {Server} — {Owned} files present but only {Delivered} delivered by us (shared race, foreign uploads)",
                    _serverConfigs.TryGetValue(serverId, out var c2) ? c2.Name : serverId, owned, deliveredHere);
                continue;
            }

            if (!sitePaths.TryGetValue(serverId, out var path)) continue;
            if (!_pools.TryGetValue(serverId, out var pool)) continue;

            var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
            var sanitized = Ftp.CpsvDataHelper.SanitizeFtpPath(path);

            try
            {
                // Bounded — Borrow(CancellationToken.None) on a login-cap-starved
                // pool blocked a finished race here for 15+ minutes while it held
                // a maxConcurrentRaces slot (2026-06-08), and one job hung forever.
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await using var conn = await pool.Borrow(cleanupCts.Token);

                // Prefer SITE WIPE -r: removes dir + contents without credit
                // deduction. This is the critical difference — if we use DELE
                // or plain RMD, the user gets nuked for leaving incomplete
                // files behind AND pays the credit penalty for deleting their
                // own upload. SITE WIPE is glftpd's explicit "no penalty"
                // removal command.
                var wipeReply = await conn.Client.Execute($"SITE WIPE -r {sanitized}", cleanupCts.Token);
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
                var rmdReply = await conn.Client.Execute($"RMD {sanitized}", cleanupCts.Token);
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
        // Merge in any alternate sources discovered by failover (read-only snapshot).
        // sitePaths itself is NEVER mutated (the dispatch loop enumerates it elsewhere);
        // we build a separate map for this scan only.
        Dictionary<string, string> scanTargets;
        if (_extraSourcePaths.IsEmpty)
            scanTargets = sitePaths;
        else
        {
            scanTargets = new Dictionary<string, string>(sitePaths);
            foreach (var kv in _extraSourcePaths) scanTargets[kv.Key] = kv.Value;
        }

        Log.Information("Spread scan starting for {Count} servers: {Paths}",
            scanTargets.Count, string.Join(", ", scanTargets.Select(kv =>
            {
                var name = _serverConfigs.TryGetValue(kv.Key, out var c) ? c.Name : kv.Key;
                return $"{name}:{kv.Value}";
            })));

        var results = new List<(string serverId, List<SpreadFileInfo> files, ScanSignals signals)>();
        var scanLock = new Lock();

        var tasks = scanTargets.Select(async kvp =>
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
            var signals = new ScanSignals();
            var scanDone = false;
            Exception? lastError = null;

            if (mainPool != null)
            {
                try
                {
                    Log.Information("Spread scan: listing {Server} at {Path} (using main pool)...",
                        serverName, basePath);
                    await ScanDirectoryRecursive(mainPool, basePath, basePath, files, signals, 0, ct);
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
                    await ScanDirectoryRecursive(spreadPool, basePath, basePath, files, signals, 0, ct);
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
                lock (scanLock) results.Add((serverId, files, signals));
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
            foreach (var (serverId, files, signals) in results)
            {
                var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
                Log.Information("Spread scan: {Server} found {Count} files at {Path}",
                    serverName, files.Count, scanTargets.GetValueOrDefault(serverId, "?"));
                ProcessFiles(serverId, files);
                _destSawMarker[serverId] = signals.SawCompletionMarker;
                _destHasMissingStub[serverId] = signals.HasMissingStub;
                if (_sourceServersField.Contains(serverId))
                    _sourceScanSucceeded = true;
                // Any entry (real file or zipscript artifact) proves the release
                // dir exists on this site — opens fill-only dests for transfers.
                if (files.Count > 0 || signals.SawCompletionMarker || signals.HasMissingStub)
                    _destDirConfirmed.Add(serverId);
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
        if (_spreadConfig.WaitForDestinationComplete)
            EvaluateDestCompletion(finalTotal);
        ProgressChanged?.Invoke(this);
    }

    /// <summary>
    /// Detects glftpd zipscript artifact files/dirs that must NEVER be treated as
    /// real release files. Counting these as "owned" makes the engine think a
    /// destination holds files the source lacks, and it tries to FXP them — often
    /// in reverse (dest→source) — which fails forever and starves the real files
    /// of transfer slots so the race never completes (observed 2026-05-20:
    /// Grand.Sumo race spent 35 failed transfers shipping pseudo-files backward).
    ///
    /// Covers:
    ///   - "-MISSING-foo.rar" / "-missing-foo.rar"          (prefix form)
    ///   - "foo.rar.missing" / "foo.rar-missing"            (suffix forms — the
    ///                                                        dash form is what
    ///                                                        glftpd actually emits)
    ///   - "[###::::] - 27% Complete - [site]"              (race progress bar)
    ///   - "[ NUKED ] ...", "[ Incomplete ] ..."            (bracketed status stubs)
    ///   - any 0-byte file starting with "-" or "["         (belt-and-suspenders)
    /// </summary>
    internal static bool IsZipscriptArtifact(string name, long size)
    {
        if (string.IsNullOrEmpty(name)) return false;

        if (name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("-missing", StringComparison.OrdinalIgnoreCase)) return true;

        // Race progress / completion-state indicators. glftpd's zipscript writes
        // 0-byte marker files (or dirs) named with bracketed status text and a
        // percentage. Real releases never contain "% Complete" or start with '['.
        if (name.Contains("% Complete", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith('[') && name.Contains(']')) return true;

        // Tiny stubs whose name starts with a marker char are almost always
        // zipscript artifacts; real release files are multi-KB.
        if (size == 0 && (name.StartsWith('-') || name.StartsWith('['))) return true;

        return false;
    }

    private async Task ScanDirectoryRecursive(FtpConnectionPool pool, string basePath,
        string currentPath, List<SpreadFileInfo> files, ScanSignals signals, int depth, CancellationToken ct)
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

                if (CompletionDetector.IsCompletionMarker(item.Name, _spreadConfig.CompletionMarkers))
                    signals.SawCompletionMarker = true;
                if (CompletionDetector.IsMissingStub(item.Name, item.Size))
                    signals.HasMissingStub = true;

                // Skip zipscript artifact directories — some configs render the
                // race progress bar ("[###] - NN% Complete - [site]") as a dir.
                // Recursing into it is wasted LISTs at best, and its name must
                // never become a transferable entry.
                if (IsZipscriptArtifact(item.Name, item.Size)) continue;

                // Recurse into subdirectories (Sample/, Subs/, CD1/, etc.)
                await ScanDirectoryRecursive(pool, basePath, item.FullName, files, signals, depth + 1, ct);
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

                if (CompletionDetector.IsCompletionMarker(item.Name, _spreadConfig.CompletionMarkers))
                    signals.SawCompletionMarker = true;
                if (CompletionDetector.IsMissingStub(item.Name, item.Size))
                    signals.HasMissingStub = true;

                // Skip glftpd zipscript artifacts — -MISSING placeholders, "-missing"
                // suffix stubs, and "[###] - NN% Complete - [site]" race progress
                // markers. Counting them as "owned" makes the engine ship pseudo-files
                // (often in reverse) and starves the real files of slots.
                if (IsZipscriptArtifact(item.Name, item.Size)) continue;

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

            // Only arm for SFVs not already counted — re-arming for a parsed SFV
            // wasted a download every scan cycle. (Called under _ownershipLock.)
            if (fileName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)
                && !_parsedSfvs.Contains(file.Name))
                _pendingSfv = (serverId, file.FullPath, file.Name);
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
                // Actual race role, NOT "has files" — the old `owned > 0` test
                // reclassified every dest as a source the moment it received its
                // first file, which zeroed FilesDelivered in race history and
                // mislabeled roles in telemetry.
                progress.IsSource = _sourceServersField.Contains(serverId);
                progress.IsComplete = owned >= total && total > 0;
            }
        }

        ProgressChanged?.Invoke(this);
    }

    private async Task ParseSfvForCount(string serverId, string sfvPath, string relName)
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
                // Count each distinct SFV exactly once, keyed by release-relative
                // path so the same SFV seen on source AND dest counts once.
                if (_parsedSfvs.Add(relName))
                    _expectedFileCount += lineCount + 1; // +1 for the SFV itself; accumulates across DISTINCT SFVs
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

        // Read the pool registry once (volatile, replaced by UpdatePools). Used to
        // skip any server currently in a BNC cooldown: its pool has parked new
        // connections, so every Borrow routed through it throws "Server in BNC
        // cooldown" and ExecuteTransfer logs an FXP error. Pre-fix this hammered
        // 1,371 times in one day on superbnc — every file of every queued race
        // re-attempted the dead route each pass. IsInCooldown auto-clears on the
        // next successful connect or when the 90s window expires, so candidates
        // resume on their own.
        var poolsSnap = _pools;

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
        var skippedCooldown = 0;
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
                    // Source parked for this race — out of credits, can't pull
                    // from it. Skip every (file, this-src, *) candidate.
                    if (_sourceCreditDenied.Contains(srcId)) { skippedFailures++; continue; }

                    // Source's pool is in a BNC cooldown — new connections are
                    // parked, so a Borrow would just throw. Skip every candidate
                    // pulling from it until the cooldown clears.
                    if (poolsSnap.TryGetValue(srcId, out var srcPoolCd) && srcPoolCd.IsInCooldown)
                    { skippedCooldown++; continue; }

                    foreach (var (dstId, dstBasePath) in sitePaths)
                    {
                        if (srcId == dstId) continue;
                        if (owners.Contains(dstId)) { skippedOwned++; continue; }
                        if (_inFlightFiles.Contains((fileName, dstId))) { skippedOwned++; continue; }

                        // Dest's pool is in a BNC cooldown — can't open a data/
                        // control connection to receive. Skip until it clears.
                        if (poolsSnap.TryGetValue(dstId, out var dstPoolCd) && dstPoolCd.IsInCooldown)
                        { skippedCooldown++; continue; }

                        // Per-destination backoff. The dest recently failed and
                        // is parked until retryAt; MaxValue means dropped from
                        // this race entirely (blew the backoff ladder).
                        if (retrySnapshot.TryGetValue(dstId, out var until)
                            && CandidatePredicates.DestInBackoff(until, now))
                        { skippedBackoff++; continue; }

                        // Issue #6: if dirscript already denied any prefix of
                        // this dest's base path during the current job, don't
                        // try again — dirscript is deterministic and re-MKDing
                        // just spams 553 "Denied by dirscript" warnings.
                        // Treated identically to a backoff skip — UNLESS a scan
                        // has since confirmed the dir exists (another racer
                        // created it): then CWD succeeds without any MKD and a
                        // fill-only dest can receive (SYN accepted 51 files
                        // into a pre-existing dir while denying every MKD).
                        if (_destDirscriptDenied.TryGetValue(dstId, out var deniedSet)
                            && CandidatePredicates.DirscriptBlocked(dstBasePath, deniedSet)
                            && !_destDirConfirmed.Contains(dstId))
                        { skippedBackoff++; continue; }

                        // SFV-first: block non-SFV files until SFV is delivered to this dest
                        if (destsNeedingSfv != null
                            && CandidatePredicates.SfvFirstBlocked(fileName, destsNeedingSfv.Contains(dstId)))
                            continue;

                        var dstConfig = _serverConfigs[dstId];
                        if (dstConfig.SpreadSite.DownloadOnly) { skippedDownloadOnly++; continue; }
                        if (_affilCache.GetValueOrDefault(dstId)) { skippedAffil++; continue; }

                        // Check slots from snapshot (no nested lock)
                        var dstActive = activeTransferSnapshot.GetValueOrDefault(dstId);
                        var srcActive = activeTransferSnapshot.GetValueOrDefault(srcId);
                        if (CandidatePredicates.SlotsFull(dstActive, dstConfig.SpreadSite.MaxUploadSlots,
                                srcActive, _serverConfigs[srcId].SpreadSite.MaxDownloadSlots))
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

                        // Per-pair retry cap (cbftp's MAX_SINGLE_PAIR_FILE_TRANSFER_ATTEMPTS).
                        failureSnapshot.TryGetValue((fileName, srcId, dstId), out var pairFails);
                        if (CandidatePredicates.PairRetryCapped(pairFails))
                        { skippedFailures++; continue; }

                        // Per-file global retry cap (cbftp's MAX_TRANSFER_ATTEMPTS_BEFORE_SKIP = 7).
                        // Sum failures across all src->dst pairs for this filename. Without this
                        // cap, a file that hates every route can churn forever, eating slots
                        // away from files that could succeed.
                        var fileTotalFails = 0;
                        foreach (var kv in failureSnapshot)
                        {
                            if (kv.Key.Item1 == fileName) fileTotalFails += kv.Value;
                        }
                        if (CandidatePredicates.FileRetryCapped(fileTotalFails))
                        { skippedFailures++; continue; }

                        candidateCount++;

                        var ownedPercent = _pools.Count > 0
                            ? owners.Count / (double)_pools.Count
                            : 0;

                        var score = scorer.Score(fileInfo, srcId, dstId,
                            dstConfig.SpreadSite.Priority, ownedPercent,
                            maxFileSize, maxSpeed, elapsed, Mode,
                            priorFailures: pairFails);

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
                var summary = $"owned={skippedOwned} downloadOnly={skippedDownloadOnly} affil={skippedAffil} " +
                              $"slots={skippedSlots} failures={skippedFailures} backoff/dirscript={skippedBackoff} cooldown={skippedCooldown}";
                _lastSkipSummary = summary;
                // Information (rate-limited), was Debug — invisible stalls hid a
                // zero-dispatch regression for hours.
                if ((DateTime.UtcNow - _lastSkipLogAt).TotalSeconds >= 30)
                {
                    _lastSkipLogAt = DateTime.UtcNow;
                    Log.Information("FindBestTransfer: {Files} files, 0 candidates ({Summary}) — {Release}",
                        _fileInfos.Count, summary, ReleaseName);
                }
                if (skippedSlots > 0 && skippedOwned == 0)
                    Log.Debug("FindBestTransfer slot details: {SlotInfo}",
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
        var pools = _pools; // read volatile field once to avoid torn reads from UpdatePools
        var srcPool = pools[srcId];
        var dstPool = pools[dstId];
        var mode = FxpModeDetector.Detect(srcPool, dstPool);

        // Slots already claimed by TryClaimSlots

        PooledConnection? srcConn = null;
        PooledConnection? dstConn = null;
        IAsyncDisposable? gates = null;

        try
        {
            // Acquire global per-server gates BEFORE borrowing the connection pool.
            // The gate cap is enforced across ALL concurrent jobs and auto-tightens
            // when a BNC's 530 reveals its login cap. Doing this before Borrow means
            // we don't even ask the pool until BNC has slot headroom — eliminates
            // the burst of 530s that triggered today's GnuTLS native crashes.
            if (AcquireTransferGates != null)
            {
                try
                {
                    gates = await AcquireTransferGates(srcId, dstId, ct);
                }
                catch (TimeoutException tex)
                {
                    Log.Debug("Gate timeout for {File} ({Src}->{Dst}): {Msg} — rescheduling",
                        file.Name,
                        _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId,
                        _serverConfigs.TryGetValue(dstId, out var dc) ? dc.Name : dstId,
                        tex.Message);
                    // Not a real failure — the file gets re-scored on the next pass
                    // and another (file,src,dst) wins the gate.
                    return;
                }
            }

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
                _spreadConfig.TransferTimeoutSeconds, ct,
                raceId: Id, srcServerId: srcId, dstServerId: dstId,
                fileSizeBytes: file.Size);

            lock (_progressLock) _activeTransfers.Remove(transferKey);

            if (ok)
            {
                var duration = DateTime.UtcNow - startTime;
                // Don't pollute speed stats with dupe-skips — no bytes flowed.
                // The file IS on the destination (glftpd's dupescript rejected
                // our STOR because it already exists), so we still count it as
                // owned for race-completion bookkeeping.
                if (!transfer.WasDupe)
                    _speedTracker.RecordTransfer(srcId, dstId, file.Size, duration);

                lock (_ownershipLock)
                {
                    if (_fileOwnership.TryGetValue(file.Name, out var owners) && owners.Add(dstId))
                    {
                        _serverFileCount.TryGetValue(dstId, out var cnt);
                        _serverFileCount[dstId] = cnt + 1;
                    }
                    if (!transfer.WasDupe)
                    {
                        _destDelivered.TryGetValue(dstId, out var dd);
                        _destDelivered[dstId] = dd + 1;
                    }
                    _serversWithSuccessfulTransfer.Add(dstId);
                    // A single success proves the dest is working — clear any
                    // accumulated failure count + backoff window so future
                    // transient 550s don't immediately re-trigger the ladder.
                    _destFailureCount.Remove(dstId);
                    _destRetryAt.Remove(dstId);
                }
                _forceScan = true; // Rescan — new files likely appeared on source from other racers
                if (transfer.WasDupe)
                    Log.Information("FXP dupe-skip: {File} ({Src} -> {Dst}) — already on dest",
                        file.Name, _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
                else
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
                //
                // v3.6 Phase 3a: attribute the poison to the side that actually failed
                // (FxpTransfer.FaultSide) instead of always poisoning both. A clean
                // one-sided protocol failure (STOR 5xx ⇒ dest, RETR 5xx ⇒ source)
                // leaves the blameless peer's session intact, so poisoning it forced a
                // needless reconnect (burning a login, feeding the BNC cooldown).
                // Ambiguous/data/TLS failures stay None ⇒ Both (never under-poison a
                // possibly-corrupt session). Spread pools validate-on-borrow as a
                // safety net for any mis-narrowed connection.
                ApplyPoisonAttribution(transfer, srcConn, dstConn);

                lock (_failureLock)
                {
                    var failKey = (file.Name, srcId, dstId);
                    _failureCounts.TryGetValue(failKey, out var count);
                    _failureCounts[failKey] = count + 1;
                }
                Log.Warning("FXP failed: {File} ({Src} -> {Dst}): {Error}", file.Name,
                    _serverConfigs[srcId].Name, _serverConfigs[dstId].Name, transfer.ErrorMessage);

                // Permanent UPLOAD denial (STOR 553 "no upload rights" /
                // path-filter)? The account can't write to this dest tree at all
                // — retrying is futile. Drop the dest for this race immediately
                // (in-race guard) AND blacklist (dst, section) so future races
                // skip it. Observed 2026-05-22: 119 SYN->superbnc STOR failures
                // because superbnc is leech-only; each was retried to the cap.
                if (MkdFailureClassifier.IsPermanentUploadDenial(transfer.ErrorMessage))
                {
                    // In-race guard: add this dest's base path to the denied set so
                    // FindBestTransfer stops picking it for every remaining file.
                    lock (_ownershipLock)
                    {
                        if (!_destDirscriptDenied.TryGetValue(dstId, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _destDirscriptDenied[dstId] = set;
                        }
                        set.Add(dstBasePath);
                    }
                    // Persistent blacklist so future races skip (dst, section).
                    if (_blacklist != null)
                    {
                        var dn = _serverConfigs.TryGetValue(dstId, out var c2) ? c2.Name : dstId;
                        _blacklist.RecordPermanentFailure(dstId, dn, Section, dstBasePath,
                            transfer.ErrorMessage ?? "upload denied");
                    }
                    Log.Information("Spread: {Dst} permanently can't receive [{Section}] (upload denied) — " +
                        "dropped for this race + blacklisted",
                        _serverConfigs.TryGetValue(dstId, out var c3) ? c3.Name : dstId, Section);
                }

                // Source out of credits (RETR 550 Insufficient credits)? This is
                // a SOURCE-side condition — every file pulled from this source
                // will fail identically and credits won't return mid-race. Park
                // the source for the rest of the race so FindBestTransfer stops
                // routing through it, rather than laddering the (blameless) dest
                // toward a 5-failure drop. Not persisted: credits come back.
                if (MkdFailureClassifier.IsCreditExhaustion(transfer.ErrorMessage))
                {
                    bool firstTime;
                    lock (_ownershipLock) firstTime = _sourceCreditDenied.Add(srcId);
                    if (firstTime)
                        Log.Warning("Spread: source {Src} out of credits ([{Section}]) — parking as a " +
                            "transfer source for this race — {Error}",
                            _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId, Section,
                            transfer.ErrorMessage);
                    _forceScan = true;
                }
                else if (_spreadConfig.AlternateSourceSearch &&
                         MkdFailureClassifier.IsSourceFileMissing(transfer.ErrorMessage))
                {
                    // Source 550'd "no such file" — it may have moved the release off
                    // mid-race. Don't punish the (blameless) dest; confirm + fail over.
                    _ = HandleSourceMigration(srcId, _cts.Token);
                    _forceScan = true;
                }
                else
                {
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Job cancelled — don't log as error
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;
        }
        catch (OperationCanceledException)
        {
            // Borrow timeout — pool exhausted, likely ghost connections on server.
            // Deliberately does NOT bump _failureCounts: the transfer never
            // started, so this is pool congestion, not a problem with the FILE.
            // Counting these burned the pair (4) / file (7) retry budgets during
            // login-cap storms and permanently blocked the last few files of a
            // race (My.Family 2026-06-10: 25 min of borrow timeouts on 5 files
            // → 19/22 partial → wipe). Mirrors the gate-timeout handling above:
            // the file simply gets re-scored on the next pass.
            Log.Warning("FXP borrow timeout: {File} ({Src} -> {Dst}) — pool exhausted, " +
                "server may have ghost connections (try !username login to kill them)",
                file.Name, _serverConfigs[srcId].Name, _serverConfigs[dstId].Name);
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;
        }
        catch (Exception ex)
        {
            // BNC cooldown thrown at Borrow time (a server entered cooldown between
            // the FindBestTransfer scan that picked this route and this execute).
            // FindBestTransfer skips cooled-down servers, so this is a rare scan/
            // execute race, not a real transfer failure — no connection was even
            // borrowed (srcConn/dstConn are null, nothing to poison). Log quietly
            // so it doesn't masquerade as an FXP error. The route resumes once the
            // cooldown clears.
            if (ex is InvalidOperationException &&
                ex.Message.Contains("BNC cooldown", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("FXP deferred (BNC cooldown): {File} ({Src} -> {Dst})", file.Name, srcId, dstId);
            }
            else
            {
                Log.Warning(ex, "FXP transfer error: {File} ({Src} -> {Dst})", file.Name, srcId, dstId);
            }

            // Mark connections as poisoned so pool discards them instead of reusing
            // (GnuTLS stream may be corrupt after failed/cancelled transfer)
            if (srcConn != null) srcConn.Poisoned = true;
            if (dstConn != null) dstConn.Poisoned = true;
        }
        finally
        {
            if (srcConn != null) await srcConn.DisposeAsync();
            if (dstConn != null) await dstConn.DisposeAsync();

            // Release global gates AFTER pool connections — keeps the BNC slot
            // accounting accurate (a returned-to-pool connection still occupies
            // a BNC login until the pool actually closes it on rotation).
            if (gates != null)
            {
                try { await gates.DisposeAsync(); } catch { }
            }

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
    /// Confirm a suspected source migration (RETR 550 not-found), purge the dead
    /// source's ownership, and — if no remaining source holds the missing files —
    /// search connected sites for an alternate source and splice it (its spread pool +
    /// scan path) into the running race. Nuke-guarded; single-flighted.
    /// </summary>
    private async Task HandleSourceMigration(string srcId, CancellationToken ct)
    {
        if (_isNuked) return;
        if (Interlocked.CompareExchange(ref _failoverInFlight, 1, 0) != 0) return; // already running
        try
        {
            lock (_ownershipLock)
                if (_sourceMigratedAway.Contains(srcId)) return; // already handled

            var srcPath = _sitePathsRef != null && _sitePathsRef.TryGetValue(srcId, out var p) ? p : null;
            if (srcPath == null) return;

            // 1. Confirming re-probe: does the source still have its release dir?
            if (await SourceStillHasRelease(srcId, srcPath, ct))
            {
                Log.Information("Spread: source {Src} 550'd but release dir still present — transient, not migrating",
                    _serverConfigs.TryGetValue(srcId, out var sc0) ? sc0.Name : srcId);
                return;
            }

            // 2. Purge the dead source's ownership (keep it in _sourceServersField so it
            //    stays excluded from dest-completion eval; FindBestTransfer routes by
            //    ownership, which is now empty for it).
            lock (_ownershipLock)
            {
                _sourceMigratedAway.Add(srcId);
                foreach (var owners in _fileOwnership.Values) owners.Remove(srcId);
                _serverFileCount[srcId] = 0;
            }
            Log.Warning("Spread: source {Src} migrated the release away mid-race — searching for an alternate source ({Release})",
                _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId, ReleaseName);

            // 3. If another known source still has the missing files, just let the loop reroute.
            if (HasRemainingSourceForMissingFiles()) { _forceScan = true; return; }

            // 4. Search connected sites for an alternate source.
            if (SourceSearch == null) return;
            var exclude = new HashSet<string>(StringComparer.Ordinal) { srcId };
            lock (_ownershipLock) foreach (var s in _sourceMigratedAway) exclude.Add(s);
            // Don't search the destinations as sources.
            if (_sitePathsRef != null)
                foreach (var id in _sitePathsRef.Keys)
                {
                    bool isSrc; lock (_ownershipLock) isSrc = _sourceServersField.Contains(id);
                    if (!isSrc) exclude.Add(id);
                }

            var alt = await SourceSearch(ReleaseName, exclude, ct);
            if (alt is not { } a)
            {
                Log.Information("Spread: no alternate source found for {Release} — affected dests will time out", ReleaseName);
                return;
            }

            // 5. Splice the alternate source in: inject its spread pool into _pools so
            //    ExecuteTransfer can RETR from it, register its scan path, mark it a source.
            var live = LivePoolResolver?.Invoke(a.serverId);
            if (live == null)
            {
                Log.Warning("Spread: alternate source {Src} has no live spread pool — cannot use", a.serverId);
                return;
            }
            // _serverConfigs only holds the original participants — ProcessFiles,
            // TryClaimSlots and FindBestTransfer all index it by serverId, so the alt
            // source's config MUST be registered before the next scan routes through it.
            // Added under _ownershipLock (the lock ProcessFiles/FindBestTransfer hold for
            // their reads) and BEFORE _forceScan, so the next scan sees a complete entry.
            var altConfig = ServerConfigResolver?.Invoke(a.serverId);
            if (altConfig == null)
            {
                Log.Warning("Spread: alternate source {Src} has no resolvable config — cannot use", a.serverId);
                return;
            }
            lock (_ownershipLock)
            {
                if (!_serverConfigs.ContainsKey(a.serverId))
                    _serverConfigs[a.serverId] = altConfig;
            }
            var merged = new Dictionary<string, FtpConnectionPool>(_pools) { [a.serverId] = live };
            UpdatePools(merged);
            _extraSourcePaths[a.serverId] = a.path;
            lock (_ownershipLock) _sourceServersField.Add(a.serverId);
            // Ensure a progress entry exists so TryClaimSlots can route through the alt
            // source (it indexes _siteProgress[srcId] directly). Sources don't otherwise
            // get a progress entry from ProcessFiles.
            lock (_progressLock)
            {
                if (!_siteProgress.ContainsKey(a.serverId))
                    _siteProgress[a.serverId] = new SiteProgress
                    {
                        ServerId = a.serverId,
                        ServerName = altConfig.Name,
                        IsSource = true
                    };
            }
            Log.Information("Spread: alternate source {Src} found at {Path} — resuming {Release}",
                _serverConfigs.TryGetValue(a.serverId, out var sc2) ? sc2.Name : a.serverId, a.path, ReleaseName);
            _forceScan = true; // next scan populates ownership from the new source
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread: source-migration handling failed for {Release}", ReleaseName);
        }
        finally
        {
            Interlocked.Exchange(ref _failoverInFlight, 0);
        }
    }

    /// <summary>DirectoryExists probe of a source's release dir (confirming re-probe).
    /// Probe failure is NOT treated as proof of migration (returns true = assume present).</summary>
    private async Task<bool> SourceStillHasRelease(string serverId, string path, CancellationToken ct)
    {
        FtpConnectionPool? pool = null;
        if (_mainPools.TryGetValue(serverId, out var mainPool)) pool = mainPool;
        else if (_pools.TryGetValue(serverId, out var spreadPool)) pool = spreadPool;
        if (pool == null) return true;
        try
        {
            using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            borrowCts.CancelAfter(TimeSpan.FromSeconds(15));
            await using var conn = await pool.Borrow(borrowCts.Token);
            return await conn.Client.DirectoryExists(path, ct);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread: source re-probe failed for {Server} {Path} — assuming present", serverId, path);
            return true;
        }
    }

    /// <summary>True if some non-migrated source still owns at least one file.</summary>
    private bool HasRemainingSourceForMissingFiles()
    {
        lock (_ownershipLock)
        {
            foreach (var (_, owners) in _fileOwnership)
                foreach (var o in owners)
                {
                    if (_sourceMigratedAway.Contains(o)) continue;
                    if (_sourceServersField.Contains(o)) return true;
                }
            return false;
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
                // Issue #8: "forcibly closed by the remote host" signals BNC
                // throttling — repeat reconnects just deepen the throttle. Skip
                // a rung on the backoff ladder so the dest gets parked longer
                // faster than a one-off 550 would.
                var bump = !string.IsNullOrEmpty(errorMessage) &&
                    errorMessage.Contains("forcibly closed by the remote host",
                        StringComparison.OrdinalIgnoreCase)
                    ? 2 : 1;
                newCount = prev + bump;
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
        else if (retryAt == DateTime.MaxValue)
        {
            // Already dropped for this race (a prior failure blew the backoff
            // ladder). Don't compute (MaxValue - now) — that printed the absurd
            // "backing off 251622991756s" line. Just note the repeat failure.
            Log.Debug("Spread: destination {Dst} already dropped this race — repeat failure ({Kind}) — {Err}",
                dstName, isMkd ? "MKD" : "FXP", errorMessage ?? "(no message)");
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

    /// <summary>
    /// Poison the connection(s) implicated by a failed transfer. Narrows to one
    /// side only for unambiguous clean protocol failures (FxpTransfer.FaultSide);
    /// None/Both poison both — the conservative default that never leaves a
    /// possibly-corrupt GnuTLS session in the pool. Only ever SETS Poisoned (never
    /// clears), so an earlier explicit poison (e.g. the Relay fallback) is a floor.
    /// </summary>
    private static void ApplyPoisonAttribution(FxpTransfer transfer, PooledConnection? srcConn, PooledConnection? dstConn)
    {
        switch (transfer.FaultSide)
        {
            case FxpFaultSide.Source:
                if (srcConn != null) srcConn.Poisoned = true;
                Log.Debug("Poison attribution: source only (FaultSide=Source)");
                break;
            case FxpFaultSide.Dest:
                if (dstConn != null) dstConn.Poisoned = true;
                Log.Debug("Poison attribution: dest only (FaultSide=Dest)");
                break;
            default: // None or Both — ambiguous, poison both
                if (srcConn != null) srcConn.Poisoned = true;
                if (dstConn != null) dstConn.Poisoned = true;
                break;
        }
    }

    /// <summary>
    /// True if any participating server's pool is currently in a BNC cooldown.
    /// Used by the race loop to treat a cooldown as pending work (keeps the idle
    /// timer fresh) rather than letting a self-healing 90s window fail the race.
    /// </summary>
    private bool AnyPoolInCooldown()
    {
        var pools = _pools;
        foreach (var pool in pools.Values)
            if (pool.IsInCooldown) return true;
        return false;
    }

    private bool IsDestDropped(string dstId)
    {
        lock (_ownershipLock)
        {
            return _destRetryAt.TryGetValue(dstId, out var until) && until == DateTime.MaxValue;
        }
    }

    // Caller must already hold _ownershipLock.
    private bool IsDestDroppedNoLock(string dstId)
        => _destRetryAt.TryGetValue(dstId, out var until) && until == DateTime.MaxValue;

    /// <summary>
    /// Recompute every non-download-only destination's completion state from the
    /// latest scan. Marker/heuristic -> Complete; full file set but unconfirmed ->
    /// AwaitingCompletion (stamping _destAllFilesAt); otherwise Transferring. The
    /// await->TimedOut transition is time-based and applied in the RunAsync gate.
    /// Called from ScanSites after the FilesTotal reconcile.
    /// </summary>
    private void EvaluateDestCompletion(int finalTotal)
    {
        lock (_ownershipLock)
        {
            // Only actual race participants — _siteProgress holds every pooled
            // server, so a site excluded at race start (section-blacklisted,
            // rules-denied) would otherwise haunt the race as a phantom
            // forever-"pending" destination in DestinationState and sweep
            // missing-counts (observed: SYN reported pending in races it
            // never joined, 2026-06-08).
            var participants = _sitePathsRef?.Keys ?? (IEnumerable<string>)_siteProgress.Keys;
            foreach (var serverId in participants)
            {
                if (!_serverConfigs.TryGetValue(serverId, out var cfg)) continue;
                if (cfg.SpreadSite.DownloadOnly) continue;
                if (_sourceServersField.Contains(serverId)) continue; // sources aren't dests
                // Unopened fill-only dests get no completion state at all — they
                // never participated, and a "pending" entry here made every
                // otherwise-complete race report partial.
                if (IsUnopenedFillOnlyNoLock(serverId)) { _destStates.Remove(serverId); continue; }
                var owned = _serverFileCount.GetValueOrDefault(serverId);
                var sawMarker = _destSawMarker.GetValueOrDefault(serverId);
                var hasMissing = _destHasMissingStub.GetValueOrDefault(serverId);
                var state = CompletionDetector.Evaluate(owned, finalTotal, sawMarker, hasMissing);

                // Preserve a prior terminal verdict (don't flap Complete back to
                // Transferring if a later scan momentarily under-counts).
                if (_destStates.TryGetValue(serverId, out var prev) &&
                    (prev == DestState.Complete || prev == DestState.TimedOut))
                    continue;

                _destStates[serverId] = state;
                if (state == DestState.AwaitingCompletion)
                    _destAllFilesAt.TryAdd(serverId, DateTime.UtcNow);
                else if (state == DestState.Transferring)
                    _destAllFilesAt.Remove(serverId);
            }
        }
    }

    /// <summary>
    /// True when every participating (non-download-only, not-dropped) destination is
    /// in a terminal state. Applies the await->TimedOut transition using the
    /// completion-wait budget. Used by the RunAsync gate when WaitForDestinationComplete.
    /// </summary>
    private bool AllDestinationsTerminal(Dictionary<string, string> sitePaths, HashSet<string> sourceServers)
    {
        lock (_ownershipLock)
        {
            var states = new List<DestState>();
            var now = DateTime.UtcNow;
            foreach (var id in sitePaths.Keys)
            {
                if (sourceServers.Contains(id)) continue;
                if (_serverConfigs[id].SpreadSite.DownloadOnly) continue;
                if (IsDestDroppedNoLock(id)) { states.Add(DestState.TimedOut); continue; }
                // Unopened fill-only dest = terminal for gating purposes (the race
                // must not wait on it), but evaluated dynamically so it revives if
                // a scan later confirms the dir appeared.
                if (IsUnopenedFillOnlyNoLock(id)) { states.Add(DestState.TimedOut); continue; }

                var state = _destStates.GetValueOrDefault(id, DestState.Transferring);
                if (state == DestState.AwaitingCompletion &&
                    _destAllFilesAt.TryGetValue(id, out var at) &&
                    CompletionDetector.IsAwaitExpired(at, now, _spreadConfig.DestinationCompletionWaitMinutes))
                {
                    state = DestState.TimedOut;
                    _destStates[id] = state;
                    Log.Information("Spread: dest {Dst} timed out awaiting completion ({Min}min) — {Release}",
                        _serverConfigs.TryGetValue(id, out var c) ? c.Name : id,
                        _spreadConfig.DestinationCompletionWaitMinutes, ReleaseName);
                }
                states.Add(state);
            }
            return CompletionDetector.AllTerminal(states);
        }
    }

    /// <summary>True if any dest is still within its completion-wait window (so the
    /// idle timer must not fail the race — the await is legitimate pending work).</summary>
    private bool AnyDestAwaiting()
    {
        lock (_ownershipLock)
        {
            var now = DateTime.UtcNow;
            foreach (var (id, state) in _destStates)
            {
                if (_sourceServersField.Contains(id)) continue;
                if (state != DestState.AwaitingCompletion) continue;
                if (_destAllFilesAt.TryGetValue(id, out var at) &&
                    !CompletionDetector.IsAwaitExpired(at, now, _spreadConfig.DestinationCompletionWaitMinutes))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Compact per-dest completion summary for race history, e.g. "2 complete · 1 timeout".</summary>
    internal string DestinationStateSummary()
    {
        lock (_ownershipLock)
        {
            if (_destStates.Count == 0) return "";
            int complete = 0, timeout = 0, pending = 0;
            foreach (var s in _destStates.Values)
            {
                if (s == DestState.Complete) complete++;
                else if (s == DestState.TimedOut) timeout++;
                else pending++;
            }
            var parts = new List<string>();
            if (complete > 0) parts.Add($"{complete} complete");
            if (timeout > 0) parts.Add($"{timeout} timeout");
            if (pending > 0) parts.Add($"{pending} pending");
            return string.Join(" · ", parts);
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
            RecordPermanentMkdDenialIfMatch(dstId, basePath, code, msg);
            throw new IOException($"MKD failed for {basePath}");
        }

        // Self-heal: this dest just successfully MKD'd for this section, so any
        // stale blacklist entry from a prior permission issue is no longer true.
        // Clear it so the user doesn't have to hand-edit section-blacklist.json
        // after fixing site perms / section paths. Only on a REAL MKD though —
        // a CWD "already exists" hit on a fill-only dest must not erase the
        // MKD-denial lesson (the site still refuses dir creation).
        if (!msg.StartsWith("exists", StringComparison.Ordinal))
            ClearBlacklistOnSuccess(dstId);

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
                RecordPermanentMkdDenialIfMatch(dstId, current, codeSub, msgSub);
                throw new IOException($"MKD failed for {current}");
            }
        }
    }

    /// <summary>
    /// If the MKD reply is a PERMANENT denial (dirscript, "not allowed to make
    /// directories", permission denied, path-filter, etc. — see
    /// MkdFailureClassifier), remember this base path for the rest of the job so
    /// FindBestTransfer immediately stops picking dest candidates that route
    /// through it. These denials are deterministic — once the server says NO for
    /// a path, it keeps saying NO — so retrying every file's MKD just spams 550s
    /// (observed 2026-05-21: 24 MKD failures in one race for the same path before
    /// the slow backoff ladder dropped the dest). Previously this only caught
    /// "Denied by dirscript", missing "Not allowed to make directories here",
    /// which is why MasterChef hammered superbnc 24 times.
    /// </summary>
    private void RecordPermanentMkdDenialIfMatch(string dstId, string basePath, string code, string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (!MkdFailureClassifier.IsPermanent(code, msg)) return;

        lock (_ownershipLock)
        {
            if (!_destDirscriptDenied.TryGetValue(dstId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _destDirscriptDenied[dstId] = set;
            }
            set.Add(basePath);
        }
    }

    private void RecordIfPermanent(string dstId, string path, string code, string msg)
    {
        if (_blacklist == null) return;
        if (!MkdFailureClassifier.IsPermanent(code, msg)) return;
        // Dirscript denials are release-scoped (dupe protection on THIS dir name,
        // commonly after our own cleanup wiped it) — the in-race path block +
        // dead-race TTL handle them. Recording them per-SECTION poisoned zephyr
        // out of all tv races on 2026-06-10 (x72).
        if (MkdFailureClassifier.IsReleaseScopedDirscriptDenial(msg))
        {
            Log.Information("Spread: dirscript denied {Path} on {Server} — release-scoped (dupe?), not blacklisting the section",
                path, _serverConfigs.TryGetValue(dstId, out var c) ? c.Name : dstId);
            return;
        }
        var name = _serverConfigs.TryGetValue(dstId, out var cfg) ? cfg.Name : dstId;
        _blacklist.RecordPermanentFailure(dstId, name, Section, path, $"{code} {msg}".Trim());
    }

    /// <summary>
    /// Drop any blacklist entry for (dst, this Section) — called after a
    /// successful MKD so a previously-blocked dest gets fully re-enabled
    /// once the user fixes the site perms or section path.
    /// </summary>
    private void ClearBlacklistOnSuccess(string dstId)
    {
        if (_blacklist == null) return;
        if (!_blacklist.IsBlacklisted(dstId, Section) && !_blacklist.IsExpired(dstId, Section)) return;
        if (_blacklist.Remove(dstId, Section))
        {
            var name = _serverConfigs.TryGetValue(dstId, out var cfg) ? cfg.Name : dstId;
            Log.Information("Section blacklist: cleared {Server}/[{Section}] after successful MKD",
                name, Section);
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        // Unblock any pause wait so the RunAsync loop can observe the
        // cancellation and exit cleanly.
        _pausedFlag = false;
        _pauseSignal.Set();
        if (State == SpreadJobState.Running || State == SpreadJobState.Paused)
            State = SpreadJobState.Stopped;
    }

    /// <summary>
    /// Cooperative pause — sets the paused flag so the main RunAsync loop
    /// waits at its next tick instead of dispatching new transfers.
    /// In-flight transfers are NOT aborted; they finish naturally.
    /// </summary>
    public void Pause()
    {
        if (State == SpreadJobState.Running)
        {
            State = SpreadJobState.Paused;
            _pauseSignal.Reset();
            _pausedFlag = true;
        }
    }

    /// <summary>
    /// Resume from a paused state — clears the paused flag and wakes up
    /// the RunAsync loop waiting on _pauseSignal.
    /// </summary>
    public void Resume()
    {
        if (State == SpreadJobState.Paused)
        {
            State = SpreadJobState.Running;
            _pausedFlag = false;
            _pauseSignal.Set();
        }
    }

    /// <summary>Last failure message (PRD R1/O3). Null if the job didn't fail.</summary>
    public string? LastError { get; private set; }

    private void SetFailed(string message)
    {
        State = SpreadJobState.Failed;
        LastError = message;
        Error?.Invoke(this, message);
        Log.Warning("Spread job failed: {Release} — {Error}", ReleaseName, message);
    }

    /// <summary>
    /// PRD O3 — map a race failure message to a coarse category for metrics/UI.
    /// Pure + static so it's unit-testable without a live job.
    /// </summary>
    public static string ClassifyFailure(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var m = message;
        if (m.Contains("NUKED", StringComparison.OrdinalIgnoreCase)) return "nuked";
        if (m.Contains("not found on any server", StringComparison.OrdinalIgnoreCase)) return "not-found";
        // Site-full is a distinct, useful category — it tells the user the dest
        // needs siteop intervention, not a config / perms fix.
        if (m.Contains("out of disk space", StringComparison.OrdinalIgnoreCase)
            || m.Contains("disk full", StringComparison.OrdinalIgnoreCase)
            || m.Contains("no space", StringComparison.OrdinalIgnoreCase))
            return "site-full";
        if (m.Contains("no upload rights", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Denied by dirscript", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Not allowed to make directories", StringComparison.OrdinalIgnoreCase)
            || m.Contains("path-filter", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "upload-denied";
        if (m.Contains("simultaneous logins", StringComparison.OrdinalIgnoreCase)
            || m.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || m.Contains("BNC cooldown", StringComparison.OrdinalIgnoreCase))
            return "bnc-pressure";
        if (m.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("No connection to the server", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
            return "transport";
        if (m.Contains("Source scan never succeeded", StringComparison.OrdinalIgnoreCase)) return "source-scan-failed";
        if (m.Contains("All destinations are fill-only", StringComparison.OrdinalIgnoreCase)) return "fill-only";
        if (m.Contains("No activity", StringComparison.OrdinalIgnoreCase)) return "no-activity";
        if (m.Contains("Need 2+ servers", StringComparison.OrdinalIgnoreCase)
            || m.Contains("no eligible destination", StringComparison.OrdinalIgnoreCase)
            || m.Contains("No viable destinations", StringComparison.OrdinalIgnoreCase)
            || m.Contains("affil-blocked", StringComparison.OrdinalIgnoreCase))
            return "config";
        return "other";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _pauseSignal.Set();
        _pauseSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Mutable per-site scan signals threaded through ScanDirectoryRecursive.</summary>
    private sealed class ScanSignals
    {
        public bool SawCompletionMarker;
        public bool HasMissingStub;
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
