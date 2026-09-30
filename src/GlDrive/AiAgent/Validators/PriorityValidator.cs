using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class PriorityValidator : IChangeValidator
{
    public string Category => AgentCategories.Priority;

    // Must match the SitePriority enum names in SpreadConfig.cs: VeryLow, Low, Normal, High, VeryHigh
    private static readonly string[] TierOrder = ["VeryLow", "Low", "Normal", "High", "VeryHigh"];

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/sitePriority", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (!string.IsNullOrEmpty(trailing)) return new(false, "target-shape-unsupported", null);

        var afterStr = change.After?.ToString() ?? "";
        // JsonElement may stringify with quotes — strip them
        afterStr = afterStr.Trim('"').Trim();
        // Audit snapshots use the config's numeric enum representation, including for undo.
        if (!Enum.TryParse<SitePriority>(afterStr, out var afterTier) || !Enum.IsDefined(afterTier))
            return new(false, "bad-tier-value", null);
        afterStr = afterTier.ToString();
        if (afterStr == "VeryHigh") return new(false, "veryhigh-is-manual-only", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            var beforeIdx = Array.IndexOf(TierOrder, s.SpreadSite.Priority.ToString());
            var afterIdx = Array.IndexOf(TierOrder, afterStr);
            if (beforeIdx < 0 || afterIdx < 0) return;
            if (Math.Abs(beforeIdx - afterIdx) > 1) return;  // enforce ±1 tier only
            if (Enum.TryParse<SitePriority>(afterStr, out var parsed))
                s.SpreadSite.Priority = parsed;
        });
    }
}
