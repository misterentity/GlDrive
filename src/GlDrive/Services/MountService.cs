using System.IO;
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
    private FtpSearchService? _searchService;
    private WishlistMatcher? _wishlistMatcher;
    private bool _mounted;

    public event Action<MountState>? StateChanged;
    public event Action<string, string, string>? NewReleaseDetected; // category, release, remotePath
    public event Action<string>? BncRateLimitDetected;
    public event Action? StatsChanged;

    /// <summary>Latest SITE STATS scrape (credits + ratio). Null until first refresh.</summary>
    public SiteStats? Stats { get; private set; }

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
            _pool = new FtpConnectionPool(_factory, mainPoolSize);
            await _pool.Initialize(ct);

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
            _streamingDownloader = new StreamingDownloader(
                _pool, _downloadConfig.StreamingBufferSizeKb, _downloadConfig.WriteBufferLimitMb,
                effectiveSpeedLimit);
            _downloadManager = new DownloadManager(downloadStore, _ftp, _streamingDownloader, _downloadConfig);
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
        catch { }

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

        try { _host?.Unmount(); } catch { }

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
            catch { }
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

        // Fire-and-forget pool dispose with timeout — don't block on dead TCP connections
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
            });

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
                    var psi = new System.Diagnostics.ProcessStartInfo(
                        "launchctl-x64.exe", $"stop GlDrive\\{_serverConfig.Id}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(3000);
                }
                catch { }

                // Try net use delete
                if (Directory.Exists(mountPoint))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("net", $"use {driveLetter}: /delete /y")
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
                    var psi = new System.Diagnostics.ProcessStartInfo("mountvol", $"{driveLetter}: /P")
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

        // Try the configured command first, then fall back through common glftpd variants
        // until one yields parsed credits or ratio. SITE USER <self> is the only command
        // that reliably exposes per-user credits/ratio on glftpd; the others are site totals.
        var user = _serverConfig.Connection.Username;
        var candidates = new List<string> { _serverConfig.SiteStatsCommand };
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
            await using var conn = await _pool.Borrow(CancellationToken.None);
            SiteStats? best = null;
            foreach (var cmd in candidates)
            {
                Log.Information("RefreshStatsAsync running for {Server} via '{Cmd}'", _serverConfig.Name, cmd);
                try
                {
                    var stats = await SiteStatsCollector.RefreshAsync(
                        conn.Client, cmd, CancellationToken.None);
                    if (stats.Credits != null || stats.Ratio != null)
                    {
                        best = stats;
                        break;
                    }
                    best ??= stats; // remember first attempt for null fallback
                }
                catch (Exception cmdEx)
                {
                    Log.Information("RefreshStatsAsync candidate '{Cmd}' threw for {Server}: {Msg}",
                        cmd, _serverConfig.Name, cmdEx.Message);
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
