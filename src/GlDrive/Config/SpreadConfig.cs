namespace GlDrive.Config;

public class SpreadConfig
{
    // Raised 2 → 3. Chain mode is gone so races now use N² concurrent routes
    // bounded by per-site slots; a 1-connection spread pool serialised every
    // transfer and was the second root cause of half-raced releases. glftpd's
    // default max_logins=4 + main pool of 2 + spread of 3 exceeds the cap by 1;
    // the pool tolerates 530 rejections and will run with whatever connections
    // actually got through. Tight BNCs can lower to 2.
    public int SpreadPoolSize { get; set; } = 3;
    public int TransferTimeoutSeconds { get; set; } = 60;
    public int HardTimeoutSeconds { get; set; } = 1200;
    public int MaxConcurrentRaces { get; set; } = 1;

    /// <summary>
    /// Send a NOOP liveness probe before handing out a pooled spread connection
    /// for an FXP transfer. Spread pools have no keepalive, so a connection can
    /// sit idle long enough for the BNC/network to silently close its socket;
    /// using it then fails with "No connection to the server exists" mid-transfer.
    /// Validating on borrow catches the dead socket and swaps in a fresh
    /// connection. Costs ~1 round-trip per transfer. Default on.
    /// </summary>
    public bool ValidateConnectionOnBorrow { get; set; } = true;

    /// <summary>
    /// Keepalive interval (seconds) for idle spread-pool connections. A periodic
    /// NOOP keeps sockets warm so they don't get reaped by the BNC during quiet
    /// spells between races. 0 disables. Default 30s.
    /// </summary>
    public int SpreadKeepaliveSeconds { get; set; } = 30;
    public bool AutoRaceOnNotification { get; set; }
    public bool NotifyOnRaceComplete { get; set; } = true;
    public List<string> NukeMarkers { get; set; } = [".nuke", "NUKED-"];

    /// <summary>
    /// When on, a race runs until each destination is confirmed complete via
    /// zipscript (a CompletionMarkers match OR all SFV files present + no -MISSING-
    /// stubs), not merely until the source files were copied. Off = legacy
    /// file-count completion. Default on.
    /// </summary>
    public bool WaitForDestinationComplete { get; set; } = true;

    /// <summary>Max minutes to wait for a destination's zipscript to mark complete
    /// AFTER all files are delivered, before that dest is recorded as a timeout.
    /// Independent of HardTimeoutSeconds (which budgets the transfer phase).</summary>
    public int DestinationCompletionWaitMinutes { get; set; } = 10;

    /// <summary>Directory re-list cadence (seconds) while waiting for completion
    /// (no active transfers). Keeps polling for the marker without hammering.</summary>
    public int CompletionRefreshIntervalSeconds { get; set; } = 30;

    /// <summary>Substrings (case-insensitive) that mark a release dir as complete in
    /// a destination listing. Site-tunable, like NukeMarkers. Empty = heuristic only.</summary>
    public List<string> CompletionMarkers { get; set; } =
        ["[ COMPLETE ]", "[ COMPLETED ]", "(COMPLETE)", "COMPLETE", "-=COMPLETE=-"];

    /// <summary>When a release moves off its source mid-race, search other connected
    /// sites for an alternate source and continue feeding the target from it.</summary>
    public bool AlternateSourceSearch { get; set; } = true;

    /// <summary>Per-server timeout (seconds) for the mid-race alternate-source search.</summary>
    public int AlternateSourceSearchTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// When a destination DENIES MKD (glftpd mkdir-filter / path-filter rejection),
    /// treat it as RELEASE-scoped: drop that destination for THIS race only and do
    /// NOT persist a section blacklist. Many sites (e.g. SYNAPSE) enforce per-release
    /// rules entirely via mkdir filters — banned genre, wrong year, not on the TV
    /// allowlist — so a denial means THIS release failed the rule, not that the whole
    /// section is forbidden. The next release in the same section is still tried.
    /// Off = legacy behavior (section-blacklist permanent path/rights denials for 14
    /// days, freezing the section). Default on. (Dirscript denials are always
    /// release-scoped regardless of this flag.)
    /// </summary>
    public bool StopRaceOnMkdDenied { get; set; } = true;

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
    // Defaults raised from 1 → 3 so new configs run properly parallel
    // transfers out of the box. With slots=1, chain mode races were fully
    // serial at ~5MB/s per file and couldn't finish 2160p releases before
    // glftpd's dirscript flagged them as stale and started denying MKD.
    public int MaxUploadSlots { get; set; } = 3;
    public int MaxDownloadSlots { get; set; } = 3;
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
