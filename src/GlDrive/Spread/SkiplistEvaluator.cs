using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Spread;

public class SkiplistEvaluator
{
    public SkiplistAction Evaluate(string fileName, bool isDir, bool inRace,
        string serverId, string? section,
        IReadOnlyList<SkiplistRule> siteRules,
        IReadOnlyList<SkiplistRule> globalRules)
    {
        // Site rules first, then global. First match wins.
        var result = EvaluateRules(fileName, isDir, inRace, section, siteRules);
        if (result.HasValue) return result.Value;

        result = EvaluateRules(fileName, isDir, inRace, section, globalRules);
        if (result.HasValue) return result.Value;

        return SkiplistAction.Allow;
    }

    private static SkiplistAction? EvaluateRules(string fileName, bool isDir, bool inRace,
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

    private static bool Matches(string fileName, string pattern, bool isRegex)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        if (isRegex)
        {
            try
            {
                return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Glob-style matching: * matches anything, ? matches single char
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
