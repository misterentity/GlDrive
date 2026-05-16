using GlDrive.Config;

namespace GlDrive.AiAgent;

/// <summary>
/// Validates AI-suggested changes to a server's spreadSite.downloadOnly flag.
/// Both directions allowed (true ↔ false) — the agent may detect consistent
/// upload-side failures and propose marking a site download-only, or detect
/// a previously-misclassified site that should now allow uploads. The
/// confidence threshold + per-category budget in ChangeApplier prevent the
/// agent from flapping the flag.
/// </summary>
public sealed class DownloadOnlyValidator : IChangeValidator
{
    public string Category => AgentCategories.DownloadOnly;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/downloadOnly", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (!string.IsNullOrEmpty(trailing))
            return new(false, "target-shape-unsupported", null);

        var afterStr = change.After?.ToString()?.Trim('"').ToLowerInvariant() ?? "";
        if (afterStr != "true" && afterStr != "false")
            return new(false, "after-not-bool", null);
        var newValue = afterStr == "true";

        return new(true, null, cfg =>
        {
            var s = resolver(cfg);
            if (s is null) return;
            s.SpreadSite.DownloadOnly = newValue;
        });
    }
}
