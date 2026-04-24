using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class SkiplistValidator : IChangeValidator
{
    public string Category => AgentCategories.Skiplist;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!TryMatchServer(change.Target, "/spread/skiplistRules", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (change.After is null && change.Before is null)
            return new(false, "empty-change", null);

        SkiplistRule? newRule = null;
        if (change.After is not null)
        {
            try { newRule = JsonSerializer.Deserialize<SkiplistRule>(JsonSerializer.Serialize(change.After)); }
            catch { return new(false, "after-parse-failed", null); }
            if (newRule is null) return new(false, "after-null", null);
            if (!PatternCompiles(newRule.Pattern ?? "", newRule.IsRegex)) return new(false, "pattern-bad", null);
            if (newRule.Action == SkiplistAction.Allow && change.Confidence < 0.8)
                return new(false, "allow-needs-higher-confidence", null);
        }

        if (trailing == "-")
            return new(true, null, cfg => { var s = resolver(cfg); s?.SpreadSite.Skiplist.Add(newRule!); });

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.SpreadSite.Skiplist.Count) return;
                if (change.After is null) s.SpreadSite.Skiplist.RemoveAt(idx);
                else s.SpreadSite.Skiplist[idx] = newRule!;
            });

        return new(false, "target-shape-unsupported", null);
    }

    private static bool PatternCompiles(string p, bool isRegex)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (!isRegex) return true;  // glob — accept
        try { _ = new Regex(p); return true; } catch { return false; }
    }

    // Shared helper used by other validators in this namespace.
    // Parses "/servers/{id}{expectedSuffix}[/{trailing}]" and returns a server resolver + trailing segment.
    internal static bool TryMatchServer(string pointer, string expectedSuffix,
        out Func<AppConfig, ServerConfig?> serverResolver, out string trailing)
    {
        trailing = "";
        serverResolver = _ => null;
        if (!pointer.StartsWith("/servers/")) return false;
        var rest = pointer["/servers/".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        var serverId = rest[..slash];
        var afterId = rest[slash..];  // e.g. "/spread/skiplistRules/-"
        if (!afterId.StartsWith(expectedSuffix)) return false;
        trailing = afterId[expectedSuffix.Length..].TrimStart('/');
        serverResolver = cfg => cfg.Servers.FirstOrDefault(s => s.Id == serverId);
        return true;
    }
}
