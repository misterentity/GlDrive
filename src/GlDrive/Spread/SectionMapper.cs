using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Spread;

/// <summary>
/// Resolves IRC announce sections to site-specific remote sections using
/// RaceTrade-style SectionMappings. Each mapping has a trigger regex — the
/// first enabled mapping whose IrcSection matches AND whose TriggerRegex
/// matches the release name wins.
/// </summary>
public static class SectionMapper
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    public readonly record struct Resolution(
        string RemoteSection,
        IReadOnlyList<SkiplistRule> TagRules,
        SectionMapping? Mapping);

    /// <summary>
    /// Attempt to resolve a mapping for this site. Returns null if no mapping
    /// configured or no trigger matched — caller should fall back to fuzzy
    /// section matching (SpreadJob already does that).
    /// </summary>
    public static Resolution? Resolve(SiteSpreadConfig site, string ircSection, string releaseName)
    {
        if (site.SectionMappings.Count == 0) return null;

        foreach (var mapping in site.SectionMappings)
        {
            if (!mapping.Enabled) continue;
            if (!mapping.IrcSection.Equals(ircSection, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchesTrigger(mapping.TriggerRegex, releaseName)) continue;

            return new Resolution(mapping.RemoteSection, mapping.TagRules, mapping);
        }

        return null;
    }

    private static bool MatchesTrigger(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == ".*") return true;
        var regex = RegexCache.GetOrAdd(pattern, p =>
        {
            try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout); }
            catch { return null; }
        });
        try { return regex?.IsMatch(input) ?? false; }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
