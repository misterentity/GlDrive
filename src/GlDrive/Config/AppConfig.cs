namespace GlDrive.Config;

public class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();
    public DownloadConfig Downloads { get; set; } = new();
    public SpreadConfig Spread { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public PlexConfig Plex { get; set; } = new();
    public ControlApiConfig ControlApi { get; set; } = new();
    public TorrentConfig Torrent { get; set; } = new();
    public HealthConfig Health { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    public ConfigHistoryConfig ConfigHistory { get; set; } = new();
    public LibraryConfig Library { get; set; } = new();
    public HookConfig Hooks { get; set; } = new();
    public NukeWatchConfig NukeWatch { get; set; } = new();
    public IrcAttentionConfig IrcAttention { get; set; } = new();
    public DataStewardshipConfig DataStewardship { get; set; } = new();
    public RecoveryConfig Recovery { get; set; } = new();

    public string ResolveAgentModel() => string.IsNullOrWhiteSpace(Agent.ModelId)
        ? "anthropic/claude-sonnet-4-6" : Agent.ModelId;
}

/// <summary>Persistent, account-budget-aware site and passive announce supervision.</summary>
public class HealthConfig
{
    public bool Enabled { get; set; } = true;
    public bool TcpProbesEnabled { get; set; }
    public bool AnnounceSilenceAlertsEnabled { get; set; } = true;
    public bool AnnounceRecoveryEnabled { get; set; }
}

/// <summary>Player torrent search and download settings.</summary>
public class TorrentConfig
{
    /// <summary>
    /// VPN tunnel adapter to bind torrent sockets to, e.g. "ProtonVPN". Empty = no binding,
    /// torrent traffic uses the ordinary connection.
    ///
    /// Binds only the listener and DHT socket — outgoing peer connections cannot be bound on
    /// MonoTorrent 3.0.2, the newest stable release. See VpnBinding for the full limitation.
    /// </summary>
    public string VpnAdapterName { get; set; } = "";

    /// <summary>
    /// Optional proxy for torrent SEARCH traffic only (e.g. "http://127.0.0.1:8080"). Some
    /// networks block public indexers: this machine cannot reach apibay.org (TCP connects, TLS
    /// returns nothing — SNI filtering) and cannot resolve yts.mx at all. A proxy is the only
    /// way around SNI filtering; DNS-over-HTTPS would not help, since the block is on the TLS
    /// handshake rather than the lookup. Empty = direct.
    /// </summary>
    public string SearchProxyUrl { get; set; } = "";

    /// <summary>Last folder chosen in the download picker, used to seed the next dialog.</summary>
    public string LastDownloadFolder { get; set; } = "";

    /// <summary>
    /// Refuse to write executable file types out of torrents, on both the download and the
    /// play path. Default true.
    ///
    /// This is hygiene, not security: GlDrive never runs downloaded content, and the realistic
    /// risk is double-clicking something in the save folder that looked like the film. Set
    /// false only if you routinely torrent software.
    ///
    /// LIMITATIONS, stated in full because they are not obvious. It cannot see inside archives.
    /// BitTorrent transfers whole pieces, so a skipped file still receives the bytes it shares
    /// with a kept neighbour's first or last piece — and lands COMPLETE if it is smaller than
    /// one piece and sits between two kept files. A zero-length entry is created on disk
    /// regardless of priority. Blocked artifacts are deleted when the download stops, but not
    /// if the process is killed first.
    /// </summary>
    public bool BlockExecutables { get; set; } = true;

    /// <summary>
    /// Extensions to block in addition to the built-in set. Leading dot optional,
    /// e.g. ["jar", ".reg", ".docm"].
    /// </summary>
    public string[] ExtraBlockedExtensions { get; set; } = [];

    /// <summary>
    /// Extensions to REMOVE from the built-in set, e.g. [".msi"] if you torrent software.
    /// A blunt escape hatch; prefer the per-download override offered in the Player tab.
    /// </summary>
    public string[] AllowedExtensionOverrides { get; set; } = [];
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

    /// <summary>
    /// FTP command to query for credits/ratio in the status bar. Default works for
    /// glftpd; override per-server if your site exposes credits via a custom command.
    /// </summary>
    public string SiteStatsCommand { get; set; } = "SITE STATS";
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
    public bool LoginRateLimitEnabled { get; set; } = true;
    public int MaxLoginAttemptsPerFiveMinutes { get; set; } = 8;
    public int PoolSize { get; set; } = 3;
    public int KeepaliveIntervalSeconds { get; set; } = 15;
    public int ReconnectInitialDelaySeconds { get; set; } = 5;
    public int ReconnectMaxDelaySeconds { get; set; } = 120;

    // Account-wide login cap (v3.6 Phase 1). The hard simultaneous-login limit the
    // glftpd account allows. ALL pools to the same account (main + spread + download)
    // share one gate sized to (LoginCap − LoginHeadroom), so their combined live
    // logins never exceed the cap and stop self-inflicting 530s. Conservative
    // defaults (3/1 ⇒ 2 usable); the gate auto-tightens if a real 530 reveals a
    // lower cap. Raise LoginCap for accounts that genuinely allow more.
    public int LoginCap { get; set; } = 3;
    public int LoginHeadroom { get; set; } = 1;
}

/// <summary>
/// Loopback-only HTTP control surface, so races can be triggered and inspected without
/// driving the WPF UI (tray automation is fragile and there was no other way in).
///
/// Security posture, deliberately narrow: the listener binds ONLY to 127.0.0.1, every
/// request must present the bearer token, and any request whose remote endpoint is not a
/// loopback address is rejected regardless. Disabled by default; the token is generated on
/// first enable and persisted, never logged.
/// </summary>
public class ControlApiConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8756;
    public string Token { get; set; } = "";
    public AutomationSecurityConfig Security { get; set; } = new();
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public int MaxFileSizeMb { get; set; } = 10;
    public int RetainedFiles { get; set; } = 30;
    // Passive, locally bounded evidence collection; never an engine admission limit.
    [System.Text.Json.Serialization.JsonConverter(typeof(OptionalCaptureFlagConverter))]
    public bool IncidentCaptureEnabled { get; set; } = true;
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
    /// Exact section mappings take precedence, then a logical media class when a release
    /// name is supplied, then LocalPath. Call the layout resolver to track class-folder suppression.
    /// </summary>
    public string GetPathForCategory(string category, string? releaseName = null) =>
        GlDrive.Downloads.DownloadPathResolver.Resolve(this, category, releaseName).BasePath;
    public int MaxConcurrentDownloads { get; set; } = ConfigurationLimits.DefaultConcurrentDownloads;
    public int StreamingBufferSizeKb { get; set; } = 256;
    public int WriteBufferLimitMb { get; set; } = 0;
    public string QualityDefault { get; set; } = "1080p";
    /// <summary>Opt in to scored wishlist acquisition, pending-copy dedupe and cutoff completion.</summary>
    public bool ScoredQualityEnabled { get; set; }
    /// <summary>Wishlist-only CAM/TS/TC floor; explicit item/custom-profile choices are preserved.</summary>
    public bool BlockLowQualityWishlistSources { get; set; } = true;
    /// <summary>Region for explicit TMDB release-date lookups. Dates never gate acquisition.</summary>
    public string MovieCalendarRegion { get; set; } = "US";
    public bool WantedDecisionWindowEnabled { get; set; }
    public int WantedDecisionWindowMinutes { get; set; } = 15;
    public int WantedCandidateMaxAgeHours { get; set; } = 24;
    public bool WantedIndexSweepsEnabled { get; set; }
    public int WantedIndexSweepLimit { get; set; } = 100;
    public bool PreferFastestWantedSource { get; set; }
    public bool RaceCompletedPullsEnabled { get; set; }
    public List<string> RacePullServerIds { get; set; } = [];
    public List<GlDrive.Downloads.ScoredQualityProfile> Profiles { get; set; } = [];
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
    public string OpenRouterModel { get; set; } = "anthropic/claude-sonnet-4-6";
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
    /// <summary>Opt in to targeted CRC repairs inside staging created by this download.</summary>
    public bool SfvRepairEnabled { get; set; }
    public int SfvRepairMaxAttempts { get; set; } = 2;
    /// <summary>Multi-stream / multi-network download settings (plexo-style). Downloads only — never FXP.</summary>
    public MultiStreamConfig MultiStream { get; set; } = new();
    public bool PlaySoundOnComplete { get; set; }
    public string Theme { get; set; } = "Cyberpunk";
    public bool CyberpunkPromoted { get; set; }
}

