using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Downloads;

public sealed record WishlistQualityDecision(bool Accepted, int Score, bool MeetsCutoff,
    string Reason, string? ProfileId, IReadOnlyList<string> Keys);

/// <summary>Local identity and profile policy; it never lists or probes a source.</summary>
internal static class WishlistAcquisition
{
    private static readonly Regex Episode = new(@"(?:^|[._ -])S(?<season>\d{1,3})E(?<first>\d{1,3})(?<rest>(?:E\d{1,3}|-E?\d{1,3})*)(?=[._ -]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ExtraEpisodes = new(@"(?<range>-)?E?(?<number>\d{1,3})",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static ScoredQualityProfile? ResolveProfile(WishlistItem item, DownloadConfig config)
    {
        var profile = ResolveConfiguredProfile(item, config);
        if (profile == null || item.AllowLowQualitySources is not { } allow || profile.AllowLowQualitySources == allow) return profile;
        // Per-item source permission changes only this decision's source gate.
        // The shared profile and its resolution, size, score and upgrade choices stay intact.
        return new ScoredQualityProfile
        {
            Id = profile.Id, Name = profile.Name, AllowedResolutions = profile.AllowedResolutions,
            AllowUnknownResolution = profile.AllowUnknownResolution, AllowFullDisc = profile.AllowFullDisc,
            AllowLowQualitySources = allow, AllowUnknownSize = profile.AllowUnknownSize,
            MinimumBytes = profile.MinimumBytes, MaximumBytes = profile.MaximumBytes,
            MinimumScore = profile.MinimumScore, CutoffScore = profile.CutoffScore,
            AllowUpgrades = profile.AllowUpgrades, PreferProperRepack = profile.PreferProperRepack,
            UpgradeProperRepackAfterCutoff = profile.UpgradeProperRepackAfterCutoff, CustomFormats = profile.CustomFormats
        };
    }

    private static ScoredQualityProfile? ResolveConfiguredProfile(WishlistItem item, DownloadConfig config)
    {
        var id = item.ProfileId;
        if (string.IsNullOrWhiteSpace(id)) return ScoredQualityProfile.Legacy(item.Quality);
        var custom = config.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (custom != null) return custom;
        foreach (var quality in Enum.GetValues<QualityProfile>())
        {
            var legacy = ScoredQualityProfile.Legacy(quality);
            if (string.Equals(legacy.Id, id, StringComparison.OrdinalIgnoreCase)) return legacy;
            var recommended = ScoredQualityProfile.Recommended(quality);
            if (string.Equals(recommended.Id, id, StringComparison.OrdinalIgnoreCase)) return recommended;
        }
        return null;
    }

    internal static string Identity(WishlistItem item) => item.Type == MediaType.Movie && !string.IsNullOrWhiteSpace(item.ImdbId)
        ? "movie:imdb:" + item.ImdbId.ToLowerInvariant()
        : item.Type == MediaType.TvShow && item.TvMazeId.HasValue ? "tv:maze:" + item.TvMazeId.Value
        : $"{item.Type}:{Normalize(item.Title)}:{(item.Type == MediaType.Movie ? item.Year : null)}";

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static bool SameTitle(WishlistItem left, WishlistItem right)
    {
        if (left.Type != right.Type) return false;
        if (left.Type == MediaType.Movie && !string.IsNullOrWhiteSpace(left.ImdbId) && !string.IsNullOrWhiteSpace(right.ImdbId))
            return string.Equals(left.ImdbId, right.ImdbId, StringComparison.OrdinalIgnoreCase);
        if (left.Type == MediaType.TvShow && left.TvMazeId.HasValue && right.TvMazeId.HasValue)
            return left.TvMazeId == right.TvMazeId;
        return Normalize(left.Title) == Normalize(right.Title) &&
            (left.Type == MediaType.TvShow || !left.Year.HasValue || !right.Year.HasValue || left.Year == right.Year);
    }

    internal static string CurrentKey(WishlistItem item, WishlistCopy copy)
    {
        if (item.Type == MediaType.Movie) return Identity(item);
        var separator = copy.Key.LastIndexOf(':');
        return separator >= 0 ? Identity(item) + copy.Key[separator..] : "";
    }

    internal static IReadOnlyList<string> Keys(WishlistItem item, string release, ParsedRelease? parsed = null)
    {
        if (string.IsNullOrWhiteSpace(release) || release.Length > 1024) return [];
        var identity = Identity(item);
        if (item.Type == MediaType.Movie) return [identity];
        var match = Episode.Match(release);
        if (match.Success)
        {
            var season = int.Parse(match.Groups["season"].Value);
            var first = int.Parse(match.Groups["first"].Value);
            var episodes = new HashSet<int> { first };
            var previous = first;
            foreach (Match extra in ExtraEpisodes.Matches(match.Groups["rest"].Value))
            {
                var next = int.Parse(extra.Groups["number"].Value);
                if (extra.Groups["range"].Success)
                {
                    if (next < previous || next - previous > 100) return [];
                    for (var episode = previous; episode <= next; episode++) episodes.Add(episode);
                }
                else episodes.Add(next);
                previous = next;
            }
            return episodes.Order().Select(e => $"{identity}:S{season:D2}E{e:D2}").ToArray();
        }
        parsed ??= SceneNameParser.Parse(release);
        if (parsed.Season is { } pack) return [$"{identity}:S{pack:D2}"];
        if (SceneNameParser.TryEpisodeDate(release, out var date)) return [$"{identity}:{date:yyyy-MM-dd}"];
        return [];
    }

    internal static bool MatchesTitle(WishlistItem item, string release)
        => MatchesTitle(item, release, SceneNameParser.Parse(release));

    internal static bool MatchesTitle(WishlistItem item, string release, ParsedRelease parsed)
    {
        if (item.Type == MediaType.Movie)
            return SceneNameParser.MatchesMovie(parsed, item.Title, item.Year, QualityProfile.Any);
        if (SceneNameParser.MatchesTvEpisode(parsed, item.Title, QualityProfile.Any)) return true;
        // The legacy parser retains Sxx in a season-pack title. Do not let that
        // or a date token become part of the series identity.
        if (SceneNameParser.LogicalCategorySeason(release) != null)
            return Normalize(SceneNameParser.LogicalCategoryTitle(release, parsed)) == Normalize(item.Title);
        if (SceneNameParser.TryEpisodeDate(release, out var date))
        {
            var normalized = release.Replace('_', '.').Replace(' ', '.');
            var index = normalized.IndexOf(date.ToString("yyyy.MM.dd"), StringComparison.Ordinal);
            return index > 0 && Normalize(normalized[..index]) == Normalize(item.Title);
        }
        return false;
    }

    // A season pack overlaps its episode keys. Without a series inventory, taking
    // another whole season is deliberately conservative; gap filling is separate.
    internal static bool Overlaps(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || IsPackPrefix(left, right) || IsPackPrefix(right, left);
    private static bool IsPackPrefix(string pack, string episode)
    {
        var suffix = pack[(pack.LastIndexOf(':') + 1)..];
        return suffix.Length >= 3 && suffix[0] == 'S' && suffix[1..].All(char.IsDigit)
            && episode.StartsWith(pack + "E", StringComparison.OrdinalIgnoreCase);
    }
}
