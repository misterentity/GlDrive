using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class AnnounceRuleValidator : IChangeValidator
{
    public string Category => AgentCategories.AnnounceRule;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/irc/announceRules", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (change.After is null) return new(false, "after-null", null);

        IrcAnnounceRule? after;
        try { after = JsonSerializer.Deserialize<IrcAnnounceRule>(JsonSerializer.Serialize(change.After)); }
        catch { return new(false, "after-parse-failed", null); }
        if (after is null || string.IsNullOrWhiteSpace(after.Pattern))
            return new(false, "after-null-or-empty-pattern", null);
        try { _ = new Regex(after.Pattern); } catch { return new(false, "pattern-bad-regex", null); }

        if (trailing == "-")
            return new(true, null, cfg => { var s = resolver(cfg); s?.Irc.AnnounceRules.Add(after); });

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.Irc.AnnounceRules.Count) return;
                s.Irc.AnnounceRules[idx].Pattern = after.Pattern;
                if (!string.IsNullOrEmpty(after.Channel)) s.Irc.AnnounceRules[idx].Channel = after.Channel;
            });

        return new(false, "target-shape-unsupported", null);
    }
}
