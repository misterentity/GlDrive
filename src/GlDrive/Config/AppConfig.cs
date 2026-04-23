namespace GlDrive.Config;

public class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();
    public DownloadConfig Downloads { get; set; } = new();
    public SpreadConfig Spread { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();

    public string ResolveAgentModel() => string.IsNullOrWhiteSpace(Agent.ModelId)
        ? "anthropic/claude-sonnet-4-6" : Agent.ModelId;
}

public class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ConnectionConfig Connection { get; set; } = new();
    public MountConfig Mount { get; set; } = new();
    public TlsConfig Tls { get; set; } = new();
    public CacheConfig Cache { get; set; } = new();
    public PoolConfig Pool { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public IrcConfig Irc { get; set; } = new();
    public SiteSpreadConfig SpreadSite { get; set; } = new();
    public int SpeedLimitKbps { get; set; }
}

public class ConnectionConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "";
    public string RootPath { get; set; } = "/";
    public int[] PassivePorts { get; set; } = [];
    public ProxyConfig? Proxy { get; set; }
}

public class ProxyConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1080;
    public string Username { get; set; } = "";
}

public class MountConfig
{
    public string DriveLetter { get; set; } = "G";
    public string VolumeLabel { get; set; } = "glFTPd";
    public bool AutoMountOnStart { get; set; } = true;
    public bool MountDrive { get; set; } = true;
}

public class TlsConfig
{
    public bool PreferTls12 { get; set; } = true;
    public string CertificateFingerprintFile { get; set; } = "trusted_certs.json";
}

public class CacheConfig
{
    public int DirectoryListingTtlSeconds { get; set; } = 30;
    public int MaxCachedDirectories { get; set; } = 500;
    public int DirectoryListTimeoutSeconds { get; set; } = 30;
    public int FileInfoTimeoutMs { get; set; } = 1000;
    public int ReadBufferSpillThresholdMb { get; set; } = 50;
}

public class PoolConfig
{
    public int PoolSize { get; set; } = 3;
    public int KeepaliveIntervalSeconds { get; set; } = 30;
    public int ReconnectInitialDelaySeconds { get; set; } = 5;
    public int ReconnectMaxDelaySeconds { get; set; } = 120;
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public int MaxFileSizeMb { get; set; } = 10;
    public int RetainedFiles { get; set; } = 3;
}

public class NotificationConfig
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public string WatchPath { get; set; } = "/recent";
    public List<string> ExcludedCategories { get; set; } = [];
}

public enum SearchMethod { Auto, SiteSearch, CachedIndex, LiveCrawl }

public class SearchConfig
{
    public List<string> SearchPaths { get; set; } = ["/"];
    public int MaxDepth { get; set; } = 2;
    public SearchMethod Method { get; set; } = SearchMethod.Auto;
    public int IndexCacheMinutes { get; set; } = 60;
}

public class DownloadConfig
{
    public string LocalPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GlDrive");
    public Dictionary<string, string> CategoryPaths { get; set; } = new();

    /// <summary>
    /// Resolves the base download path for a given category.
    /// If the category has a custom path mapped, use that; otherwise use default LocalPath.
    /// </summary>
    public string GetPathForCategory(string category)
    {
        if (!string.IsNullOrEmpty(category) && CategoryPaths.TryGetValue(category, out var customPath)
            && !string.IsNullOrWhiteSpace(customPath))
            return customPath;
        return LocalPath;
    }
    public int MaxConcurrentDownloads { get; set; } = 1;
    public int StreamingBufferSizeKb { get; set; } = 256;
    public int WriteBufferLimitMb { get; set; } = 0;
    public string QualityDefault { get; set; } = "1080p";
    [System.Text.Json.Serialization.JsonIgnore]
    public string OmdbApiKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string TmdbApiKey { get; set; } = "";

    /// <summary>Resolve OMDB key from Credential Manager only.</summary>
    public string ResolveOmdbKey() => CredentialStore.GetApiKey("omdb") ?? "";
    /// <summary>Resolve TMDB key from Credential Manager only.</summary>
    public string ResolveTmdbKey() => CredentialStore.GetApiKey("tmdb") ?? "";
    /// <summary>Resolve OpenRouter key from Credential Manager only.</summary>
    public string ResolveOpenRouterKey() => CredentialStore.GetApiKey("openrouter") ?? "";
    public string OpenRouterModel { get; set; } = "openai/gpt-oss-120b:free";
    public bool AutoDownloadWishlist { get; set; } = true;
    public bool AutoExtract { get; set; } = true;
    public bool DeleteArchivesAfterExtract { get; set; } = true;
    public int SpeedLimitKbps { get; set; }
    public bool SkipIncompleteReleases { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
    public bool ScheduleEnabled { get; set; }
    public int ScheduleStartHour { get; set; }
    public int ScheduleEndHour { get; set; } = 6;
    public bool VerifySfv { get; set; } = true;
    public bool PlaySoundOnComplete { get; set; }
    public string Theme { get; set; } = "Dark";
}

public class AgentConfig
{
    public bool Enabled { get; set; } = false;
    public int RunHourLocal { get; set; } = 4;
    public int ConfidenceThreshold_x100 { get; set; } = 70;
    public int MaxChangesPerRun { get; set; } = 20;
    public int MaxChangesPerCategory { get; set; } = 5;
    public int DryRunsRemaining { get; set; } = 3;
    public int WindowDays { get; set; } = 7;
    public int GzipAfterDays { get; set; } = 30;
    public int DeleteAfterDays { get; set; } = 90;
    public int SnapshotRetentionCount { get; set; } = 30;
    public int NukePollIntervalHours { get; set; } = 6;
    public string ModelId { get; set; } = "anthropic/claude-sonnet-4-6";
    public int TelemetryMaxFileMB { get; set; } = 100;
    public bool HasAcceptedConsent { get; set; } = false;
}
