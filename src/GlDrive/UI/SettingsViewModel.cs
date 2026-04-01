using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Tls;
using static GlDrive.Config.CredentialStore;

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
    private string _openRouterApiKey;
    private string _openRouterModel;
    private bool _autoDownloadWishlist;
    private bool _autoExtract;
    private bool _deleteArchivesAfterExtract;
    private string _speedLimitKbps;
    private bool _skipIncompleteReleases;
    private string _maxRetries;
    private string _retryDelaySeconds;
    private bool _scheduleEnabled;
    private string _scheduleStartHour;
    private string _scheduleEndHour;
    private bool _verifySfv;
    private bool _playSoundOnComplete;
    private string _theme;
    private string _spreadPoolSize;
    private string _spreadTransferTimeout;
    private string _spreadHardTimeout;
    private string _spreadMaxConcurrent;
    private bool _spreadAutoRace;
    private bool _spreadNotifyComplete;

    public SettingsViewModel(AppConfig config)
    {
        _logLevel = config.Logging.Level;
        _trustedCertsInfo = GetCertsInfo();
        _downloadLocalPath = config.Downloads.LocalPath;
        _maxConcurrentDownloads = config.Downloads.MaxConcurrentDownloads.ToString();
        _streamingBufferSize = config.Downloads.StreamingBufferSizeKb.ToString();
        _writeBufferLimit = config.Downloads.WriteBufferLimitMb.ToString();
        _qualityDefault = config.Downloads.QualityDefault;
        // Prefer Credential Manager, fall back to config (migration)
        _omdbApiKey = CredentialStore.GetApiKey("omdb") ?? config.Downloads.OmdbApiKey;
        _tmdbApiKey = CredentialStore.GetApiKey("tmdb") ?? config.Downloads.TmdbApiKey;
        _openRouterApiKey = CredentialStore.GetApiKey("openrouter") ?? "";
        _openRouterModel = config.Downloads.OpenRouterModel;
        _autoDownloadWishlist = config.Downloads.AutoDownloadWishlist;
        _autoExtract = config.Downloads.AutoExtract;
        _deleteArchivesAfterExtract = config.Downloads.DeleteArchivesAfterExtract;
        _speedLimitKbps = config.Downloads.SpeedLimitKbps.ToString();
        _skipIncompleteReleases = config.Downloads.SkipIncompleteReleases;
        _maxRetries = config.Downloads.MaxRetries.ToString();
        _retryDelaySeconds = config.Downloads.RetryDelaySeconds.ToString();
        _scheduleEnabled = config.Downloads.ScheduleEnabled;
        _scheduleStartHour = config.Downloads.ScheduleStartHour.ToString();
        _scheduleEndHour = config.Downloads.ScheduleEndHour.ToString();
        _verifySfv = config.Downloads.VerifySfv;
        _playSoundOnComplete = config.Downloads.PlaySoundOnComplete;
        _theme = config.Downloads.Theme;
        _spreadPoolSize = config.Spread.SpreadPoolSize.ToString();
        _spreadTransferTimeout = config.Spread.TransferTimeoutSeconds.ToString();
        _spreadHardTimeout = config.Spread.HardTimeoutSeconds.ToString();
        _spreadMaxConcurrent = config.Spread.MaxConcurrentRaces.ToString();
        _spreadAutoRace = config.Spread.AutoRaceOnNotification;
        _spreadNotifyComplete = config.Spread.NotifyOnRaceComplete;

        GlobalSkiplist = new ObservableCollection<SkiplistRule>(config.Spread.GlobalSkiplist);
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
    public string OpenRouterApiKey { get => _openRouterApiKey; set { _openRouterApiKey = value; OnPropertyChanged(); } }
    public string OpenRouterModel { get => _openRouterModel; set { _openRouterModel = value; OnPropertyChanged(); } }
    public string[] OpenRouterModels { get; } = ["gpt_oss/gpt-oss-120b", "anthropic/claude-sonnet-4", "google/gemini-2.5-pro", "meta-llama/llama-4-maverick", "deepseek/deepseek-r1"];
    public bool AutoDownloadWishlist { get => _autoDownloadWishlist; set { _autoDownloadWishlist = value; OnPropertyChanged(); } }
    public bool AutoExtract { get => _autoExtract; set { _autoExtract = value; OnPropertyChanged(); } }
    public bool DeleteArchivesAfterExtract { get => _deleteArchivesAfterExtract; set { _deleteArchivesAfterExtract = value; OnPropertyChanged(); } }
    public string SpeedLimitKbps { get => _speedLimitKbps; set { _speedLimitKbps = value; OnPropertyChanged(); } }
    public bool SkipIncompleteReleases { get => _skipIncompleteReleases; set { _skipIncompleteReleases = value; OnPropertyChanged(); } }
    public string MaxRetries { get => _maxRetries; set { _maxRetries = value; OnPropertyChanged(); } }
    public string RetryDelaySeconds { get => _retryDelaySeconds; set { _retryDelaySeconds = value; OnPropertyChanged(); } }
    public bool ScheduleEnabled { get => _scheduleEnabled; set { _scheduleEnabled = value; OnPropertyChanged(); } }
    public string ScheduleStartHour { get => _scheduleStartHour; set { _scheduleStartHour = value; OnPropertyChanged(); } }
    public string ScheduleEndHour { get => _scheduleEndHour; set { _scheduleEndHour = value; OnPropertyChanged(); } }
    public bool VerifySfv { get => _verifySfv; set { _verifySfv = value; OnPropertyChanged(); } }
    public bool PlaySoundOnComplete { get => _playSoundOnComplete; set { _playSoundOnComplete = value; OnPropertyChanged(); } }
    public string Theme { get => _theme; set { _theme = value; OnPropertyChanged(); } }
    public string SpreadPoolSize { get => _spreadPoolSize; set { _spreadPoolSize = value; OnPropertyChanged(); } }
    public string SpreadTransferTimeout { get => _spreadTransferTimeout; set { _spreadTransferTimeout = value; OnPropertyChanged(); } }
    public string SpreadHardTimeout { get => _spreadHardTimeout; set { _spreadHardTimeout = value; OnPropertyChanged(); } }
    public string SpreadMaxConcurrent { get => _spreadMaxConcurrent; set { _spreadMaxConcurrent = value; OnPropertyChanged(); } }
    public bool SpreadAutoRace { get => _spreadAutoRace; set { _spreadAutoRace = value; OnPropertyChanged(); } }
    public bool SpreadNotifyComplete { get => _spreadNotifyComplete; set { _spreadNotifyComplete = value; OnPropertyChanged(); } }
    public ObservableCollection<SkiplistRule> GlobalSkiplist { get; set; } = new();

    public string[] LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error"];
    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];
    public string[] ThemeOptions { get; } = ["Dark", "Light", "System"];

    public void ApplyTo(AppConfig config)
    {
        config.Logging.Level = LogLevel;
        config.Downloads.LocalPath = DownloadLocalPath;
        config.Downloads.MaxConcurrentDownloads = int.TryParse(MaxConcurrentDownloads, out var mcd) ? Math.Clamp(mcd, 1, 5) : 1;
        config.Downloads.StreamingBufferSizeKb = int.TryParse(StreamingBufferSize, out var sbs) ? Math.Clamp(sbs, 64, 4096) : 256;
        config.Downloads.WriteBufferLimitMb = int.TryParse(WriteBufferLimit, out var wbl) ? Math.Clamp(wbl, 0, 512) : 0;
        config.Downloads.QualityDefault = QualityDefault;
        // Store API keys in Credential Manager, clear from plaintext config
        SaveApiKey("omdb", OmdbApiKey);
        SaveApiKey("tmdb", TmdbApiKey);
        SaveApiKey("openrouter", OpenRouterApiKey);
        config.Downloads.OmdbApiKey = "";
        config.Downloads.TmdbApiKey = "";
        config.Downloads.OpenRouterModel = OpenRouterModel;
        config.Downloads.AutoDownloadWishlist = AutoDownloadWishlist;
        config.Downloads.AutoExtract = AutoExtract;
        config.Downloads.DeleteArchivesAfterExtract = DeleteArchivesAfterExtract;
        config.Downloads.SpeedLimitKbps = int.TryParse(SpeedLimitKbps, out var slk) ? Math.Max(slk, 0) : 0;
        config.Downloads.SkipIncompleteReleases = SkipIncompleteReleases;
        config.Downloads.MaxRetries = int.TryParse(MaxRetries, out var mr) ? Math.Clamp(mr, 0, 10) : 3;
        config.Downloads.RetryDelaySeconds = int.TryParse(RetryDelaySeconds, out var rds) ? Math.Clamp(rds, 5, 300) : 30;
        config.Downloads.ScheduleEnabled = ScheduleEnabled;
        config.Downloads.ScheduleStartHour = int.TryParse(ScheduleStartHour, out var ssh) ? Math.Clamp(ssh, 0, 23) : 0;
        config.Downloads.ScheduleEndHour = int.TryParse(ScheduleEndHour, out var seh) ? Math.Clamp(seh, 0, 23) : 6;
        config.Downloads.VerifySfv = VerifySfv;
        config.Downloads.PlaySoundOnComplete = PlaySoundOnComplete;
        config.Downloads.Theme = Theme;

        config.Spread.SpreadPoolSize = int.TryParse(SpreadPoolSize, out var sps) ? Math.Clamp(sps, 1, 10) : 2;
        config.Spread.TransferTimeoutSeconds = int.TryParse(SpreadTransferTimeout, out var stt) ? Math.Clamp(stt, 10, 600) : 60;
        config.Spread.HardTimeoutSeconds = int.TryParse(SpreadHardTimeout, out var sht) ? Math.Clamp(sht, 60, 7200) : 1200;
        config.Spread.MaxConcurrentRaces = int.TryParse(SpreadMaxConcurrent, out var smc) ? Math.Clamp(smc, 1, 10) : 3;
        config.Spread.AutoRaceOnNotification = SpreadAutoRace;
        config.Spread.NotifyOnRaceComplete = SpreadNotifyComplete;
        config.Spread.GlobalSkiplist = GlobalSkiplist.ToList();
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
