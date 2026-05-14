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

    /// <summary>
    /// True if the site has any way to map a destination path for this IRC announce
    /// section — either an explicit SectionMapping, an exact Sections key (case-
    /// insensitive), or a fuzzy / substring match against Sections keys. Matches
    /// the resolution order used by SpreadJob.RunAsync when picking destinations.
    ///
    /// Used by SpreadManager.TryAutoRace to drop auto-race participants up front
    /// for sites that can't possibly host the announce's category, avoiding the
    /// "Need 2+ servers — 1 unmapped" failures that dominated 2026-05-13/14 logs.
    /// </summary>
    public static bool HasSectionFor(SiteSpreadConfig site, string ircSection)
    {
        if (string.IsNullOrWhiteSpace(ircSection)) return false;

        foreach (var m in site.SectionMappings)
        {
            if (m.Enabled && m.IrcSection.Equals(ircSection, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var key in site.Sections.Keys)
        {
            if (key.Equals(ircSection, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var norm = NormalizeKey(ircSection);
        foreach (var key in site.Sections.Keys)
        {
            var nk = NormalizeKey(key);
            if (nk == norm) return true;
            if (nk.Length > 0 && norm.Length > 0 && (nk.Contains(norm) || norm.Contains(nk)))
                return true;
        }

        return false;
    }

    private static string NormalizeKey(string s) =>
        s.ToLowerInvariant().Replace("-", "").Replace("_", "");
}
