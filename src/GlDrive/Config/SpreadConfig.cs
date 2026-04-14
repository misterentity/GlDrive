namespace GlDrive.Config;

public class SpreadConfig
{
    public int SpreadPoolSize { get; set; } = 2;
    public int TransferTimeoutSeconds { get; set; } = 60;
    public int HardTimeoutSeconds { get; set; } = 1200;
    public int MaxConcurrentRaces { get; set; } = 1;
    public bool AutoRaceOnNotification { get; set; }
    public bool NotifyOnRaceComplete { get; set; } = true;
    public List<string> NukeMarkers { get; set; } = [".nuke", "NUKED-"];
    public List<SkiplistRule> GlobalSkiplist { get; set; } = [];

    /// <summary>
    /// When on, the rules engine logs every evaluation step at Info level
    /// to the main log. Useful for diagnosing why a release was/wasn't raced.
    /// </summary>
    public bool DebugMode { get; set; }
}

public class SiteSpreadConfig
{
    public Dictionary<string, string> Sections { get; set; } = new();
    public SitePriority Priority { get; set; } = SitePriority.Normal;
    public int MaxUploadSlots { get; set; } = 1;
    public int MaxDownloadSlots { get; set; } = 1;
    public bool DownloadOnly { get; set; }
    public List<SkiplistRule> Skiplist { get; set; } = [];
    public List<string> Affils { get; set; } = [];

    /// <summary>
    /// RaceTrade-style mappings: IRC section → internal/remote section with
    /// per-mapping trigger regex and tag rules.
    /// When set, takes precedence over Sections fuzzy matching for IRC announces.
    /// </summary>
    public List<SectionMapping> SectionMappings { get; set; } = [];

    /// <summary>
    /// Optional IMDb/TMDB/TVMaze metadata filter applied to releases before racing.
    /// Uses existing OmdbClient (with imdbapi.dev fallback) + TvMazeClient clients —
    /// no additional API keys required.
    /// </summary>
    public MetadataFilterConfig MetadataFilter { get; set; } = new();
}

/// <summary>
/// RaceTrade-style metadata filtering. When enabled, SpreadManager queries
/// IMDb/TVMaze for each release after rule evaluation passes and drops
/// releases whose metadata doesn't match the thresholds. On lookup failure
/// or timeout, the filter fails OPEN (allow the race) to avoid blocking on
/// flaky network.
/// </summary>
public class MetadataFilterConfig
{
    public bool Enabled { get; set; }

    /// <summary>Minimum IMDb rating (0-10). 0 = no threshold.</summary>
    public double MinImdbRating { get; set; }

    /// <summary>Minimum IMDb votes. 0 = no threshold.</summary>
    public int MinVotes { get; set; }

    /// <summary>Comma-separated genre allow-list. Empty = allow all.</summary>
    public string AllowGenres { get; set; } = "";

    /// <summary>Comma-separated genre deny-list (takes precedence over allow).</summary>
    public string DenyGenres { get; set; } = "";

    /// <summary>Skip TV shows that are Ended / Cancelled (TVMaze status).</summary>
    public bool SkipEndedShows { get; set; }

    /// <summary>Lookup timeout — bypasses filter if the API is slow.</summary>
    public int LookupTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Maps an IRC-announced section name to a remote section path on this site,
/// filtered by a release-name trigger regex. Tag rules apply only to this mapping.
/// </summary>
public class SectionMapping
{
    public string IrcSection { get; set; } = "";   // e.g., "TV-1080P" (from announce)
    public string RemoteSection { get; set; } = ""; // e.g., "X264-HD" (local section key, used by SpreadJob)
    public string Path { get; set; } = "";          // e.g., "/site/TV-HD" (remote path on this server)
    public string TriggerRegex { get; set; } = ".*"; // release-name filter for this mapping
    public List<SkiplistRule> TagRules { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public enum SitePriority { VeryLow = 0, Low = 625, Normal = 1250, High = 1875, VeryHigh = 2500 }

public class SkiplistRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public SkiplistAction Action { get; set; } = SkiplistAction.Deny;
    public SkiplistScope Scope { get; set; } = SkiplistScope.All;
    public bool MatchDirectories { get; set; } = true;
    public bool MatchFiles { get; set; } = true;
    public string? Section { get; set; }

    /// <summary>
    /// Optional RaceTrade-style rich expression: [key] operator value.
    /// When set, takes precedence over Pattern-based matching.
    /// Null/empty means "use Pattern".
    /// </summary>
    public string? Expression { get; set; }
}

public enum SkiplistAction { Allow, Deny, Unique, Similar }
public enum SkiplistScope { All, InRace }
