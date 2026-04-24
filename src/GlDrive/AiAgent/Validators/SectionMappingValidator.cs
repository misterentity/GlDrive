using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class SectionMappingValidator : IChangeValidator
{
    public string Category => AgentCategories.SectionMapping;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/sectionMappings", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (change.After is null) return new(false, "after-null", null);

        SectionMapping? after;
        try { after = JsonSerializer.Deserialize<SectionMapping>(JsonSerializer.Serialize(change.After)); }
        catch { return new(false, "after-parse-failed", null); }
        if (after is null) return new(false, "after-null", null);

        // Validate trigger regex compiles
        try { _ = new Regex(after.TriggerRegex ?? ""); }
        catch { return new(false, "trigger-bad-regex", null); }

        if (trailing == "-")
            return new(true, null, cfg => { var s = resolver(cfg); s?.SpreadSite.SectionMappings.Add(after); });

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.SpreadSite.SectionMappings.Count) return;
                var cur = s.SpreadSite.SectionMappings[idx];
                var isDefault = string.IsNullOrEmpty(cur.TriggerRegex) || cur.TriggerRegex == ".*";
                if (!isDefault) return;  // preserve user-edited triggers
                cur.TriggerRegex = after.TriggerRegex ?? ".*";
                if (!string.IsNullOrEmpty(after.IrcSection)) cur.IrcSection = after.IrcSection;
                if (!string.IsNullOrEmpty(after.RemoteSection)) cur.RemoteSection = after.RemoteSection;
            });

        return new(false, "target-shape-unsupported", null);
    }
}
