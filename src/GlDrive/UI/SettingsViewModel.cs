using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Tls;

namespace GlDrive.UI;

public class SettingsViewModel : INotifyPropertyChanged
{
    private string _host;
    private string _port;
    private string _username;
    private string _rootPath;
    private string _driveLetter;
    private string _volumeLabel;
    private bool _autoMountOnStart;
    private bool _preferTls12;
    private string _cacheTtl;
    private string _poolSize;
    private string _logLevel;
    private string _trustedCertsInfo;
    private string _downloadLocalPath;
    private string _maxConcurrentDownloads;
    private string _streamingBufferSize;
    private string _writeBufferLimit;
    private string _keepaliveInterval;
    private string _maxCachedDirs;
    private string _dirListTimeout;
    private string _fileInfoTimeout;
    private string _qualityDefault;
    private string _omdbApiKey;
    private bool _autoDownloadWishlist;

    public SettingsViewModel(AppConfig config)
    {
        _host = config.Connection.Host;
        _port = config.Connection.Port.ToString();
        _username = config.Connection.Username;
        _rootPath = config.Connection.RootPath;
        _driveLetter = config.Mount.DriveLetter;
        _volumeLabel = config.Mount.VolumeLabel;
        _autoMountOnStart = config.Mount.AutoMountOnStart;
        _preferTls12 = config.Tls.PreferTls12;
        _cacheTtl = config.Cache.DirectoryListingTtlSeconds.ToString();
        _poolSize = config.Pool.PoolSize.ToString();
        _logLevel = config.Logging.Level;
        _trustedCertsInfo = GetCertsInfo(config);
        _downloadLocalPath = config.Downloads.LocalPath;
        _maxConcurrentDownloads = config.Downloads.MaxConcurrentDownloads.ToString();
        _streamingBufferSize = config.Downloads.StreamingBufferSizeKb.ToString();
        _writeBufferLimit = config.Downloads.WriteBufferLimitMb.ToString();
        _keepaliveInterval = config.Pool.KeepaliveIntervalSeconds.ToString();
        _maxCachedDirs = config.Cache.MaxCachedDirectories.ToString();
        _dirListTimeout = config.Cache.DirectoryListTimeoutSeconds.ToString();
        _fileInfoTimeout = config.Cache.FileInfoTimeoutMs.ToString();
        _qualityDefault = config.Downloads.QualityDefault;
        _omdbApiKey = config.Downloads.OmdbApiKey;
        _autoDownloadWishlist = config.Downloads.AutoDownloadWishlist;
    }

    public string Host { get => _host; set { _host = value; OnPropertyChanged(); } }
    public string Port { get => _port; set { _port = value; OnPropertyChanged(); } }
    public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
    public string RootPath { get => _rootPath; set { _rootPath = value; OnPropertyChanged(); } }
    public string DriveLetter { get => _driveLetter; set { _driveLetter = value; OnPropertyChanged(); } }
    public string VolumeLabel { get => _volumeLabel; set { _volumeLabel = value; OnPropertyChanged(); } }
    public bool AutoMountOnStart { get => _autoMountOnStart; set { _autoMountOnStart = value; OnPropertyChanged(); } }
    public bool PreferTls12 { get => _preferTls12; set { _preferTls12 = value; OnPropertyChanged(); } }
    public string CacheTtl { get => _cacheTtl; set { _cacheTtl = value; OnPropertyChanged(); } }
    public string PoolSize { get => _poolSize; set { _poolSize = value; OnPropertyChanged(); } }
    public string LogLevel { get => _logLevel; set { _logLevel = value; OnPropertyChanged(); } }
    public string TrustedCertsInfo { get => _trustedCertsInfo; set { _trustedCertsInfo = value; OnPropertyChanged(); } }
    public string DownloadLocalPath { get => _downloadLocalPath; set { _downloadLocalPath = value; OnPropertyChanged(); } }
    public string MaxConcurrentDownloads { get => _maxConcurrentDownloads; set { _maxConcurrentDownloads = value; OnPropertyChanged(); } }
    public string StreamingBufferSize { get => _streamingBufferSize; set { _streamingBufferSize = value; OnPropertyChanged(); } }
    public string WriteBufferLimit { get => _writeBufferLimit; set { _writeBufferLimit = value; OnPropertyChanged(); } }
    public string KeepaliveInterval { get => _keepaliveInterval; set { _keepaliveInterval = value; OnPropertyChanged(); } }
    public string MaxCachedDirs { get => _maxCachedDirs; set { _maxCachedDirs = value; OnPropertyChanged(); } }
    public string DirListTimeout { get => _dirListTimeout; set { _dirListTimeout = value; OnPropertyChanged(); } }
    public string FileInfoTimeout { get => _fileInfoTimeout; set { _fileInfoTimeout = value; OnPropertyChanged(); } }
    public string QualityDefault { get => _qualityDefault; set { _qualityDefault = value; OnPropertyChanged(); } }
    public string OmdbApiKey { get => _omdbApiKey; set { _omdbApiKey = value; OnPropertyChanged(); } }
    public bool AutoDownloadWishlist { get => _autoDownloadWishlist; set { _autoDownloadWishlist = value; OnPropertyChanged(); } }

