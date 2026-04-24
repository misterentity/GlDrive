using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class AffilsValidator : IChangeValidator
{
    public string Category => AgentCategories.Affils;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/affils", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (trailing != "-")
            return new(false, "must-append", null);

        var group = change.After?.ToString()?.Trim('"');
        if (string.IsNullOrWhiteSpace(group))
            return new(false, "after-empty", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg);
            if (s is null) return;
            s.SpreadSite.Affils ??= [];
            if (!s.SpreadSite.Affils.Contains(group!, StringComparer.OrdinalIgnoreCase))
                s.SpreadSite.Affils.Add(group!);
        });
    }
}
