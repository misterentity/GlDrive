using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class CollectionQueryTests
{
    [Theory]
    [InlineData("603", "tmdb", 603, null, null, null)]
    [InlineData("https://www.themoviedb.org/movie/603-the-matrix", "tmdb", 603, null, null, null)]
    [InlineData("tt0133093", "imdb", null, "tt0133093", null, null)]
    [InlineData("https://www.imdb.com/title/tt0133093/", "imdb", null, "tt0133093", null, null)]
    [InlineData("The Matrix", "title", null, null, "The Matrix", null)]
    [InlineData("The Matrix 1999", "title", null, null, "The Matrix", 1999)]
    [InlineData("The Matrix (1999)", "title", null, null, "The Matrix", 1999)]
    [InlineData("The.Matrix.1999", "title", null, null, "The Matrix", 1999)]
    public void Parses_what_the_user_typed(string text, string kind, int? tmdb, string? imdb, string? title, int? year)
    {
        var q = CollectionQuery.Parse(text)!;
        Assert.Equal(kind, q.Kind);
        Assert.Equal(tmdb, q.TmdbId);
        Assert.Equal(imdb, q.ImdbId);
        Assert.Equal(title, q.Title);
        Assert.Equal(year, q.Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    public void Empty_or_zero_is_not_a_query(string text) => Assert.Null(CollectionQuery.Parse(text));

    private static TmdbSearchResult Movie(int id, string title, string date, string type = "movie") =>
        new() { Id = id, Title = type == "movie" ? title : "", Name = type == "tv" ? title : "", MediaType = type, ReleaseDate = date, FirstAirDate = date };

    [Fact]
    public void Exact_title_and_year_ranks_first_and_tv_is_dropped()
    {
        var q = CollectionQuery.Parse("Dune 1984")!;
        var ranked = q.Rank([Movie(1, "Dune: Part Two", "2024"), Movie(2, "Dune", "2021"), Movie(3, "Dune", "1984-12-14"), Movie(4, "Dune", "2000", "tv")]);
        Assert.Equal([3, 2, 1], ranked.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void A_trailing_number_that_is_part_of_the_title_still_matches()
    {
        var q = CollectionQuery.Parse("Blade Runner 2049")!;
        Assert.Equal(["Blade Runner", "Blade Runner 2049"], q.SearchTexts().ToArray());
        var ranked = q.Rank([Movie(78, "Blade Runner", "1982"), Movie(335984, "Blade Runner 2049", "2017"), Movie(78, "Blade Runner", "1982")]);
        Assert.Equal(335984, ranked[0].Id);
        Assert.Equal(2, ranked.Count);
    }
}
