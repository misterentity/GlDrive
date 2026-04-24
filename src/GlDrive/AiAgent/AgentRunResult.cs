using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public sealed class AgentRunResult
{
    [JsonPropertyName("memo_update")]    public string MemoUpdate { get; set; } = "";
    [JsonPropertyName("changes")]        public List<AgentChange> Changes { get; set; } = [];
    [JsonPropertyName("suggestions")]    public List<AgentChange> Suggestions { get; set; } = [];
    [JsonPropertyName("brief_markdown")] public string BriefMarkdown { get; set; } = "";
}

public sealed class AgentChange
{
    [JsonPropertyName("category")]     public string Category { get; set; } = "";
    [JsonPropertyName("target")]       public string Target { get; set; } = "";
    [JsonPropertyName("before")]       public object? Before { get; set; }
    [JsonPropertyName("after")]        public object? After { get; set; }
    [JsonPropertyName("reasoning")]    public string Reasoning { get; set; } = "";
    [JsonPropertyName("evidence_ref")] public string EvidenceRef { get; set; } = "";
    [JsonPropertyName("confidence")]   public double Confidence { get; set; }
}

public static class AgentCategories
{
    public const string Skiplist           = "skiplist";
    public const string Priority           = "priority";
    public const string SectionMapping     = "sectionMapping";
    public const string AnnounceRule       = "announceRule";
    public const string ExcludedCategories = "excludedCategories";
    public const string WishlistPrune      = "wishlistPrune";
    public const string PoolSizing         = "poolSizing";
    public const string Blacklist          = "blacklist";
    public const string Affils             = "affils";
    public const string ErrorReport        = "errorReport";

    public static readonly string[] All =
    {
        Skiplist, Priority, SectionMapping, AnnounceRule, ExcludedCategories,
        WishlistPrune, PoolSizing, Blacklist, Affils, ErrorReport
    };
}
