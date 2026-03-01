using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

public static partial class SceneNameParser
{
    private static readonly Regex SeasonEpisodeRegex = MySeasonEpisodeRegex();
    private static readonly Regex SeasonPackRegex = MySeasonPackRegex();
    private static readonly Regex YearRegex = MyYearRegex();
    private static readonly Regex GroupRegex = MyGroupRegex();

    private static readonly Dictionary<string, QualityProfile> QualityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2160p"] = QualityProfile.Q2160p,
        ["4k"] = QualityProfile.Q2160p,
        ["uhd"] = QualityProfile.Q2160p,
        ["1080p"] = QualityProfile.Q1080p,
        ["1080i"] = QualityProfile.Q1080p,
        ["720p"] = QualityProfile.Q720p,
        ["480p"] = QualityProfile.SD,
        ["576p"] = QualityProfile.SD,
        ["sdtv"] = QualityProfile.SD,
    };

    public static ParsedRelease Parse(string releaseName)
    {
        var name = releaseName.Replace('.', ' ').Replace('_', ' ');

        // Extract group (after last hyphen)
        string? group = null;
        var groupMatch = GroupRegex.Match(releaseName);
        if (groupMatch.Success)
            group = groupMatch.Groups[1].Value;

        // Extract season/episode
        int? season = null, episode = null;
        bool isSeasonPack = false;
        var seMatch = SeasonEpisodeRegex.Match(name);
        if (seMatch.Success)
        {
            season = int.Parse(seMatch.Groups[1].Value);
            episode = int.Parse(seMatch.Groups[2].Value);
        }
        else
        {
            var spMatch = SeasonPackRegex.Match(name);
            if (spMatch.Success)
            {
                season = int.Parse(spMatch.Groups[1].Value);
                isSeasonPack = true;
            }
        }

        // Extract year
        int? year = null;
        var yearMatch = YearRegex.Match(name);
        if (yearMatch.Success)
            year = int.Parse(yearMatch.Groups[1].Value);

        // Extract quality
        var quality = QualityProfile.Any;
        foreach (var (token, q) in QualityTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                quality = q;
                break;
            }
        }

        // Extract title: everything before the first recognized token
        var title = ExtractTitle(name, year, season, seMatch, yearMatch);

        return new ParsedRelease(title, year, season, episode, quality, group, isSeasonPack);
    }

    public static bool MatchesMovie(string releaseName, string title, int? year, QualityProfile quality)
    {
        var parsed = Parse(releaseName);
        if (parsed.Season != null || parsed.Episode != null) return false;

        if (!TitlesMatch(parsed.Title, title)) return false;
        if (year.HasValue && parsed.Year.HasValue && parsed.Year != year) return false;
        if (quality != QualityProfile.Any && parsed.Quality != QualityProfile.Any && parsed.Quality != quality) return false;

        return true;
    }

    public static bool MatchesTvEpisode(string releaseName, string showTitle, QualityProfile quality)
    {
        var parsed = Parse(releaseName);
        if (parsed.Season == null) return false;

        if (!TitlesMatch(parsed.Title, showTitle)) return false;
        if (quality != QualityProfile.Any && parsed.Quality != QualityProfile.Any && parsed.Quality != quality) return false;

        return true;
    }

    private static string ExtractTitle(string name, int? year, int? season, Match seMatch, Match yearMatch)
    {
        int cutoff = name.Length;

        if (seMatch.Success && seMatch.Index < cutoff)
            cutoff = seMatch.Index;
        if (yearMatch.Success && yearMatch.Index < cutoff)
            cutoff = yearMatch.Index;

        // Also cut at known quality tokens
        foreach (var token in QualityTokens.Keys)
        {
            var idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutoff)
                cutoff = idx;
        }

        // Cut at common tags
        foreach (var tag in new[] { "BluRay", "BDRip", "WEB-DL", "WEBRip", "HDTV", "DVDRip", "PROPER", "REPACK", "REMUX" })
        {
            var idx = name.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutoff)
                cutoff = idx;
        }

        return name[..cutoff].Trim();
    }

    private static bool TitlesMatch(string parsedTitle, string searchTitle)
    {
        var a = NormalizeTitle(parsedTitle);
        var b = NormalizeTitle(searchTitle);
        return a == b;
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = title.ToLowerInvariant()
            .Replace("&", "and");
        // Remove articles at start
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (normalized.StartsWith(article))
            {
                normalized = normalized[article.Length..];
                break;
            }
        }
        // Keep only alphanumeric and spaces, collapse spaces
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", "");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex MySeasonEpisodeRegex();

    [GeneratedRegex(@"S(\d{1,2})(?:\s|$|\.)", RegexOptions.IgnoreCase)]
    private static partial Regex MySeasonPackRegex();

    [GeneratedRegex(@"(?:^|\s)(\d{4})(?:\s|$)")]
    private static partial Regex MyYearRegex();

    [GeneratedRegex(@"-([A-Za-z0-9]+)$")]
    private static partial Regex MyGroupRegex();
}
