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
    private string _tmdbApiKey;
    private bool _autoDownloadWishlist;
    private bool _autoExtract;
    private bool _deleteArchivesAfterExtract;
    private string _speedLimitKbps;
    private bool _skipIncompleteReleases;

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
        _tmdbApiKey = config.Downloads.TmdbApiKey;
        _autoDownloadWishlist = config.Downloads.AutoDownloadWishlist;
        _autoExtract = config.Downloads.AutoExtract;
        _deleteArchivesAfterExtract = config.Downloads.DeleteArchivesAfterExtract;
        _speedLimitKbps = config.Downloads.SpeedLimitKbps.ToString();
        _skipIncompleteReleases = config.Downloads.SkipIncompleteReleases;
    }

    public string LogLevel { get => _logLevel; set { _logLevel = value; OnPropertyChanged(); } }
    public string TrustedCertsInfo { get => _trustedCertsInfo; set { _trustedCertsInfo = value; OnPropertyChanged(); } }
    public string DownloadLocalPath { get => _downloadLocalPath; set { _downloadLocalPath = value; OnPropertyChanged(); } }
    public string MaxConcurrentDownloads { get => _maxConcurrentDownloads; set { _maxConcurrentDownloads = value; OnPropertyChanged(); } }
    public string StreamingBufferSize { get => _streamingBufferSize; set { _streamingBufferSize = value; OnPropertyChanged(); } }
    public string WriteBufferLimit { get => _writeBufferLimit; set { _writeBufferLimit = value; OnPropertyChanged(); } }
    public string QualityDefault { get => _qualityDefault; set { _qualityDefault = value; OnPropertyChanged(); } }
    public string OmdbApiKey { get => _omdbApiKey; set { _omdbApiKey = value; OnPropertyChanged(); } }
    public string TmdbApiKey { get => _tmdbApiKey; set { _tmdbApiKey = value; OnPropertyChanged(); } }
    public bool AutoDownloadWishlist { get => _autoDownloadWishlist; set { _autoDownloadWishlist = value; OnPropertyChanged(); } }
    public bool AutoExtract { get => _autoExtract; set { _autoExtract = value; OnPropertyChanged(); } }
    public bool DeleteArchivesAfterExtract { get => _deleteArchivesAfterExtract; set { _deleteArchivesAfterExtract = value; OnPropertyChanged(); } }
    public string SpeedLimitKbps { get => _speedLimitKbps; set { _speedLimitKbps = value; OnPropertyChanged(); } }
    public bool SkipIncompleteReleases { get => _skipIncompleteReleases; set { _skipIncompleteReleases = value; OnPropertyChanged(); } }

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
        config.Downloads.TmdbApiKey = TmdbApiKey;
        config.Downloads.AutoDownloadWishlist = AutoDownloadWishlist;
        config.Downloads.AutoExtract = AutoExtract;
        config.Downloads.DeleteArchivesAfterExtract = DeleteArchivesAfterExtract;
        config.Downloads.SpeedLimitKbps = int.TryParse(SpeedLimitKbps, out var slk) ? Math.Max(slk, 0) : 0;
        config.Downloads.SkipIncompleteReleases = SkipIncompleteReleases;
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
