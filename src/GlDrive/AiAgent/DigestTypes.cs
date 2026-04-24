using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public sealed class DigestBundle
{
    [JsonPropertyName("windowStart")] public string WindowStart { get; set; } = "";
    [JsonPropertyName("windowEnd")]   public string WindowEnd { get; set; } = "";
    [JsonPropertyName("races")]       public RacesDigest Races { get; set; } = new();
    [JsonPropertyName("nukes")]       public NukesDigest Nukes { get; set; } = new();
    [JsonPropertyName("siteHealth")]  public SiteHealthDigest SiteHealth { get; set; } = new();
    [JsonPropertyName("announces")]   public AnnouncesDigest Announces { get; set; } = new();
    [JsonPropertyName("wishlist")]    public WishlistDigest Wishlist { get; set; } = new();
    [JsonPropertyName("overrides")]   public OverridesDigest Overrides { get; set; } = new();
    [JsonPropertyName("downloads")]   public DownloadsDigest Downloads { get; set; } = new();
    [JsonPropertyName("transfers")]   public TransfersDigest Transfers { get; set; } = new();
    [JsonPropertyName("sectionActivity")] public SectionActivityDigest SectionActivity { get; set; } = new();
    [JsonPropertyName("errors")]      public ErrorsDigest Errors { get; set; } = new();
    [JsonPropertyName("evidencePointers")] public Dictionary<string, string> EvidencePointers { get; set; } = new();
}

public sealed class RacesDigest
{
    [JsonPropertyName("totalRaces")] public int TotalRaces { get; set; }
    [JsonPropertyName("winRateByServer")] public Dictionary<string, double> WinRateByServer { get; set; } = new();
    [JsonPropertyName("kbpsByRoute")] public Dictionary<string, double> KbpsByRoute { get; set; } = new(); // "src->dst"
    [JsonPropertyName("abortReasonHistogram")] public Dictionary<string, int> AbortReasonHistogram { get; set; } = new();
    [JsonPropertyName("completionRateBySection")] public Dictionary<string, double> CompletionRateBySection { get; set; } = new();
}

public sealed class NukesDigest
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("correlatedWithOurUploads")] public int Correlated { get; set; }
    [JsonPropertyName("topNukedReleases")] public List<NukeTop> TopNukedReleases { get; set; } = [];
    [JsonPropertyName("nukeRateBySection")] public Dictionary<string, double> NukeRateBySection { get; set; } = new();
    public sealed class NukeTop
    {
        [JsonPropertyName("release")] public string Release { get; set; } = "";
        [JsonPropertyName("count")]   public int Count { get; set; }
        [JsonPropertyName("reason")]  public string Reason { get; set; } = "";
        [JsonPropertyName("section")] public string Section { get; set; } = "";
    }
}

public sealed class SiteHealthDigest
{
    [JsonPropertyName("serverDeltas")] public Dictionary<string, HealthDelta> ServerDeltas { get; set; } = new();
    public sealed class HealthDelta
    {
        [JsonPropertyName("avgConnectMs_pct_change")] public double AvgConnectMsPctChange { get; set; }
        [JsonPropertyName("disconnects_total")] public int DisconnectsTotal { get; set; }
        [JsonPropertyName("poolExhaustCount_total")] public int PoolExhaustTotal { get; set; }
        [JsonPropertyName("ghostKills_total")] public int GhostKillsTotal { get; set; }
        [JsonPropertyName("tlsHandshake_pct_change")] public double TlsHandshakePctChange { get; set; }
        [JsonPropertyName("flagged")] public List<string> Flagged { get; set; } = [];
    }
}

public sealed class AnnouncesDigest
{
    [JsonPropertyName("clusters")] public List<AnnounceCluster> Clusters { get; set; } = [];
    public sealed class AnnounceCluster
    {
        [JsonPropertyName("representative")] public string Representative { get; set; } = "";
        [JsonPropertyName("count")]          public int Count { get; set; }
        [JsonPropertyName("channel")]        public string Channel { get; set; } = "";
        [JsonPropertyName("botNick")]        public string BotNick { get; set; } = "";
        [JsonPropertyName("nearestRule")]    public string? NearestRule { get; set; }
        [JsonPropertyName("nearestRuleDistance")] public int? NearestRuleDistance { get; set; }
    }
}

public sealed class WishlistDigest
{
    [JsonPropertyName("deadItems")] public List<DeadItem> DeadItems { get; set; } = [];
    [JsonPropertyName("nearMissPatterns")] public List<NearMiss> NearMissPatterns { get; set; } = [];
    public sealed class DeadItem
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
        [JsonPropertyName("daysSinceLastMatch")] public int DaysSinceLastMatch { get; set; }
        [JsonPropertyName("attemptsInWindow")] public int AttemptsInWindow { get; set; }
    }
    public sealed class NearMiss
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
        [JsonPropertyName("missReason")] public string MissReason { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
    }
}

public sealed class OverridesDigest
{
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = [];
    [JsonPropertyName("revertedAiPaths")] public List<string> RevertedAiPaths { get; set; } = [];
}

public sealed class DownloadsDigest
{
    [JsonPropertyName("totalComplete")] public int TotalComplete { get; set; }
    [JsonPropertyName("totalFailed")] public int TotalFailed { get; set; }
    [JsonPropertyName("failureClassHistogram")] public Dictionary<string, int> FailureClassHistogram { get; set; } = new();
}

public sealed class TransfersDigest
{
    [JsonPropertyName("kbpsMatrix")] public Dictionary<string, double> KbpsMatrix { get; set; } = new(); // "src->dst"
    [JsonPropertyName("ttfbP99Ms")]  public double TtfbP99Ms { get; set; }
}

public sealed class SectionActivityDigest
{
    [JsonPropertyName("perServerSection")] public List<Row> PerServerSection { get; set; } = [];
    public sealed class Row
    {
        [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
        [JsonPropertyName("section")]  public string Section { get; set; } = "";
        [JsonPropertyName("filesIn")]  public int FilesIn { get; set; }
        [JsonPropertyName("ourRaces")] public int OurRaces { get; set; }
        [JsonPropertyName("ourWinRate")] public double OurWinRate { get; set; }
    }
}

public sealed class ErrorsDigest
{
    [JsonPropertyName("topSignatures")] public List<Sig> TopSignatures { get; set; } = [];
    public sealed class Sig
    {
        [JsonPropertyName("component")] public string Component { get; set; } = "";
        [JsonPropertyName("exceptionType")] public string ExceptionType { get; set; } = "";
        [JsonPropertyName("normalizedMessage")] public string NormalizedMessage { get; set; } = "";
        [JsonPropertyName("count")]   public int Count { get; set; }
        [JsonPropertyName("trendVsPrior")] public string TrendVsPrior { get; set; } = ""; // "up" | "down" | "flat"
    }
}
