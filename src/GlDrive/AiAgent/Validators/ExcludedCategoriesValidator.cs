using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class ExcludedCategoriesValidator : IChangeValidator
{
    public string Category => AgentCategories.ExcludedCategories;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/notifications/excludedCategories", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (trailing != "-") return new(false, "must-append", null);
        var key = change.After?.ToString()?.Trim('"');
        if (string.IsNullOrWhiteSpace(key)) return new(false, "after-empty", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            if (!s.Notifications.ExcludedCategories.Contains(key!, StringComparer.OrdinalIgnoreCase))
                s.Notifications.ExcludedCategories.Add(key!);
        });
    }
}
