using System.IO;
using Fsp;
using GlDrive.Config;
using GlDrive.Filesystem;
using GlDrive.Ftp;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public class MountService : IDisposable
{
    private readonly AppConfig _config;
    private readonly CertificateManager _certManager;
    private FtpClientFactory? _factory;
    private FtpConnectionPool? _pool;
    private FtpOperations? _ftp;
    private DirectoryCache? _cache;
    private GlDriveFileSystem? _fileSystem;
    private FileSystemHost? _host;
    private ConnectionMonitor? _monitor;
    private NewReleaseMonitor? _releaseMonitor;
    private bool _mounted;

    public event Action<MountState>? StateChanged;
    public event Action<string, string>? NewReleaseDetected;

    public MountState CurrentState { get; private set; } = MountState.Unmounted;
    public FtpConnectionPool? Pool => _pool;
    public DirectoryCache? Cache => _cache;

    public MountService(AppConfig config, CertificateManager certManager)
    {
        _config = config;
        _certManager = certManager;
    }

    public async Task Mount(CancellationToken ct = default)
    {
        if (_mounted) return;

        // Clean up stale mount from a previous crash
        CleanStaleMounts();

        SetState(MountState.Connecting);

        try
        {
            _factory = new FtpClientFactory(_config, _certManager);
            _pool = new FtpConnectionPool(_factory, _config.Pool.PoolSize);
            await _pool.Initialize(ct);

            _ftp = new FtpOperations(_pool);
            _cache = new DirectoryCache(
                _config.Cache.DirectoryListingTtlSeconds,
                _config.Cache.MaxCachedDirectories);

            _fileSystem = new GlDriveFileSystem(
                _ftp, _cache,
                _config.Connection.RootPath,
                _config.Mount.VolumeLabel);

            _host = new FileSystemHost(_fileSystem);
            _host.Prefix = @"\GlDrive\ftps";

            var mountPoint = _config.Mount.DriveLetter + ":";
            Log.Information("Mounting {MountPoint} (network prefix: {Prefix})...", mountPoint, _host.Prefix);

            var status = _host.Mount(mountPoint, null, false, 0);
            if (status < 0)
            {
                throw new InvalidOperationException(
                    $"WinFsp Mount failed with NTSTATUS 0x{status:X8}");
            }

            _mounted = true;

            // Start connection monitor
            _monitor = new ConnectionMonitor(_pool, _factory, _config.Pool);
            _monitor.ConnectionLost += () => SetState(MountState.Reconnecting);
            _monitor.ConnectionRestored += () => SetState(MountState.Connected);
            _monitor.Start();

            // Start release monitor
            _releaseMonitor = new NewReleaseMonitor(_pool, _config.Notifications, () => CurrentState);
            _releaseMonitor.NewReleaseDetected += (category, release) => NewReleaseDetected?.Invoke(category, release);
            _releaseMonitor.Start();

            SetState(MountState.Connected);
            Log.Information("Drive {MountPoint} mounted successfully", mountPoint);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mount failed");
            SetState(MountState.Error);
            Cleanup();
            throw;
        }
    }

    public void Unmount()
    {
        if (!_mounted) return;

        Log.Information("Unmounting drive...");
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
        Log.Information("Drive unmounted");
    }

    public void RefreshCache()
    {
        _cache?.Clear();
    }

    private void Cleanup()
    {
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
        var mountPoint = _config.Mount.DriveLetter + ":\\";
        try
        {
            if (Directory.Exists(mountPoint))
            {
                Log.Warning("Stale mount detected at {MountPoint}, attempting cleanup...", mountPoint);
                // Try to unmount via a temporary host — WinFsp will clean up the stale mount
                var tempHost = new FileSystemHost(null!);
                try { tempHost.Unmount(); } catch { }
                tempHost.Dispose();

                // If directory still exists, force-remove the drive mapping
                if (Directory.Exists(mountPoint))
                {
                    Log.Warning("Stale mount still present, removing via net use...");
                    var psi = new System.Diagnostics.ProcessStartInfo("net", $"use {_config.Mount.DriveLetter}: /delete /y")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(5000);
                }
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
