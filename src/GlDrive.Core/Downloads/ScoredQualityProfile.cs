namespace GlDrive.Downloads;

/// <summary>Opt-in quality policy. A legacy profile preserves the resolution filter's accepted set.</summary>
public sealed class ScoredQualityProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Scored quality";
    public List<QualityProfile> AllowedResolutions { get; set; } = [QualityProfile.Q1080p];
    public bool AllowUnknownResolution { get; set; }
    public bool AllowFullDisc { get; set; }
    public bool AllowLowQualitySources { get; set; }
    public bool AllowUnknownSize { get; set; } = true;
    public long? MinimumBytes { get; set; }
    public long? MaximumBytes { get; set; }
    public int MinimumScore { get; set; }
    public int CutoffScore { get; set; } = 3300;
    public bool AllowUpgrades { get; set; } = true;
    public bool PreferProperRepack { get; set; } = true;
    public bool UpgradeProperRepackAfterCutoff { get; set; }
    public List<QualityCustomFormat> CustomFormats { get; set; } = [];

    public static ScoredQualityProfile Legacy(QualityProfile quality) => new()
    {
        Id = "legacy-" + quality.ToString().ToLowerInvariant(), Name = "Legacy " + quality,
        AllowedResolutions = quality == QualityProfile.Any ? [] : [quality],
        AllowUnknownResolution = true, AllowFullDisc = true, AllowLowQualitySources = true,
        CutoffScore = 0
    };

    public static ScoredQualityProfile Recommended(QualityProfile quality) => new()
    {
        Id = "scored-" + quality.ToString().ToLowerInvariant(), Name = "Scored " + quality,
        AllowedResolutions = quality == QualityProfile.Any ? [] : [quality],
        CutoffScore = 3000 + ReleaseScorer.ResolutionScore(quality)
    };
}

public sealed class QualityCustomFormat
{
    public string Name { get; set; } = "Custom format";
    public string Pattern { get; set; } = "";
    public int Score { get; set; }
}
