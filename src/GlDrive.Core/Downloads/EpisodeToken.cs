using System.Globalization;
using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

/// <summary>
/// A season/episode token in a scene name: "S01" (season pack), "S01E05", or a multi-episode
/// "S01E01E02", "S01E01-E03" or "S01E01-03". Seasons and episodes take 1-3 ASCII digits, and the
/// token must stand alone between separators (". _ -" or space) or the ends of the name.
/// <para>
/// <see cref="SceneNameParser.Parse"/> and <see cref="ReleaseTraits"/> keep their own two-digit,
/// partly boundary-free patterns: widening them to three digits would turn names such as
/// "Mercedes.S600" into season packs. Dated daily episodes are <see cref="SceneNameParser.TryEpisodeDate"/>.
/// </para>
/// </summary>
/// <param name="Index">Position of the leading "S" in the name.</param>
/// <param name="Episodes">Distinct episodes in ascending order; empty for a season pack and for an
/// invalid range (backwards, or spanning more than 100 episodes).</param>
public sealed partial record EpisodeToken(int Index, int Length, int Season, int? FirstEpisode, IReadOnlyList<int> Episodes)
{
    private const int MaxRangeSpan = 100;

    public bool IsSeasonPack => FirstEpisode is null;

    /// <summary>Every token in the name, left to right.</summary>
    public static IReadOnlyList<EpisodeToken> FindAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var tokens = new List<EpisodeToken>();
        foreach (Match match in TokenRegex().Matches(name)) tokens.Add(FromMatch(match));
        return tokens;
    }

    /// <summary>The first token that names an episode, skipping season-pack tokens before it.</summary>
    public static EpisodeToken? FindEpisode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var match = TokenRegex().Match(name); match.Success; match = match.NextMatch())
            if (match.Groups["first"].Success) return FromMatch(match);
        return null;
    }

    /// <summary>
    /// Conservative pre-check for single-file library import: true when the name might cover more than
    /// one episode, including loose spellings the token grammar rejects ("S01E01.E02", "S01E01+02") and
    /// tokens glued to other text. A false positive only skips an import.
    /// </summary>
    public static bool MayCoverSeveralEpisodes(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return LooseMultiEpisodeRegex().IsMatch(name) || LooseEpisodeRegex().Count(name) > 1;
    }

    private static EpisodeToken FromMatch(Match match)
    {
        var token = match.Groups["token"];
        var season = Number(match.Groups["season"].Value);
        if (!match.Groups["first"].Success) return new(token.Index, token.Length, season, null, []);
        var first = Number(match.Groups["first"].Value);
        var episodes = new SortedSet<int> { first };
        var separators = match.Groups["sep"].Captures;
        var numbers = match.Groups["more"].Captures;
        var previous = first;
        for (var i = 0; i < numbers.Count; i++)
        {
            var next = Number(numbers[i].Value);
            if (separators[i].Value.StartsWith('-'))
            {
                if (next < previous || next - previous > MaxRangeSpan)
                    return new(token.Index, token.Length, season, first, []);
                for (var episode = previous; episode <= next; episode++) episodes.Add(episode);
            }
            else episodes.Add(next);
            previous = next;
        }
        return new(token.Index, token.Length, season, first, episodes.ToArray());
    }

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?:^|[._ -])(?<token>S(?<season>[0-9]{1,3})(?:E(?<first>[0-9]{1,3})(?:(?<sep>E|-E?)(?<more>[0-9]{1,3}))*)?)(?=[._ -]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"S\d{1,3}E\d{1,3}(?:[ ._-]*E\d|[-+]\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LooseMultiEpisodeRegex();

    [GeneratedRegex(@"S\d{1,3}E\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LooseEpisodeRegex();
}