public class AgentConfig
{
    public bool AccountabilityEnabled { get; set; } = true;
    public bool NoOpRejectionEnabled { get; set; } = true;
    public bool CounterfactualReplayEnabled { get; set; } = true;
    public bool AutoRevertRegressions { get; set; }
    public int OutcomeMinimumSamples { get; set; } = 10;
    public int OutcomeTolerancePercent { get; set; } = 10;
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

/// <summary>
/// Multi-stream / multi-network downloads (plan: docs/superpowers/plans/2026-10-14-multistream-downloads.md).
/// Applies to FTP release downloads, HTTP downloads (updates) and — speed caps only — torrents.
/// FXP racing never reads this.
/// </summary>
public class MultiStreamConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Let an FTP download use the site's race login (the spread pool's idle connection) as a
    /// second stream while that site is not racing. The download hands it back within about a
    /// second when a race starts or is queued for the site. CPSV sites only.
    /// </summary>
    public bool BorrowIdleRaceLogin { get; set; } = true;

    /// <summary>HTTP connections per enabled network (plexo starts at 8; updates are one zip, 4 is plenty).</summary>
    public int HttpLanesPerNetwork { get; set; } = ConfigurationLimits.DefaultHttpLanesPerNetwork;

    /// <summary>An in-flight FTP range is only split when both halves are at least this big.</summary>
    public int FtpMinSplitMb { get; set; } = 32;

    /// <summary>Slow mode: a temporary total cap, toggled from the tray.</summary>
    public bool SlowMode { get; set; }
    public int SlowModeKbps { get; set; } = 2048;

    /// <summary>Networks downloads may use. Empty = default route only (unchanged behavior).</summary>
    public List<NetworkPathConfig> Networks { get; set; } = [];
}

public enum AllowancePeriod { None, Daily, Weekly, Monthly }

public class NetworkPathConfig
{
    /// <summary>NetworkInterface.Id — stable across reboots; AdapterName is the fallback match.</summary>
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Per-network download cap, 0 = unlimited.</summary>
    public int SpeedLimitKbps { get; set; }
    /// <summary>Data allowance per <see cref="Period"/> in GB, 0 = unlimited. Counts only GlDrive download traffic.</summary>
    public double AllowanceGb { get; set; }
    public AllowancePeriod Period { get; set; } = AllowancePeriod.None;
}
