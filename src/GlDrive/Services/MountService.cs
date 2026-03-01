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
    public event Action<string, string>? NewReleaseDetected;

    public string ServerId => _serverConfig.Id;
    public string ServerName => _serverConfig.Name;
    public string DriveLetter => _serverConfig.Mount.DriveLetter;
    public MountState CurrentState { get; private set; } = MountState.Unmounted;
    public FtpConnectionPool? Pool => _pool;
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
            _pool = new FtpConnectionPool(_factory, _serverConfig.Pool.PoolSize);
            await _pool.Initialize(ct);

            _ftp = new FtpOperations(_pool);
            _cache = new DirectoryCache(
                _serverConfig.Cache.DirectoryListingTtlSeconds,
                _serverConfig.Cache.MaxCachedDirectories);

            // Mount WinFsp drive if enabled
            if (_serverConfig.Mount.MountDrive)
            {
                CleanStaleMounts();

                _fileSystem = new GlDriveFileSystem(
                    _ftp, _cache,
                    _serverConfig.Connection.RootPath,
                    _serverConfig.Mount.VolumeLabel,
                    _serverConfig.Cache.FileInfoTimeoutMs,
                    _serverConfig.Cache.DirectoryListTimeoutSeconds);

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
            _monitor.Start();

            // Start release monitor
            _releaseMonitor = new NewReleaseMonitor(_pool, _serverConfig.Notifications, () => CurrentState);
            _releaseMonitor.NewReleaseDetected += (category, release) => NewReleaseDetected?.Invoke(category, release);
            _releaseMonitor.Start();

            // Initialize download subsystem
            var downloadStore = new DownloadStore(_serverConfig.Id);
            downloadStore.Load();
            var wishlistStore = new WishlistStore();
            wishlistStore.Load();

            _streamingDownloader = new StreamingDownloader(
                _pool, _downloadConfig.StreamingBufferSizeKb, _downloadConfig.WriteBufferLimitMb);
            _downloadManager = new DownloadManager(downloadStore, _ftp, _streamingDownloader, _downloadConfig);
            _searchService = new FtpSearchService(_pool, _serverConfig.Notifications);
            _wishlistMatcher = new WishlistMatcher(wishlistStore, _downloadManager, _ftp, _downloadConfig,
                _serverConfig.Id, _serverConfig.Name);

            if (_downloadConfig.AutoDownloadWishlist)
                _releaseMonitor.NewReleaseDetected += _wishlistMatcher.OnNewRelease;

            _downloadManager.Start();

            SetState(MountState.Connected);
            var driveInfo = _serverConfig.Mount.MountDrive ? $"Drive {_serverConfig.Mount.DriveLetter}:" : "No drive";
            Log.Information("{DriveInfo} connected for server {ServerName}", driveInfo, _serverConfig.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mount failed for server {ServerName}", _serverConfig.Name);
            SetState(MountState.Error);
            Cleanup();
            throw;
        }
    }

    public void Unmount()
    {
        if (!_mounted) return;

        Log.Information("Unmounting drive for server {ServerName}...", _serverConfig.Name);
        _releaseMonitor?.Stop();
        _monitor?.Stop();

        try
        {
            _host?.Unmount();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unmount error");
        }

        Cleanup();
        _mounted = false;
        SetState(MountState.Unmounted);
        Log.Information("Drive unmounted for server {ServerName}", _serverConfig.Name);
    }

    public void RefreshCache()
    {
        _cache?.Clear();
    }

    private void Cleanup()
    {
        _downloadManager?.Dispose();
        _downloadManager = null;
        _wishlistMatcher = null;
        _searchService = null;
        _streamingDownloader = null;
        _releaseMonitor?.Stop();
        _releaseMonitor = null;
        _monitor?.Stop();
        _host?.Dispose();
        _host = null;
        _fileSystem = null;
        _cache = null;
        _ftp = null;
        _pool?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pool = null;
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
