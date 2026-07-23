using System.IO;
using Microsoft.Win32;
using Fsp;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Filesystem;
using GlDrive.Ftp;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public class MountService : IDisposable
{
    private readonly ServerConfig _serverConfig;
    private readonly DownloadConfig _downloadConfig;
    private readonly CertificateManager _certManager;
    private FtpClientFactory? _factory;
    private FtpConnectionPool? _pool;
    private FtpOperations? _ftp;
    private DirectoryCache? _cache;
    private GlDriveFileSystem? _fileSystem;
    private FileSystemHost? _host;
    private ConnectionMonitor? _monitor;
    private NewReleaseMonitor? _releaseMonitor;
    private StreamingDownloader? _streamingDownloader;
    private DownloadManager? _downloadManager;
    // Process-wide disk reservation shared across all servers' downloads so
    // concurrent releases can't collectively overrun a drive.
    private static readonly DiskReservation _diskReservation = new();
    private FtpSearchService? _searchService;
    private WishlistMatcher? _wishlistMatcher;
    private bool _mounted;
    private static string? _launchctlPath;
    private static readonly string NetExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe");
    private static readonly string MountvExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mountvol.exe");

    public event Action<MountState>? StateChanged;
    public event Action<string, string, string>? NewReleaseDetected; // category, release, remotePath
    public event Action<string>? BncRateLimitDetected;
    public event Action? StatsChanged;

    /// <summary>Latest SITE STATS scrape (credits + ratio). Null until first refresh.</summary>
    public SiteStats? Stats { get; private set; }

    // SITE STATS probe caching. On ACL-restricted sites (no access to SITE STATS/USER/
    // TRAFFIC) the full candidate chain + LIST trailer fall through every tick — observed
    // ~2,000 "You do not have access" probes/day per site, each burning a login on accounts
    // capped at ~4 logins (directly starving FXP transfers). Once we learn the working
    // command we reuse it; once we confirm NOTHING works we back off for a TTL instead of
    // re-probing every tick.
    private string? _workingStatsCommand;
    private bool _statsViaListTrailer;
    private DateTime _statsUnavailableUntil = DateTime.MinValue;
    private static readonly TimeSpan StatsUnavailableTtl = TimeSpan.FromHours(6);

    public string ServerId => _serverConfig.Id;
    public string ServerName => _serverConfig.Name;
    public string DriveLetter => _serverConfig.Mount.DriveLetter;
    public MountState CurrentState { get; private set; } = MountState.Unmounted;
    public FtpConnectionPool? Pool => _pool;
    public FtpClientFactory? Factory => _factory;
    public FtpOperations? Ftp => _ftp;
    public DirectoryCache? Cache => _cache;
    public DownloadManager? Downloads => _downloadManager;
    public FtpSearchService? Search => _searchService;
    public WishlistMatcher? Matcher => _wishlistMatcher;

    public MountService(ServerConfig serverConfig, DownloadConfig downloadConfig, CertificateManager certManager)
    {
        _serverConfig = serverConfig;
        _downloadConfig = downloadConfig;
        _certManager = certManager;
    }

    public async Task Mount(CancellationToken ct = default)
    {
        if (_mounted) return;

        SetState(MountState.Connecting);

        try
        {
            _factory = new FtpClientFactory(_serverConfig, _certManager);
            // If spread is configured, reduce main pool to leave room for spread connections
            // within the server's login limit (typically 4 simultaneous logins)
            var mainPoolSize = _serverConfig.Pool.PoolSize;
            if (_serverConfig.SpreadSite?.Sections.Count > 0)
                mainPoolSize = Math.Min(mainPoolSize, 2);
            // Resolve the account-wide login gate (shared with the spread + download
            // pools for the same host:port:username) so total live logins across all
            // subsystems never exceed the account cap — the root-cause fix for the
            // self-inflicted 530 / BNC-cooldown storms.
            var conn = _serverConfig.Connection;
            var loginGate = ServerLoginGateRegistry.GetOrCreate(
                conn.Host, conn.Port, conn.Username,
                _serverConfig.Pool.LoginCap, _serverConfig.Pool.LoginHeadroom);
            // Do not give this non-priority pool a higher local ceiling than the
            // gate can ever grant it. Otherwise a second concurrent main-pool
            // borrow tries to create a login, waits for an impossible permit, and
            // emits "Account login cap reached" instead of simply waiting for the
            // existing pooled connection to return.
            mainPoolSize = Math.Min(mainPoolSize,
                Math.Max(1, loginGate.Limit - loginGate.Reserved));
            _pool = new FtpConnectionPool(_factory, mainPoolSize, loginGate);
            await _pool.Initialize(ct);

            // Warm idle main-pool connections ourselves now that FluentFTP's
            // background NoopDaemon is disabled (it raced reads against disposal
            // and crashed the process — see FtpClientFactory). This keepalive is
            // owner-exclusive: it reads each connection out of the channel before
            // NOOPing, so no read ever races a quarantine/dispose. NOOP cadence
            // tracks the configured keepalive interval (default 15s), staying well
            // under the BNC's <30s idle-drop. validateOnBorrow stays off — the
            // filesystem path already gates borrows via IsGnuTlsHealthy and a
            // per-borrow NOOP would add latency to every file operation.
            _pool.ConfigureHealth(
                validateOnBorrow: false,
                keepaliveSeconds: Math.Clamp(_serverConfig.Pool.KeepaliveIntervalSeconds, 10, 120));

            _ftp = new FtpOperations(_pool);
            _cache = new DirectoryCache(
                _serverConfig.Cache.DirectoryListingTtlSeconds,
                _serverConfig.Cache.MaxCachedDirectories);
            // Wire stale-while-revalidate: when a cached listing expires, return stale
            // data immediately and refresh in the background
            _cache.BackgroundRefresh = async remotePath =>
            {
                try
                {
                    var items = await _ftp.ListDirectory(remotePath);
                    _cache.Set(remotePath, items);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Background refresh failed for {Path}", remotePath);
                }
            };

            // Mount WinFsp drive if enabled
            if (_serverConfig.Mount.MountDrive)
            {
                CleanStaleMounts();

                _fileSystem = new GlDriveFileSystem(
                    _ftp, _cache,
                    _serverConfig.Connection.RootPath,
                    _serverConfig.Mount.VolumeLabel,
                    _serverConfig.Cache.FileInfoTimeoutMs,
                    _serverConfig.Cache.DirectoryListTimeoutSeconds,
                    _serverConfig.Cache.ReadBufferSpillThresholdMb);

                _host = new FileSystemHost(_fileSystem);
                _host.Prefix = @$"\GlDrive\{_serverConfig.Id}";

                var mountPoint = _serverConfig.Mount.DriveLetter + ":";
                Log.Information("Mounting {MountPoint} for server {ServerName} (prefix: {Prefix})...",
                    mountPoint, _serverConfig.Name, _host.Prefix);

                var status = _host.Mount(mountPoint, null, false, 0);
                if (status < 0)
                {
                    throw new InvalidOperationException(
                        $"WinFsp Mount failed with NTSTATUS 0x{status:X8}");
                }
            }
            else
            {
                Log.Information("Drive mount disabled for server {ServerName}, connecting without drive letter", _serverConfig.Name);
            }

            _mounted = true;

            // Start connection monitor
            _monitor = new ConnectionMonitor(_pool, _factory, _serverConfig.Pool);
            _monitor.ConnectionLost += () => SetState(MountState.Reconnecting);
            _monitor.ConnectionRestored += () => SetState(MountState.Connected);
            _monitor.BncRateLimitDetected += msg => BncRateLimitDetected?.Invoke(msg);
            _monitor.PeriodicMetricsCallback = () =>
            {
                _cache?.LogMetrics();
                _ = RefreshStatsAsync();
            };
            _monitor.Start();

            // Start release monitor
            _releaseMonitor = new NewReleaseMonitor(_pool, _serverConfig.Notifications, () => CurrentState);
            _releaseMonitor.NewReleaseDetected += (category, release, remotePath) => NewReleaseDetected?.Invoke(category, release, remotePath);
            _releaseMonitor.Start();

            // Initialize download subsystem
            var downloadStore = new DownloadStore(_serverConfig.Id);
            downloadStore.Load();
            var wishlistStore = new WishlistStore();
            wishlistStore.Load();

            var effectiveSpeedLimit = _serverConfig.SpeedLimitKbps > 0
                ? _serverConfig.SpeedLimitKbps
                : _downloadConfig.SpeedLimitKbps;
            // Downloads run on the main pool, capped by the shared account login
            // gate. (A separate download pool was evaluated but rejected: at the
            // conservative login cap its idle connection permanently holds a permit
            // and starves the spread pool's initialization.) The disk reservation +
            // slot-before-extract + cooldown-defer wins below don't need a separate
            // pool.
            _streamingDownloader = new StreamingDownloader(
                _pool, _downloadConfig.StreamingBufferSizeKb, _downloadConfig.WriteBufferLimitMb,
                effectiveSpeedLimit);
            _downloadManager = new DownloadManager(downloadStore, _ftp, _streamingDownloader,
                _downloadConfig, _diskReservation);
            _searchService = new FtpSearchService(_pool, _serverConfig.Search);
            _searchService.StartIndexer();
            _wishlistMatcher = new WishlistMatcher(wishlistStore, _downloadManager, _ftp, _downloadConfig,
                _serverConfig.Id, _serverConfig.Name);

            if (_downloadConfig.AutoDownloadWishlist)
                _releaseMonitor.NewReleaseDetected += (category, release, remotePath) =>
                    _wishlistMatcher.OnNewRelease(category, release, remotePath);

            _downloadManager.Start();

            SetState(MountState.Connected);
            var driveInfo = _serverConfig.Mount.MountDrive ? $"Drive {_serverConfig.Mount.DriveLetter}:" : "No drive";
            Log.Information("{DriveInfo} connected for server {ServerName}", driveInfo, _serverConfig.Name);

            // Kick off an initial stats fetch so the status bar populates fast,
            // rather than waiting ~5 minutes for the first periodic tick.
            // Must run AFTER SetState(Connected) — RefreshStatsAsync's guard would otherwise bail.
            _ = RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mount failed for server {ServerName}", _serverConfig.Name);
            SetState(MountState.Error);
            Cleanup();
            throw;
        }
    }

    public async Task UnmountAsync()
    {
        if (!_mounted) return;

        Log.Information("Unmounting drive for server {ServerName}...", _serverConfig.Name);

        var timeout = TimeSpan.FromSeconds(5);

        // 1. Signal all background tasks to cancel (non-blocking)
        _releaseMonitor?.Stop();
        _monitor?.Stop();
        _downloadManager?.Stop();
        _searchService?.StopIndexer();

        // 2. Wait for background tasks to finish (with timeout)
        var stopTasks = new List<Task>();
        if (_releaseMonitor != null) stopTasks.Add(_releaseMonitor.StopAsync(timeout));
        if (_monitor != null) stopTasks.Add(_monitor.StopAsync(timeout));
        if (_downloadManager != null) stopTasks.Add(_downloadManager.StopAsync(timeout));
        if (_searchService != null) stopTasks.Add(_searchService.StopIndexerAsync(timeout));

        try
        {
            await Task.WhenAll(stopTasks).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            Log.Warning("Background tasks did not stop in time for {ServerName} — proceeding with cleanup", _serverConfig.Name);
        }
        // Best-effort: a stopping task may fault; never let it block unmount.
        catch (Exception ex) { Log.Debug(ex, "Background task stop faulted during unmount for {ServerName}", _serverConfig.Name); }

        // 3. Unmount WinFsp host
        try
        {
            _host?.Unmount();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unmount error");
        }

        // 4. Dispose resources with timeout
        await CleanupAsync();
        _mounted = false;
        SetState(MountState.Unmounted);
        Log.Information("Drive unmounted for server {ServerName}", _serverConfig.Name);
    }

    public void Unmount()
    {
        if (!_mounted) return;
        // Sync fallback for Dispose() — just signal cancellation and tear down fast
        Log.Information("Unmounting drive (sync) for server {ServerName}...", _serverConfig.Name);
        _releaseMonitor?.Stop();
        _monitor?.Stop();
        _downloadManager?.Stop();
        _searchService?.StopIndexer();

        // Best-effort WinFsp unmount on the sync teardown path; a dead host shouldn't block Dispose.
        try { _host?.Unmount(); } catch (Exception ex) { Log.Debug(ex, "Sync unmount error for {ServerName}", _serverConfig.Name); }

        Cleanup();
        _mounted = false;
        SetState(MountState.Unmounted);
    }

    public void RefreshCache()
    {
        _cache?.Clear();
    }

    private async Task CleanupAsync()
    {
        _downloadManager?.Dispose();
        _downloadManager = null;
        _wishlistMatcher = null;
        _searchService?.Dispose();
        _searchService = null;
        _streamingDownloader = null;
        _releaseMonitor = null;
        _monitor = null;
        _host?.Dispose();
        _host = null;
        _fileSystem = null;
        _cache = null;
        _ftp = null;

        if (_pool != null)
        {
            try
            {
                await _pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warning("Pool dispose timed out — abandoning connections");
            }
            // Best-effort: a non-timeout dispose fault (dead socket, GnuTLS) must not block unmount.
            catch (Exception ex) { Log.Debug(ex, "Pool dispose error during unmount for {ServerName}", _serverConfig.Name); }
            _pool = null;
        }

        _factory = null;
    }

    private void Cleanup()
    {
        _downloadManager?.Dispose();
        _downloadManager = null;
        _wishlistMatcher = null;
        _searchService?.Dispose();
        _searchService = null;
        _streamingDownloader = null;
        _releaseMonitor = null;
        _monitor = null;
        _host?.Dispose();
        _host = null;
        _fileSystem = null;
        _cache = null;
        _ftp = null;

        // Fire-and-forget pool dispose with timeout — don't block on dead TCP connections.
        // The inner try/catch logs expected faults; the OnlyOnFaulted continuation is a
        // backstop so no dispose fault escapes as an unobserved TaskException (which can
        // crash the process on finalization) — matches the pool-lifecycle logging style.
        var pool = _pool;
        _pool = null;
        if (pool != null)
            _ = Task.Run(async () =>
            {
                try
                {
                    await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    Log.Warning("Pool dispose timed out (sync cleanup) — abandoning connections");
                }
                catch (Exception ex) { Log.Debug(ex, "Pool dispose error"); }
            }).ContinueWith(
                t => Log.Debug(t.Exception, "Pool dispose task faulted (sync cleanup)"),
                TaskContinuationOptions.OnlyOnFaulted);

        _factory = null;
    }

    private void CleanStaleMounts()
    {
        var driveLetter = _serverConfig.Mount.DriveLetter;
        var mountPoint = driveLetter + ":\\";
        try
        {
            if (Directory.Exists(mountPoint))
            {
                Log.Warning("Stale mount detected at {MountPoint}, attempting cleanup...", mountPoint);

                // Try WinFsp launchctl stop first (most reliable for WinFsp mounts)
                try
                {
                    var launchctl = ResolveLaunchctlPath();
                    if (launchctl != null)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(
                            launchctl, $"stop GlDrive\\{_serverConfig.Id}")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit(3000);
                    }
                }
                // Best-effort: launchctl stop is one of three fallback cleanup attempts; failure
                // is expected (no WinFsp launchctl, not our mount) and we fall through to net use.
                catch (Exception ex) { Log.Debug(ex, "WinFsp launchctl stop failed for {MountPoint}", mountPoint); }

                // Try net use delete
                if (Directory.Exists(mountPoint))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(NetExePath, $"use {driveLetter}: /delete /y")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(3000);
                }

                // Last resort: use mountvol to remove the mount point
                if (Directory.Exists(mountPoint))
                {
                    Log.Warning("Stale mount still present after net use, trying mountvol...");
                    var psi = new System.Diagnostics.ProcessStartInfo(MountvExePath, $"{driveLetter}: /P")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(3000);
                }

                if (!Directory.Exists(mountPoint))
                    Log.Information("Stale mount at {MountPoint} cleaned up successfully", mountPoint);
                else
                    Log.Warning("Stale mount at {MountPoint} could not be fully cleaned — mounting may fail", mountPoint);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stale mount cleanup failed — continuing anyway");
        }
    }

    private static string? ResolveLaunchctlPath()
    {
        if (_launchctlPath != null) return _launchctlPath;
        var regDir = Registry.LocalMachine.OpenSubKey(@"Software\WinFsp")?.GetValue("InstallDir") as string;
        var candidate = regDir != null
            ? Path.Combine(regDir, "bin", "launchctl-x64.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", "launchctl-x64.exe");
        if (!File.Exists(candidate))
        {
            Log.Warning("launchctl-x64.exe not found at {Path}", candidate);
            return null;
        }
        _launchctlPath = candidate;
        return _launchctlPath;
    }

    private void SetState(MountState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }

    public async Task RefreshStatsAsync()
    {
        if (_pool == null || CurrentState != MountState.Connected)
        {
            Log.Information("RefreshStatsAsync skipped for {Server}: pool={HasPool} state={State}",
                _serverConfig.Name, _pool != null, CurrentState);
            return;
        }

        // Negative cache: if we already learned this site grants NO stats access, don't
        // re-probe the whole candidate chain (+ LIST trailer) every tick — that burns a
        // login per command on accounts capped at ~4 logins. Re-probe only after the TTL.
        if (_workingStatsCommand == null && DateTime.UtcNow < _statsUnavailableUntil)
            return;

        // Try the configured command first, then fall back through common glftpd variants
        // until one yields parsed credits or ratio. SITE USER <self> is the only command
        // that reliably exposes per-user credits/ratio on glftpd; the others are site totals.
        var user = _serverConfig.Connection.Username;
        var candidates = new List<string>();
        // Positive cache: once a command worked, reuse it first (and let the rest follow
        // as fallback in case the site changed).
        if (_workingStatsCommand != null) candidates.Add(_workingStatsCommand);
        if (!candidates.Contains(_serverConfig.SiteStatsCommand, StringComparer.OrdinalIgnoreCase))
            candidates.Add(_serverConfig.SiteStatsCommand);
        var fallbacks = new List<string>();
        if (!string.IsNullOrWhiteSpace(user))
        {
            fallbacks.Add($"SITE USER {user}");
            fallbacks.Add($"SITE STATS {user}");
        }
        fallbacks.AddRange(new[] { "SITE STATS", "SITE TRAFFIC", "SITE USER" });
        foreach (var fallback in fallbacks)
        {
            if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fallback);
        }

        try
        {
            using var statsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var conn = await _pool.Borrow(statsCts.Token);
            SiteStats? best = null;
            bool connDied = false;
            // Trailer-only sites (e.g. SYN): every SITE candidate is ACL-denied but the
            // 226 LIST trailer carries credits. Trailer success is neither a "clean
            // no-access" (negative cache) nor a working SITE command (positive cache),
            // so without this flag the 5-command dead chain re-ran every 5-min tick
            // (~1,170 wasted SITE probes/day observed 2026-07-01). Go straight to the
            // trailer; if it ever stops yielding, re-probe the chain next tick.
            if (!_statsViaListTrailer)
            foreach (var cmd in candidates)
            {
                // If the BNC silently dropped this control connection between candidates,
                // FluentFTP's response parser throws ArgumentOutOfRangeException
                // ("sourceIndex (-10) must be >= 0") on every subsequent command. Bail out
                // immediately — the next periodic tick will borrow a fresh connection.
                if (!conn.Client.IsConnected)
                {
                    Log.Information("RefreshStatsAsync: connection dropped before '{Cmd}' for {Server}; aborting candidate loop",
                        cmd, _serverConfig.Name);
                    connDied = true;
                    break;
                }
                Log.Information("RefreshStatsAsync running for {Server} via '{Cmd}'", _serverConfig.Name, cmd);
                try
                {
                    var stats = await SiteStatsCollector.RefreshAsync(
                        conn.Client, cmd, statsCts.Token);
                    if (stats.Credits != null || stats.Ratio != null)
                    {
                        best = stats;
                        _workingStatsCommand = cmd; // positive cache: reuse next tick
                        break;
                    }
                    best ??= stats; // remember first attempt for null fallback
                }
                catch (Exception cmdEx)
                {
                    Log.Information("RefreshStatsAsync candidate '{Cmd}' threw for {Server}: {Msg}",
                        cmd, _serverConfig.Name, cmdEx.Message);
                    // If the connection died mid-command, every remaining candidate will throw
                    // the same parser error. Poison the conn so the pool discards it on return,
                    // then break out — don't keep hammering a broken socket.
                    if (!conn.Client.IsConnected)
                    {
                        conn.Poisoned = true;
                        connDied = true;
                        break;
                    }
                }
            }

            // Last resort: many BNCs (SuperBNC, zSBNC variants) don't expose credits via any
            // SITE command — they append them to the 226 trailer of every directory listing.
            // Trigger a small LIST and parse client.LastReply, which holds that trailer.
            // Skip when the connection already died — LIST will just throw the same parser error.
            if (!connDied && (best == null || (best.Credits == null && best.Ratio == null)))
            {
                Log.Information("RefreshStatsAsync falling back to LIST trailer for {Server}", _serverConfig.Name);
                try
                {
                    // Honor the 10s statsCts deadline so a slow/hung LIST can't pin this
                    // borrowed connection (and the unmount/stats path) indefinitely.
                    await conn.Client.GetListing(_serverConfig.Connection.RootPath, statsCts.Token);
                    var reply = conn.Client.LastReply;
                    var body = (reply.InfoMessages ?? string.Empty) + "\n" + (reply.Message ?? string.Empty);
                    Log.Information("LIST trailer for {Server} bodyLen={Len} body={Body}",
                        _serverConfig.Name, body.Length, body.Length > 600 ? body[..600] + "...(truncated)" : body);
                    var trailer = SiteStatsCollector.Parse(body);
                    if (trailer.Credits != null || trailer.Ratio != null)
                    {
                        best = trailer;
                        _statsViaListTrailer = true; // positive cache: skip the dead SITE chain next tick
                    }
                }
                catch (Exception listEx)
                {
                    Log.Information("LIST trailer fallback failed for {Server}: {Msg}",
                        _serverConfig.Name, listEx.Message);
                    if (!conn.Client.IsConnected) conn.Poisoned = true;
                }
            }

            // Negative cache: a CLEAN no-access result (every candidate fell through and
            // the LIST trailer yielded nothing, with the connection still alive) means this
            // site simply doesn't expose stats to us. Back off for the TTL so we stop
            // re-probing it every tick. A transient drop (connDied) is NOT cached — the next
            // tick borrows a fresh connection and tries again.
            bool gotStats = best?.Credits != null || best?.Ratio != null;
            if (!gotStats && !connDied)
            {
                if (_statsViaListTrailer)
                {
                    // The cached trailer path stopped yielding — the SITE chain was
                    // skipped this tick, so re-probe it next tick before concluding
                    // the site grants no stats access at all.
                    _statsViaListTrailer = false;
                }
                else
                {
                    _workingStatsCommand = null;
                    _statsUnavailableUntil = DateTime.UtcNow + StatsUnavailableTtl;
                    Log.Information("SITE STATS unavailable for {Server} (no command grants access) — " +
                        "backing off probes for {Hours}h", _serverConfig.Name, StatsUnavailableTtl.TotalHours);
                }
            }

            Stats = best;
            Log.Information("SITE STATS result for {Server}: credits={Credits} ratio={Ratio}",
                _serverConfig.Name, best?.Credits ?? "(null)", best?.Ratio ?? "(null)");
            StatsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RefreshStatsAsync failed for {Server}", _serverConfig.Name);
        }
    }

    public void Dispose()
    {
        Unmount();
        GC.SuppressFinalize(this);
    }
}

public enum MountState
{
    Unmounted,
    Connecting,
    Connected,
    Reconnecting,
    Error
}
