using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Spread;

public class SkiplistEvaluator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new();

    public SkiplistAction Evaluate(string fileName, bool isDir, bool inRace,
        string serverId, string? section,
        IReadOnlyList<SkiplistRule> siteRules,
        IReadOnlyList<SkiplistRule> globalRules)
    {
        var result = EvaluateRules(fileName, isDir, inRace, section, siteRules);
        if (result.HasValue) return result.Value;

        result = EvaluateRules(fileName, isDir, inRace, section, globalRules);
        if (result.HasValue) return result.Value;

        return SkiplistAction.Allow;
    }

    private SkiplistAction? EvaluateRules(string fileName, bool isDir, bool inRace,
        string? section, IReadOnlyList<SkiplistRule> rules)
    {
        foreach (var rule in rules)
        {
            if (isDir && !rule.MatchDirectories) continue;
            if (!isDir && !rule.MatchFiles) continue;
            if (rule.Scope == SkiplistScope.InRace && !inRace) continue;
            if (rule.Section != null && rule.Section != section) continue;

            if (Matches(fileName, rule.Pattern, rule.IsRegex))
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
