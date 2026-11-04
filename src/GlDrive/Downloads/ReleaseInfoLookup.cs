using System.Text;
using GlDrive.Library;

namespace GlDrive.Downloads;

public sealed record ReleaseInfo(
    string Release,
    ParsedRelease Parsed,
    bool IsTv,
    string Status,
    int? TmdbId = null,
    string? Title = null,
    int? Year = null,
    string? Overview = null,
    double? Rating = null,
    string? Genres = null,
    int? RuntimeMinutes = null,
    string? PosterUrl = null,
    string? ImdbId = null,
    string? ShowStatus = null,
    string? EpisodeTitle = null,
    string? EpisodeAirDate = null,
    string? EpisodeOverview = null)
{
    public string? TmdbUrl => TmdbId is { } id ? $"https://www.themoviedb.org/{(IsTv ? "tv" : "movie")}/{id}" : null;
    public string? ImdbUrl => string.IsNullOrWhiteSpace(ImdbId) ? null : $"https://www.imdb.com/title/{ImdbId}/";
}

/// <summary>
/// Looks up what a scene release is (movie or TV, with the episode when the name has one) on
/// TMDB. One search plus one or two detail calls, only when the user asks.
/// </summary>
public static class ReleaseInfoLookup
{
    public static async Task<ReleaseInfo> LookupAsync(string release, TmdbClient tmdb, CancellationToken ct)
    {
        var parsed = SceneNameParser.Parse(release);
        var isTv = parsed.Season.HasValue || parsed.Episode.HasValue || parsed.IsSeasonPack
                   || SceneNameParser.TryEpisodeDate(release, out _);
        if (!tmdb.HasApiKey) return new(release, parsed, isTv, "no-key");
        if (string.IsNullOrWhiteSpace(parsed.Title)) return new(release, parsed, isTv, "not-found");

        TmdbSearchResult? match = null;
        foreach (var query in SearchQueries(parsed.Title))
        {
            match = PickMatch(await tmdb.SearchMulti(query, ct), parsed with { Title = query }, isTv);
            ct.ThrowIfCancellationRequested();
            if (match != null) break;
        }
        if (match == null) return new(release, parsed, isTv, "not-found");
        isTv = match.MediaType == "tv";

        if (!isTv)
        {
            var movie = await tmdb.GetMovieDetail(match.Id, ct);
            return new(release, parsed, false, "found", match.Id,
                movie?.Title ?? match.DisplayTitle, match.YearParsed,
                Blank(movie?.Overview) ?? Blank(match.Overview), Rating(movie?.VoteAverage ?? match.VoteAverage),
                Blank(movie?.GenreText), movie?.Runtime is > 0 ? movie.Runtime : null,
                movie?.PosterUrl ?? match.PosterUrl, Blank(movie?.ImdbId));
        }

        var show = await tmdb.GetTvDetail(match.Id, ct);
        TmdbEpisode? episode = null;
        if (parsed is { Season: { } season, Episode: { } number })
            episode = (await tmdb.GetTvSeason(match.Id, season, ct))?.Episodes?.FirstOrDefault(e => e.EpisodeNumber == number);
        return new(release, parsed, true, "found", match.Id,
            Blank(show?.Name) ?? match.DisplayTitle, match.YearParsed,
            Blank(show?.Overview) ?? Blank(match.Overview), Rating(show?.VoteAverage ?? match.VoteAverage),
            show?.Genres is { Length: > 0 } g ? string.Join(", ", g.Select(x => x.Name)) : null, null,
            match.PosterUrl, null, Blank(show?.Status),
            Blank(episode?.Name), Blank(episode?.AirDate), Blank(episode?.Overview));
    }

    /// <summary>
    /// The result that is this release: the right kind (an episode name is never a movie), then
    /// an exact title, then the release year. Falls back to TMDB's own ranking within the kind.
    /// </summary>
    internal static TmdbSearchResult? PickMatch(IReadOnlyList<TmdbSearchResult> results, ParsedRelease parsed, bool isTv)
    {
        var wantedKind = isTv ? "tv" : "movie";
        var title = Normalize(parsed.Title);
        TmdbSearchResult? Best(IEnumerable<TmdbSearchResult> pool) => pool
            .Select((r, index) => (r, index, score:
                (Normalize(r.DisplayTitle) == title ? 2 : 0) + (parsed.Year is { } y && r.YearParsed == y ? 1 : 0)))
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .Select(x => x.r).FirstOrDefault();
        var usable = results.Where(r => r.Id > 0).ToList();
        return Best(usable.Where(r => r.MediaType == wantedKind))
               // The parser can misread a kind (a dated talk show, a movie with a number); accept
               // the other kind only on an exact title, never a loose cross-kind hit.
               ?? Best(usable.Where(r => r.MediaType != wantedKind && Normalize(r.DisplayTitle) == title));
    }

    /// <summary>The parsed title, then without a scene country tag (MasterChef.US → MasterChef).</summary>
    internal static IEnumerable<string> SearchQueries(string title)
    {
        var trimmed = title.Trim();
        yield return trimmed;
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[^1].ToUpperInvariant() is "US" or "UK" or "AU" or "NZ" or "CA" or "IE")
            yield return string.Join(' ', words[..^1]);
    }

    /// <summary>Where the release's movie or episode already is: Plex, disk, both, or unknown.</summary>
    public static string LibraryStatus(LibraryIndexSnapshot index, ReleaseInfo info)
    {
        if (index.RefreshedAtUtc == DateTime.MinValue) return "Library not indexed yet (refresh it from Collection gaps).";
        var title = info.Title ?? info.Parsed.Title;
        var year = info.IsTv ? null : info.Year ?? info.Parsed.Year;
        bool Matches(LibraryIndexEntry e)
        {
            if (info.TmdbId is { } id && !info.IsTv && e.Guids.Contains($"tmdb://{id}", StringComparer.OrdinalIgnoreCase)) return true;
            if (Normalize(e.Identity.Title) != Normalize(title) && Normalize(e.Identity.Title) != Normalize(info.Parsed.Title)) return false;
            if (info.IsTv)
                return e.Identity.Kind == "episode" && (info.Parsed.Season == null || e.Identity.Season == info.Parsed.Season)
                       && (info.Parsed.Episode == null || e.Identity.Episode == info.Parsed.Episode);
            return e.Identity.Kind == "movie" && (year == null || e.Identity.Year == null || e.Identity.Year == year);
        }
        var matches = index.Entries.Where(Matches).ToArray();
        var what = info.IsTv ? info.Parsed.Episode != null ? "This episode" : info.Parsed.Season != null ? "This season" : "This show" : "This movie";
        if (matches.Length == 0)
            return index.Complete || index.PlexComplete ? $"{what} is not in your library." : $"{what} was not found in the part of your library that was read.";
        var plex = matches.Any(e => e.PlexRatingKey != null);
        var disk = matches.Any(e => e.VerifiedOnDisk);
        var best = (QualityProfile)matches.Max(e => (int)e.Identity.Quality);
        var where = plex && disk ? "in Plex and on disk" : plex ? "in Plex" : "on disk";
        return $"{what} is already {where}{(best == QualityProfile.Any ? "" : $" ({QualityLabel(best)})")}.";
    }

    public static string QualityLabel(QualityProfile quality) => quality switch
    {
        QualityProfile.Q2160p => "2160p",
        QualityProfile.Q1080p => "1080p",
        QualityProfile.Q720p => "720p",
        QualityProfile.SD => "SD",
        _ => "unknown quality"
    };

    private static double? Rating(double value) => value > 0 ? Math.Round(value, 1) : null;
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string? title) => new((title ?? "").Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
