namespace GlDrive.Config;

public class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();
    public DownloadConfig Downloads { get; set; } = new();
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
}

public class ConnectionConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "";
    public string RootPath { get; set; } = "/";
    public int[] PassivePorts { get; set; } = [];
}

public class MountConfig
{
    public string DriveLetter { get; set; } = "G";
    public string VolumeLabel { get; set; } = "glFTPd";
    public bool AutoMountOnStart { get; set; } = true;
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
}

public class DownloadConfig
{
    public string LocalPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GlDrive");
    public int MaxConcurrentDownloads { get; set; } = 1;
    public int StreamingBufferSizeKb { get; set; } = 256;
    public int WriteBufferLimitMb { get; set; } = 0;
    public string QualityDefault { get; set; } = "1080p";
    public string OmdbApiKey { get; set; } = "";
    public bool AutoDownloadWishlist { get; set; } = true;
}
