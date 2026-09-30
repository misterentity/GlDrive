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

        // Field-level trigger patch: .../sectionMappings/{i}/triggerRegex with a string. This is the
        // natural JSON Pointer for "tighten this trigger", and it used to fail after-parse-failed
        // because the string was deserialized as a whole SectionMapping.
        var fieldParts = trailing.Split('/');
        if (fieldParts.Length == 2 && fieldParts[1] == "triggerRegex" && int.TryParse(fieldParts[0], out var fieldIdx))
            return ValidateTriggerPatch(change, config, resolver, fieldIdx);

        SectionMapping? after;
        try { after = ChangeValueJson.Read<SectionMapping>(change.After); }
        catch { return new(false, "after-parse-failed", null); }
        if (after is null) return new(false, "after-null", null);

        // Validate trigger regex compiles
        try { _ = new Regex(after.TriggerRegex ?? ""); }
        catch { return new(false, "trigger-bad-regex", null); }

        // Resolve the target site at validate time so we can confirm the proposed mapping
        // actually points at a section the site has. A site of null means we can't verify
        // (e.g. server not present in the validate-time config) — fall through unguarded.
        var site = resolver(config);

        if (trailing == "-")
        {
            // APPEND guard: an appended mapping whose RemoteSection the site doesn't have is
            // unroutable — SectionMapper.Resolve would silently fail at race time. Reject early.
            if (site is not null)
            {
                if (string.IsNullOrEmpty(after.RemoteSection))
                    return new(false, "remote-section-empty", null);
                if (!RemoteSectionExists(site, after.RemoteSection))
                    return new(false, "remote-section-unknown", null);
            }
            return new(true, null, cfg => { var s = resolver(cfg); s?.SpreadSite.SectionMappings.Add(after); });
        }

        if (int.TryParse(trailing, out var idx))
        {
            // PATCH guard: when a patch would set a new non-empty RemoteSection, it must point at
            // a known section too (same routability invariant as append). Empty RemoteSection on a
            // patch is allowed — the mutation below only overwrites RemoteSection when non-empty,
            // so it leaves the existing value intact.
            if (site is not null && !string.IsNullOrEmpty(after.RemoteSection)
                && !RemoteSectionExists(site, after.RemoteSection))
                return new(false, "remote-section-unknown", null);

            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.SpreadSite.SectionMappings.Count) return;
                var cur = s.SpreadSite.SectionMappings[idx];
                if (!IsDefaultTrigger(cur.TriggerRegex)) return;  // preserve user-edited triggers
                cur.TriggerRegex = after.TriggerRegex ?? ".*";
                if (!string.IsNullOrEmpty(after.IrcSection)) cur.IrcSection = after.IrcSection;
                if (!string.IsNullOrEmpty(after.RemoteSection)) cur.RemoteSection = after.RemoteSection;
            });
        }

        return new(false, "target-shape-unsupported", null);
    }

    private static ValidationResult ValidateTriggerPatch(AgentChange change, AppConfig config,
        Func<AppConfig, ServerConfig?> resolver, int idx)
    {
        var trigger = change.After is JsonElement { ValueKind: JsonValueKind.String } el
            ? el.GetString() ?? ""
            : change.After?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(trigger)) return new(false, "after-null", null);
        try { _ = new Regex(trigger); }
        catch { return new(false, "trigger-bad-regex", null); }

        var site = resolver(config);
        if (site is not null)
        {
            if (idx < 0 || idx >= site.SpreadSite.SectionMappings.Count) return new(false, "index-out-of-range", null);
            if (!IsDefaultTrigger(site.SpreadSite.SectionMappings[idx].TriggerRegex))
                return new(false, "trigger-user-edited", null);
        }

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            if (idx < 0 || idx >= s.SpreadSite.SectionMappings.Count) return;
            var cur = s.SpreadSite.SectionMappings[idx];
            if (!IsDefaultTrigger(cur.TriggerRegex)) return;  // preserve user-edited triggers
            cur.TriggerRegex = trigger;
        });
    }

    private static bool IsDefaultTrigger(string? trigger) => string.IsNullOrEmpty(trigger) || trigger == ".*";

    // A mapping is routable only if its RemoteSection matches a configured section key
    // (case-insensitive). Sections is the authoritative folder map; SectionMapper.Resolve keys off it.
    private static bool RemoteSectionExists(ServerConfig site, string remoteSection) =>
        site.SpreadSite.Sections.Keys.Any(k => string.Equals(k, remoteSection, StringComparison.OrdinalIgnoreCase));
}
