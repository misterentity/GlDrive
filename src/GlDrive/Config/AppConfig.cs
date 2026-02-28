namespace GlDrive.Config;

public class AppConfig
{
    public ConnectionConfig Connection { get; set; } = new();
    public MountConfig Mount { get; set; } = new();
    public TlsConfig Tls { get; set; } = new();
    public CacheConfig Cache { get; set; } = new();
    public PoolConfig Pool { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
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
}

public class PoolConfig
{
    public int PoolSize { get; set; } = 3;
    public int ReconnectInitialDelaySeconds { get; set; } = 5;
    public int ReconnectMaxDelaySeconds { get; set; } = 120;
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public int MaxFileSizeMb { get; set; } = 10;
    public int RetainedFiles { get; set; } = 3;
}
