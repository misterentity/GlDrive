using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

/// <summary>What the user typed into Collection gaps: a TMDB id, an IMDb id, a TMDB/IMDb link,
/// or a title with an optional year ("Dune 2021", "Dune (2021)").</summary>
public sealed record CollectionQuery(string Kind, int? TmdbId = null, string? ImdbId = null, string? Title = null, int? Year = null, string? Raw = null)
{
    public static CollectionQuery? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;
        if (int.TryParse(value, out var id)) return id > 0 ? new("tmdb", TmdbId: id) : null;
        var tmdbLink = Regex.Match(value, @"themoviedb\.org/movie/(\d+)", RegexOptions.IgnoreCase);
        if (tmdbLink.Success && int.TryParse(tmdbLink.Groups[1].Value, out var linked) && linked > 0) return new("tmdb", TmdbId: linked);
        var imdb = Regex.Match(value, @"(?<![a-z0-9])(tt\d{5,10})(?![0-9])", RegexOptions.IgnoreCase);
        if (imdb.Success) return new("imdb", ImdbId: imdb.Groups[1].Value.ToLowerInvariant());
        // Scene-style "The.Matrix.1999" searches as "The Matrix 1999".
        if (!value.Contains(' ') && value.Contains('.')) value = value.Replace('.', ' ').Trim();
        var titled = Regex.Match(value, @"^(?<title>.+?)[\s.]*\(?(?<year>(?:19|20)\d{2})\)?$");
        if (titled.Success && titled.Groups["title"].Value.Trim().Length > 0)
            return new("title", Title: titled.Groups["title"].Value.Trim(' ', '.', '-', ':'), Year: int.Parse(titled.Groups["year"].Value), Raw: value);
        return new("title", Title: value, Raw: value);
    }

    /// <summary>The searches to run: the title without its year, and the text as typed — a
    /// trailing number can be part of the title ("Blade Runner 2049").</summary>
    public IEnumerable<string> SearchTexts()
    {
        if (!string.IsNullOrWhiteSpace(Title)) yield return Title;
        if (!string.IsNullOrWhiteSpace(Raw) && Raw != Title) yield return Raw;
    }

    /// <summary>Movie results best first: an exact title (with its year), then TMDB's order.</summary>
    public IReadOnlyList<TmdbSearchResult> Rank(IEnumerable<TmdbSearchResult> results)
    {
        var title = Normalize(Title);
        var raw = Normalize(Raw);
        return results.Where(r => r.Id > 0 && r.MediaType is "movie" or "").DistinctBy(r => r.Id)
            .Select((r, index) =>
            {
                var name = Normalize(r.DisplayTitle);
                var score = name == title && Year != null && r.YearParsed == Year ? 4
                    : raw.Length > 0 && name == raw ? 3
                    : name == title ? 2
                    : Year != null && r.YearParsed == Year ? 1 : 0;
                return (r, index, score);
            })
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .Select(x => x.r).Take(10).ToArray();
    }

    private static string Normalize(string? title) =>
        new((title ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
