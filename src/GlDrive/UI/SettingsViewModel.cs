using System.ComponentModel;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Tls;

namespace GlDrive.UI;

public class SettingsViewModel : INotifyPropertyChanged
{
    private string _logLevel;
    private string _trustedCertsInfo;
    private string _downloadLocalPath;
    private string _maxConcurrentDownloads;
    private string _streamingBufferSize;
    private string _writeBufferLimit;
    private string _qualityDefault;
    private string _omdbApiKey;
    private bool _autoDownloadWishlist;

    public SettingsViewModel(AppConfig config)
    {
        _logLevel = config.Logging.Level;
        _trustedCertsInfo = GetCertsInfo();
        _downloadLocalPath = config.Downloads.LocalPath;
        _maxConcurrentDownloads = config.Downloads.MaxConcurrentDownloads.ToString();
        _streamingBufferSize = config.Downloads.StreamingBufferSizeKb.ToString();
        _writeBufferLimit = config.Downloads.WriteBufferLimitMb.ToString();
        _qualityDefault = config.Downloads.QualityDefault;
        _omdbApiKey = config.Downloads.OmdbApiKey;
        _autoDownloadWishlist = config.Downloads.AutoDownloadWishlist;
    }

    public string LogLevel { get => _logLevel; set { _logLevel = value; OnPropertyChanged(); } }
    public string TrustedCertsInfo { get => _trustedCertsInfo; set { _trustedCertsInfo = value; OnPropertyChanged(); } }
    public string DownloadLocalPath { get => _downloadLocalPath; set { _downloadLocalPath = value; OnPropertyChanged(); } }
    public string MaxConcurrentDownloads { get => _maxConcurrentDownloads; set { _maxConcurrentDownloads = value; OnPropertyChanged(); } }
    public string StreamingBufferSize { get => _streamingBufferSize; set { _streamingBufferSize = value; OnPropertyChanged(); } }
    public string WriteBufferLimit { get => _writeBufferLimit; set { _writeBufferLimit = value; OnPropertyChanged(); } }
    public string QualityDefault { get => _qualityDefault; set { _qualityDefault = value; OnPropertyChanged(); } }
    public string OmdbApiKey { get => _omdbApiKey; set { _omdbApiKey = value; OnPropertyChanged(); } }
    public bool AutoDownloadWishlist { get => _autoDownloadWishlist; set { _autoDownloadWishlist = value; OnPropertyChanged(); } }

    public string[] LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error"];
    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];

    public void ApplyTo(AppConfig config)
    {
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

    private static string GetCertsInfo()
    {
        try
        {
            var certMgr = new CertificateManager();
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
