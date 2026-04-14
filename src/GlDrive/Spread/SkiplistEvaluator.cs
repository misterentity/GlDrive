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
