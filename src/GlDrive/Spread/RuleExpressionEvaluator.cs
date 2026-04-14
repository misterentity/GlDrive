using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GlDrive.Downloads;

namespace GlDrive.Spread;

/// <summary>
/// RaceTrade-style rule expression evaluator.
/// Format: [key] operator value
///   keys:      release, section, group, year, quality, source, title, season, episode
///   operators: ==, !=, iswm, matches, contains, startswith, endswith, isin
///   value:     free text, possibly comma/pipe-separated list for isin
///
/// Examples:
///   [release] contains INTERNAL
///   [group]   isin BadGroup1,BadGroup2
///   [release] matches (?i)\bGERMAN\b
///   [quality] == 1080p
///   [year]    != 2024
/// </summary>
public static class RuleExpressionEvaluator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    // [key] op value — whitespace between parts
    private static readonly Regex ExpressionRegex = new(
        @"^\s*\[(?<key>[A-Za-z]+)\]\s+(?<op>==|!=|iswm|matches|contains|startswith|endswith|isin)\s+(?<val>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string expression, out ParsedExpression parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var m = ExpressionRegex.Match(expression);
        if (!m.Success) return false;
        parsed = new ParsedExpression(
            Key: m.Groups["key"].Value.ToLowerInvariant(),
            Operator: m.Groups["op"].Value.ToLowerInvariant(),
            Value: m.Groups["val"].Value.Trim());
        return true;
    }

    public static bool Matches(string expression, string releaseName, string? section, ParsedRelease? parsed)
    {
        if (!TryParse(expression, out var expr)) return false;

        var left = ExtractKey(expr.Key, releaseName, section, parsed);
        if (left is null) return false;

        return Compare(left, expr.Operator, expr.Value);
    }

    private static string? ExtractKey(string key, string releaseName, string? section, ParsedRelease? p)
    {
        return key switch
        {
            "release" => releaseName,
            "section" => section,
            "group"   => p?.Group,
            "year"    => p?.Year?.ToString(),
            "quality" => p?.Quality switch
            {
                QualityProfile.Q2160p => "2160p",
                QualityProfile.Q1080p => "1080p",
                QualityProfile.Q720p  => "720p",
                QualityProfile.SD     => "sd",
                _                     => null
            },
            "source"  => p?.Source,
            "title"   => p?.Title,
            "season"  => p?.Season?.ToString(),
            "episode" => p?.Episode?.ToString(),
            _         => null
        };
    }

    private static bool Compare(string left, string op, string right)
    {
        switch (op)
        {
            case "==":
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            case "!=":
                return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            case "contains":
                return left.Contains(right, StringComparison.OrdinalIgnoreCase);
            case "startswith":
                return left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
            case "endswith":
                return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
            case "isin":
                foreach (var item in right.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (string.Equals(left, item, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            case "iswm":
                return WildcardMatch(left, right);
            case "matches":
                return RegexMatch(left, right);
            default:
                return false;
        }
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var key = "w:" + pattern;
        var regex = RegexCache.GetOrAdd(key, _ =>
        {
            try
            {
                var escaped = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return new Regex(escaped, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
            }
            catch { return null; }
        });
        try { return regex?.IsMatch(input) ?? false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool RegexMatch(string input, string pattern)
    {
        var key = "r:" + pattern;
        var regex = RegexCache.GetOrAdd(key, _ =>
        {
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
            }
            catch { return null; }
        });
        try { return regex?.IsMatch(input) ?? false; }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

public readonly record struct ParsedExpression(string Key, string Operator, string Value);
