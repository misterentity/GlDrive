using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Downloads;

namespace GlDrive.Spread;

public class SkiplistTraceEntry
{
    public string Pattern { get; set; } = "";
    public string? Section { get; set; }
    public string Action { get; set; } = "";
    public string Result { get; set; } = ""; // "Matched", "Skipped (wrong section)", "No match", etc.
    public bool IsMatch { get; set; }
    public string Source { get; set; } = ""; // "Site" or "Global"
}

public class SkiplistEvaluator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new();

    public SkiplistAction Evaluate(string fileName, bool isDir, bool inRace,
        string serverId, string? section,
        IReadOnlyList<SkiplistRule> siteRules,
        IReadOnlyList<SkiplistRule> globalRules,
        ParsedRelease? parsed = null)
    {
        var result = EvaluateRules(fileName, isDir, inRace, section, siteRules, parsed);
        if (result.HasValue) return result.Value;

        result = EvaluateRules(fileName, isDir, inRace, section, globalRules, parsed);
        if (result.HasValue) return result.Value;

        return SkiplistAction.Allow;
    }

    /// <summary>
    /// RaceTrade-style tiered evaluation with affiliate auto-allow and
    /// per-mapping tag rules.
    ///
    /// Priority:
    ///   0. Affiliate groups → immediate ALLOW (highest priority)
    ///   1. Site section DENY rules (rules scoped to the current section with action=Deny)
    ///   2. Tag rules for the matched mapping (DENY first, then ALLOW)
    ///   3. Site section ALLOW rules (remaining site rules)
    ///   4. Global DENY then ALLOW
    ///   5. Default ALLOW
    /// </summary>
    public SkiplistAction EvaluateTiered(string fileName, bool isDir, bool inRace,
        string? section,
        IReadOnlyList<SkiplistRule> siteRules,
        IReadOnlyList<SkiplistRule> tagRules,
        IReadOnlyList<SkiplistRule> globalRules,
        IReadOnlyList<string> affils,
        ParsedRelease? parsed)
    {
        // 0. Affiliate auto-allow
        if (parsed?.Group is { } g && affils.Any(a => string.Equals(a, g, StringComparison.OrdinalIgnoreCase)))
            return SkiplistAction.Allow;

        // 1. Site section DENY rules
        var denyOnly = EvaluateRulesFiltered(fileName, isDir, inRace, section, siteRules, parsed, onlyDeny: true);
        if (denyOnly.HasValue) return denyOnly.Value;

        // 2. Tag rules (both DENY and ALLOW)
        var tag = EvaluateRules(fileName, isDir, inRace, section, tagRules, parsed);
        if (tag.HasValue) return tag.Value;

        // 3. Site section ALLOW rules
        var allowOnly = EvaluateRulesFiltered(fileName, isDir, inRace, section, siteRules, parsed, onlyDeny: false);
        if (allowOnly.HasValue) return allowOnly.Value;

        // 4. Global rules
        var global = EvaluateRules(fileName, isDir, inRace, section, globalRules, parsed);
        if (global.HasValue) return global.Value;

        // 5. Default allow
        return SkiplistAction.Allow;
    }

    /// <summary>
    /// Evaluates skiplist rules and returns a detailed trace of every rule checked.
    /// Used for the race history detail popup.
    /// </summary>
    public (SkiplistAction action, List<SkiplistTraceEntry> trace) EvaluateWithTrace(
        string name, bool isDir, bool inRace, string? section,
        IReadOnlyList<SkiplistRule> siteRules,
        IReadOnlyList<SkiplistRule> globalRules,
        ParsedRelease? parsed = null)
    {
        var trace = new List<SkiplistTraceEntry>();
        SkiplistAction finalAction = SkiplistAction.Allow;

        void TraceRules(IReadOnlyList<SkiplistRule> rules, string source)
        {
            foreach (var rule in rules)
            {
                var displayPattern = !string.IsNullOrWhiteSpace(rule.Expression) ? rule.Expression : rule.Pattern;
                var entry = new SkiplistTraceEntry
                {
                    Pattern = displayPattern,
                    Section = rule.Section,
                    Action = rule.Action.ToString(),
                    Source = source
                };

                if (isDir && !rule.MatchDirectories) { entry.Result = "Skipped (files-only rule)"; trace.Add(entry); continue; }
                if (!isDir && !rule.MatchFiles) { entry.Result = "Skipped (dirs-only rule)"; trace.Add(entry); continue; }
                if (rule.Scope == SkiplistScope.InRace && !inRace) { entry.Result = "Skipped (in-race only)"; trace.Add(entry); continue; }
                if (rule.Section != null && !rule.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Result = $"Skipped (section={rule.Section}, current={section})";
                    trace.Add(entry);
                    continue;
                }

                var matched = !string.IsNullOrWhiteSpace(rule.Expression)
                    ? RuleExpressionEvaluator.Matches(rule.Expression, name, section, parsed)
                    : Matches(name, rule.Pattern, rule.IsRegex);

                if (matched)
                {
                    entry.IsMatch = true;
                    entry.Result = $"MATCHED → {rule.Action}";
                    trace.Add(entry);
                    if (finalAction == SkiplistAction.Allow) // First match wins
                        finalAction = rule.Action;
                    return; // Stop after first match (same as Evaluate)
                }

                entry.Result = "No match";
                trace.Add(entry);
            }
        }

        TraceRules(siteRules, "Site");
        if (finalAction == SkiplistAction.Allow)
            TraceRules(globalRules, "Global");

        return (finalAction, trace);
    }

    private SkiplistAction? EvaluateRules(string fileName, bool isDir, bool inRace,
        string? section, IReadOnlyList<SkiplistRule> rules, ParsedRelease? parsed)
    {
        foreach (var rule in rules)
        {
            if (isDir && !rule.MatchDirectories) continue;
            if (!isDir && !rule.MatchFiles) continue;
            if (rule.Scope == SkiplistScope.InRace && !inRace) continue;
            if (rule.Section != null && !rule.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;

            var matched = !string.IsNullOrWhiteSpace(rule.Expression)
                ? RuleExpressionEvaluator.Matches(rule.Expression, fileName, section, parsed)
                : Matches(fileName, rule.Pattern, rule.IsRegex);

            if (matched)
                return rule.Action;
        }
        return null;
    }

    private SkiplistAction? EvaluateRulesFiltered(string fileName, bool isDir, bool inRace,
        string? section, IReadOnlyList<SkiplistRule> rules, ParsedRelease? parsed, bool onlyDeny)
    {
        foreach (var rule in rules)
        {
            if (onlyDeny && rule.Action != SkiplistAction.Deny) continue;
            if (!onlyDeny && rule.Action == SkiplistAction.Deny) continue;
            if (isDir && !rule.MatchDirectories) continue;
            if (!isDir && !rule.MatchFiles) continue;
            if (rule.Scope == SkiplistScope.InRace && !inRace) continue;
            if (rule.Section != null && !rule.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;

            var matched = !string.IsNullOrWhiteSpace(rule.Expression)
                ? RuleExpressionEvaluator.Matches(rule.Expression, fileName, section, parsed)
                : Matches(fileName, rule.Pattern, rule.IsRegex);

            if (matched)
                return rule.Action;
        }
        return null;
    }

    private bool Matches(string fileName, string pattern, bool isRegex)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        try
        {
            var key = isRegex ? $"r:{pattern}" : $"g:{pattern}";
            var regex = _regexCache.GetOrAdd(key, _ =>
            {
                try
                {
                    var p = isRegex ? pattern : "^" + Regex.Escape(pattern)
                        .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Compiled,
                        MatchTimeout);
                }
                catch { return null; }
            });

            return regex?.IsMatch(fileName) ?? false;
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch { return false; }
    }
}
