using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Irc;

namespace GlDrive.Spread;

internal sealed record AnnouncePatternMatch(string Section, string Release, bool AutoRace,
    string RuleSource, bool Plausible);

/// <summary>Shared parsing only: no telemetry, deduplication, network or configuration writes.</summary>
internal sealed class AnnounceMatcher
{
    private readonly IReadOnlyList<IrcAnnounceRule> _rules;
    private readonly bool _defaultAutoRace;
    private readonly Dictionary<string, Regex> _compiled = new();
    private readonly List<string> _invalidPatterns = [];
    internal int CompiledCount => _compiled.Count;
    internal IReadOnlyList<string> InvalidPatterns => _invalidPatterns;

    private static readonly Regex VerbosePattern = new(
        @"\[ NEW \] in \[ (?<section>[^\]]+?) \] (?<release>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex RemovalPattern = new(
        @"^\s*(?:\[\s*(?:DELETE|DELETED|DELDIR|RMDIR|DELPRE|NUKE|NUKED)\s*\]|(?:DELETE|DELETED|DELDIR|RMDIR|DELPRE|NUKE|NUKED)\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(200));
    private static readonly HashSet<string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "brings", "leads", "racing", "wins", "complete", "completed", "incomplete",
        "pred", "nuke", "nuked", "unnuke", "unnuked", "delpre", "request", "filled",
        "the", "from", "for", "with", "and", "now", "new"
    };

    private static readonly char[] InvalidReleaseNameChars = Path.GetInvalidFileNameChars();

    internal AnnounceMatcher(IEnumerable<IrcAnnounceRule> rules, bool defaultAutoRace, CancellationToken cancellationToken = default)
    {
        _rules = rules as IReadOnlyList<IrcAnnounceRule> ?? rules.ToArray();
        _defaultAutoRace = defaultAutoRace;
        foreach (var rule in _rules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Pattern)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _compiled[rule.Channel + "|" + rule.Pattern] = new Regex(rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException) { _invalidPatterns.Add(rule.Pattern); }
        }
    }

    internal static bool IsRemoval(string text) => RemovalPattern.IsMatch(text);

    internal static bool IsPlausibleReleaseName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var value = candidate.Trim();
        if (value.Length < 10 || KnownVerbs.Contains(value)) return false;
        // A release is a single directory name ending in its group tag: a path ("TV/Name" from
        // an AUTONUKE notice) or a word glued to prose punctuation ("Frankenstein,") is not.
        if (value.IndexOfAny(InvalidReleaseNameChars) >= 0) return false;
        if (!(char.IsAsciiLetterOrDigit(value[^1]) || value[^1] is ')' or ']')) return false;
        return value.Any(c => c is '.' or '_' or >= '0' and <= '9' or >= 'A' and <= 'Z');
    }

    // Replay can inspect all captures, including implausible names, for rule breadth.
    internal IReadOnlyList<AnnouncePatternMatch> Match(string channel, string text, CancellationToken cancellationToken = default)
        => Enumerate(channel, text, cancellationToken).ToArray();

    // The live listener stops after an accepted candidate. Lazy matching preserves
    // that behavior and avoids running later regexes after a built-in match succeeds.
    internal IEnumerable<AnnouncePatternMatch> Enumerate(string channel, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IrcLogStore.IsChannelName(channel) || IsRemoval(text)) yield break;
        var builtin = VerbosePattern.Match(text);
        if (builtin.Success)
        {
            var release = builtin.Groups["release"].Value;
            var matchingRule = _rules.FirstOrDefault(r => r.Enabled &&
                (string.IsNullOrEmpty(r.Channel) || channel.Equals(r.Channel, StringComparison.OrdinalIgnoreCase)));
            if (release.Length > 0)
                yield return new(builtin.Groups["section"].Value.Trim(), release,
                    matchingRule?.AutoRace ?? _defaultAutoRace, "builtin", IsPlausibleReleaseName(release));
        }
        foreach (var rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rule.Enabled || (!string.IsNullOrEmpty(rule.Channel) &&
                !channel.Equals(rule.Channel, StringComparison.OrdinalIgnoreCase))) continue;
            if (!_compiled.TryGetValue(rule.Channel + "|" + rule.Pattern, out var regex)) continue;
            Match match;
            try { match = regex.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success || !match.Groups["release"].Success) continue;
            var release = match.Groups["release"].Value;
            if (release.Length == 0) continue;
            yield return new(match.Groups["section"].Success ? match.Groups["section"].Value.Trim() : "",
                release, rule.AutoRace, "custom", IsPlausibleReleaseName(release));
        }
    }
}