    public string[] AvailableDriveLetters { get; } = GetAvailableDriveLetters();
    public string[] LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error"];
    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];

    public void ApplyTo(AppConfig config)
    {
        config.Connection.Host = Host;
        config.Connection.Port = int.TryParse(Port, out var p) ? p : 21;
        config.Connection.Username = Username;
        config.Connection.RootPath = RootPath;
        config.Mount.DriveLetter = DriveLetter;
        config.Mount.VolumeLabel = VolumeLabel;
        config.Mount.AutoMountOnStart = AutoMountOnStart;
        config.Tls.PreferTls12 = PreferTls12;
        config.Cache.DirectoryListingTtlSeconds = int.TryParse(CacheTtl, out var ttl) ? Math.Clamp(ttl, 5, 300) : 30;
        config.Cache.MaxCachedDirectories = int.TryParse(MaxCachedDirs, out var mcd2) ? Math.Clamp(mcd2, 50, 5000) : 500;
        config.Cache.DirectoryListTimeoutSeconds = int.TryParse(DirListTimeout, out var dlt) ? Math.Clamp(dlt, 10, 120) : 30;
        config.Cache.FileInfoTimeoutMs = int.TryParse(FileInfoTimeout, out var fit) ? Math.Clamp(fit, 200, 30000) : 1000;
        config.Pool.PoolSize = int.TryParse(PoolSize, out var ps) ? Math.Clamp(ps, 1, 10) : 3;
        config.Pool.KeepaliveIntervalSeconds = int.TryParse(KeepaliveInterval, out var kai) ? Math.Clamp(kai, 10, 300) : 30;
        config.Logging.Level = LogLevel;
        config.Downloads.LocalPath = DownloadLocalPath;
        config.Downloads.MaxConcurrentDownloads = int.TryParse(MaxConcurrentDownloads, out var mcd) ? Math.Clamp(mcd, 1, 5) : 1;
        config.Downloads.StreamingBufferSizeKb = int.TryParse(StreamingBufferSize, out var sbs) ? Math.Clamp(sbs, 64, 4096) : 256;
        config.Downloads.WriteBufferLimitMb = int.TryParse(WriteBufferLimit, out var wbl) ? Math.Clamp(wbl, 0, 512) : 0;
        config.Downloads.QualityDefault = QualityDefault;
        config.Downloads.OmdbApiKey = OmdbApiKey;
        config.Downloads.AutoDownloadWishlist = AutoDownloadWishlist;
    }

    public void RefreshCertsInfo()
    {
        var certMgr = new CertificateManager();
        var certs = certMgr.GetTrustedCertificates();
        TrustedCertsInfo = certs.Count == 0
            ? "No trusted certificates."
            : $"{certs.Count} trusted certificate(s).";
    }

    private static string GetCertsInfo(AppConfig config)
    {
        try
        {
            var certMgr = new CertificateManager(config.Tls.CertificateFingerprintFile);
            var certs = certMgr.GetTrustedCertificates();
            return certs.Count == 0
                ? "No trusted certificates."
                : $"{certs.Count} trusted certificate(s).";
        }
        catch
        {
            return "Unable to load certificate info.";
        }
    }

    private static string[] GetAvailableDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        return Enumerable.Range('D', 23)
            .Select(c => ((char)c).ToString())
            .Where(c => !used.Contains(c[0]))
            .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
