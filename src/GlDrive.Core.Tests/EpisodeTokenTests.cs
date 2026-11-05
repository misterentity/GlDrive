using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class EpisodeTokenTests
{
    [Theory]
    [InlineData("MasterChef.US.S16E05.1080p.WEB.h264-EDITH", 16, "5")]
    [InlineData("Love.Island.S01E13.720p.HDTV.x264-SYGiNT", 1, "13")]
    [InlineData("The.Daily.Show.S30E100.1080p.WEB.h264-EDITH", 30, "100")]
    [InlineData("Jeopardy.S100E01.1080p.WEB.h264-GRP", 100, "1")]
    [InlineData("Example.Show.S01E01E02.1080p.WEB.H264-G", 1, "1,2")]
    [InlineData("Example.Show.S01E01-E03.1080p.WEB.H264-G", 1, "1,2,3")]
    [InlineData("Example.Show.S01E01-03.1080p.WEB.H264-G", 1, "1,2,3")]
    [InlineData("Example.Show.S01E03E01E03.1080p.WEB.H264-G", 1, "1,3")]
    [InlineData("Example.Show.S01E01E03-E05.1080p.WEB.H264-G", 1, "1,3,4,5")]
    [InlineData("example_show_s02e07_720p_web", 2, "7")]
    [InlineData("Example Show S02E07 720p", 2, "7")]
    [InlineData("Example.Show.s01e01-e03.1080p.WEB.H264-G", 1, "1,2,3")]
    public void Finds_episode_tokens_in_scene_names(string name, int season, string episodes)
    {
        var token = EpisodeToken.FindEpisode(name);
        Assert.NotNull(token);
        Assert.Equal(season, token.Season);
        Assert.False(token.IsSeasonPack);
        Assert.Equal(episodes.Split(',').Select(int.Parse), token.Episodes);
        Assert.Contains(token.FirstEpisode!.Value, token.Episodes);
    }

    // Before EpisodeToken the wishlist read the range part case-sensitively, so a lower-case
    // "s01e01-e03" became the two episodes 1 and 3 and skipped the range checks.
    [Theory]
    [InlineData("example.show.s01e01-e03.1080p", "1,2,3")]
    [InlineData("example.show.s01e03-e01.1080p", "")]
    public void Lower_case_ranges_read_like_upper_case_ones(string name, string episodes)
        => Assert.Equal(episodes.Length == 0 ? Array.Empty<int>() : episodes.Split(',').Select(int.Parse).ToArray(),
            EpisodeToken.FindEpisode(name)!.Episodes);

    [Theory]
    [InlineData("Example.Show.S01E03-E01.1080p.WEB.H264-G", 1, 3)]
    [InlineData("Example.Show.S01E001-E999.1080p.WEB.H264-G", 1, 1)]
    public void Backwards_or_unbounded_ranges_have_no_episodes(string name, int season, int first)
    {
        var token = EpisodeToken.FindEpisode(name);
        Assert.NotNull(token);
        Assert.Equal(season, token.Season);
        Assert.Equal(first, token.FirstEpisode);
        Assert.Empty(token.Episodes);
    }

    [Theory]
    [InlineData("Example.Show.S01.1080p.WEB.H264-G", 1)]
    [InlineData("Example.Show.S12.COMPLETE.2160p.WEB.H265-G", 12)]
    [InlineData("Example.Show.S101-GROUP", 101)]
    public void Season_packs_are_tokens_without_episodes(string name, int season)
    {
        var token = Assert.Single(EpisodeToken.FindAll(name));
        Assert.True(token.IsSeasonPack);
        Assert.Equal(season, token.Season);
        Assert.Null(token.FirstEpisode);
        Assert.Empty(token.Episodes);
        Assert.Null(EpisodeToken.FindEpisode(name));
    }

    [Fact]
    public void Episode_search_skips_an_earlier_season_pack_token()
    {
        const string name = "Example.Show.S01.S01E05.1080p.WEB.H264-G";
        Assert.Equal(2, EpisodeToken.FindAll(name).Count);
        var token = EpisodeToken.FindEpisode(name);
        Assert.NotNull(token);
        Assert.Equal([5], token.Episodes);
    }

    [Fact]
    public void Index_and_length_cover_the_token_without_its_separator()
    {
        const string name = "Example.Show.S01E01-E03.1080p";
        var token = EpisodeToken.FindEpisode(name)!;
        Assert.Equal("S01E01-E03", name.Substring(token.Index, token.Length));
        Assert.Equal("Example.Show.", name[..token.Index]);
    }

    [Theory]
    [InlineData("Example.Show.2024.05.01.1080p.WEB.H264-G")] // dated daily episode: SceneNameParser.TryEpisodeDate
    [InlineData("Example.Show.S2024E05.1080p.WEB.H264-G")]   // four-digit season
    [InlineData("Example.Show.1x05.720p.HDTV.x264-G")]        // NxNN is not a recognised spelling
    [InlineData("Mercedes.S600E01X.Review")]                   // glued to trailing text
    [InlineData("XS01E01.Something")]                          // glued to leading text
    [InlineData("Example.Show.S01E1000.1080p")]
    [InlineData("Example.Show.S١٢E01.1080p")]        // non-ASCII digits
    [InlineData("")]
    public void Names_without_a_standalone_token_have_none(string name)
    {
        Assert.Empty(EpisodeToken.FindAll(name));
        Assert.Null(EpisodeToken.FindEpisode(name));
    }

    [Theory]
    [InlineData("Example.Show.S01E01E02.1080p", true)]
    [InlineData("Example.Show.S01E01-E02.1080p", true)]
    [InlineData("Example.Show.S01E01-02.1080p", true)]
    [InlineData("Example.Show.S01E01.E02.1080p", true)]
    [InlineData("Example.Show.S01E01+02.1080p", true)]
    [InlineData("Example.Show.S01E01.Part.S01E02", true)]
    [InlineData("Example.Show.S01E01.1080p.WEB.H264-G", false)]
    [InlineData("Example.Show.S01.1080p.WEB.H264-G", false)]
    [InlineData("Example.Movie.2026.1080p.BluRay.x264-G", false)]
    public void Library_import_precheck_flags_possible_multi_episode_names(string name, bool several)
        => Assert.Equal(several, EpisodeToken.MayCoverSeveralEpisodes(name));
}
