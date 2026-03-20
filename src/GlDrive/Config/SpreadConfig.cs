namespace GlDrive.Config;

public class SpreadConfig
{
    public int SpreadPoolSize { get; set; } = 2;
    public int TransferTimeoutSeconds { get; set; } = 60;
    public int HardTimeoutSeconds { get; set; } = 1200;
    public List<SkiplistRule> GlobalSkiplist { get; set; } = [];
}

public class SiteSpreadConfig
{
    public Dictionary<string, string> Sections { get; set; } = new();
    public SitePriority Priority { get; set; } = SitePriority.Normal;
    public int MaxUploadSlots { get; set; } = 3;
    public int MaxDownloadSlots { get; set; } = 3;
    public bool DownloadOnly { get; set; }
    public List<SkiplistRule> Skiplist { get; set; } = [];
    public List<string> Affils { get; set; } = [];
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
}

public enum SkiplistAction { Allow, Deny, Unique, Similar }
public enum SkiplistScope { All, InRace }
