namespace GlDrive.Config;

public sealed class AlertConfig
{
    // Opt-in until retention and queue headroom have been measured for this profile.
    public bool Enabled { get; set; }
    public bool Coalesce { get; set; }
    public int CoalesceMinutes { get; set; } = 5;
    public int HistoryCapacity { get; set; } = 10000;
    public int QueueCapacity { get; set; } = 1024;
    public int RetryCount { get; set; } = 2;
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartHour { get; set; } = 23;
    public int QuietEndHour { get; set; } = 8;
    public List<string> MutedKinds { get; set; } = [];
    public List<string> MutedServerIds { get; set; } = [];
    public bool MorningDigestEnabled { get; set; }
    public int DigestHourLocal { get; set; } = 10;
    public int HeartbeatMinutes { get; set; } = 5;
    public List<AlertTargetConfig> Targets { get; set; } = [];
}

public sealed class AlertTargetConfig
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "ntfy";
    public bool Enabled { get; set; }
    public bool IncludeReleaseNames { get; set; }
    public int MinimumSeverity { get; set; } = 1;
}
