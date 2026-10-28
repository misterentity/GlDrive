using System.Text.RegularExpressions;
using System.Globalization;

namespace GlDrive.Downloads;

public static partial class SceneNameParser
{
    private static readonly Regex SeasonEpisodeRegex = MySeasonEpisodeRegex();
    private static readonly Regex SeasonPackRegex = MySeasonPackRegex();
    private static readonly Regex YearRegex = MyYearRegex();
    private static readonly Regex GroupRegex = MyGroupRegex();

    private static readonly string[] CommonTags = ["BluRay", "BDRip", "WEB-DL", "WEBRip", "HDTV", "DVDRip", "PROPER", "REPACK", "REMUX"];
    private static readonly string[] SourceTokens = ["BluRay", "BDRip", "BRRip", "WEB-DL", "WEBRip", "WEB", "HDTV", "PDTV", "DVDRip", "DVD", "HDRip", "CAM", "TS", "TC", "SCREENER", "DVDScr", "REMUX"];
    private static readonly Regex NonAlphanumRegex = new(@"[^a-z0-9\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

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

        // Extract source (first token match wins — ordered longest-first)
        string? source = null;
        foreach (var token in SourceTokens)
        {
            if (Regex.IsMatch(name, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
            {
                source = token;
                break;
            }
        }

        // Extract title: everything before the first recognized token
        var title = ExtractTitle(name, year, season, seMatch, yearMatch);

        return new ParsedRelease(title, year, season, episode, quality, group, isSeasonPack, source);
    }

    /// <summary>Conservative media classes for download routing. A year or bare WEB is not video evidence.</summary>
    public static string? ClassifyCategory(string releaseName)
    {
        var parsed = Parse(releaseName);
        // Platform/installer tokens describe software even when its name advertises a resolution.
        if (SoftwarePlatformRegex().IsMatch(releaseName)) return null;
        if (CategoryTvRegex().IsMatch(releaseName)) return "TV";
        var video = VideoTokenRegex().IsMatch(releaseName) || parsed.Source is
            "BluRay" or "BDRip" or "BRRip" or "WEB-DL" or "WEBRip" or "HDTV" or "PDTV" or
            "DVDRip" or "HDRip" or "CAM" or "SCREENER" or "DVDScr" or "REMUX";
        if (!video) return null;
        return TryEpisodeDate(releaseName, out _) ? "TV" : "Movies";
    }

    public static bool TryEpisodeDate(string releaseName, out DateOnly date)
    {
        var match = EpisodeDateRegex().Match(releaseName);
        date = default;
        return match.Success && DateOnly.TryParseExact(
            $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}",
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Title for a newly selected logical media root; legacy exact-section layouts stay unchanged.</summary>
    public static string LogicalCategoryTitle(string releaseName)
    {
        var parsed = Parse(releaseName);
        var season = CategoryTvRegex().Match(releaseName);
        var normalized = releaseName.Replace('.', ' ').Replace('_', ' ');
        return season.Success && season.Index < parsed.Title.Length ? normalized[..season.Index].Trim() : parsed.Title;
    }

    public static int? LogicalCategorySeason(string releaseName)
    {
        var match = CategoryTvRegex().Match(releaseName);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
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
        foreach (var tag in CommonTags)
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
        normalized = NonAlphanumRegex.Replace(normalized, "");
        normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();
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

    [GeneratedRegex(@"(?:^|[. _-])S(\d{1,2})(?:E\d{1,3})*(?=$|[. _-])", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryTvRegex();

    [GeneratedRegex(@"(?:^|[. _-])((?:19|20)\d{2})[. _-](\d{2})[. _-](\d{2})(?=$|[. _-])")]
    private static partial Regex EpisodeDateRegex();

    [GeneratedRegex(@"(?:^|[. _-])(?:2160p|1080[pi]|720p|480p|576p|SDTV|4K|UHD|x26[45]|h[ .]?26[45]|HEVC|AVC|XviD|DivX|AV1)(?=$|[. _-])", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTokenRegex();

    [GeneratedRegex(@"(?:^|[. _-])(?:WIN32|WIN64|x86|x64|MACOS|LINUX|ANDROID|KEYGEN|CRACKED)(?=$|[. _-])", RegexOptions.IgnoreCase)]
    private static partial Regex SoftwarePlatformRegex();
}
