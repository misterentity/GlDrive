using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class WishlistAcquisitionPolicyTests
{
    private static WishlistItem Show() => new() { Type = MediaType.TvShow, Title = "Example Show", TvMazeId = 123 };

    [Theory]
    [InlineData("Example.Show.S01E02.1080p.WEB.H264-G", "tv:maze:123:S01E02")]
    [InlineData("Example.Show.S01.1080p.WEB.H264-G", "tv:maze:123:S01")]
    [InlineData("Example.Show.2026.10.27.1080p.WEB.H264-G", "tv:maze:123:2026-10-27")]
    public void Episode_pack_and_date_have_separate_stable_identity(string release, string key)
    {
        Assert.Equal([key], WishlistAcquisition.Keys(Show(), release));
        Assert.True(WishlistAcquisition.MatchesTitle(Show(), release));
    }

    [Theory]
    [InlineData("S01E01E03", "01,03")]
    [InlineData("S01E01-E03", "01,02,03")]
    [InlineData("S01E01-03", "01,02,03")]
    [InlineData("S01E03E01E03", "01,03")]
    public void Multi_episode_keys_are_expanded_sorted_and_distinct(string episodeToken, string suffixes)
    {
        Assert.Equal(suffixes.Split(',').Select(s => "tv:maze:123:S01E" + s),
            WishlistAcquisition.Keys(Show(), $"Example.Show.{episodeToken}.1080p.WEB.H264-G"));
    }

    [Theory]
    [InlineData("S01E03-E01")]
    [InlineData("S01E001-E999")]
    [InlineData("no.episode")]
    [InlineData("2026.02.30")]
    public void Invalid_or_unbounded_episode_identity_fails_closed(string token)
    {
        Assert.Empty(WishlistAcquisition.Keys(Show(), $"Example.Show.{token}.1080p.WEB.H264-G"));
    }

    [Theory]
    [InlineData("tv:maze:123:S01", "tv:maze:123:S01E02", true)]
    [InlineData("tv:maze:123:S01E02", "tv:maze:123:S01", true)]
    [InlineData("tv:maze:123:S01E02", "tv:maze:123:S01E03", false)]
    [InlineData("tv:maze:123:S01", "tv:maze:123:S010E02", false)]
    [InlineData("tv:maze:123:S01", "tv:maze:124:S01E02", false)]
    [InlineData("tv:maze:123:2026-10-27", "tv:maze:123:2026-10-28", false)]
    public void Pack_overlap_does_not_cross_series_seasons_or_dates(string first, string second, bool overlap)
    {
        Assert.Equal(overlap, WishlistAcquisition.Overlaps(first, second));
    }

    [Theory]
    [InlineData(QualityProfile.Any)]
    [InlineData(QualityProfile.SD)]
    [InlineData(QualityProfile.Q720p)]
    [InlineData(QualityProfile.Q1080p)]
    [InlineData(QualityProfile.Q2160p)]
    public void Missing_profile_resolves_legacy_item_quality_without_using_new_default(QualityProfile quality)
    {
        var item = new WishlistItem { Quality = quality };
        var profile = WishlistAcquisition.ResolveProfile(item, new DownloadConfig { QualityDefault = "2160p" });
        Assert.NotNull(profile);
        Assert.Equal(ScoredQualityProfile.Legacy(quality).Id, profile.Id);
        Assert.True(profile.AllowUnknownResolution);
        Assert.Equal(quality == QualityProfile.Any ? [] : new[] { quality }, profile.AllowedResolutions);
    }

    [Fact]
    public void Profile_lookup_is_case_insensitive_and_missing_reference_does_not_silently_fallback()
    {
        var custom = new ScoredQualityProfile { Id = "custom" };
        var config = new DownloadConfig { Profiles = [custom] };
        var item = new WishlistItem { ProfileId = "CUSTOM" };
        Assert.Same(custom, WishlistAcquisition.ResolveProfile(item, config));
        item.ProfileId = "does-not-exist";
        Assert.Null(WishlistAcquisition.ResolveProfile(item, config));
        item.ProfileId = ScoredQualityProfile.Recommended(QualityProfile.Q2160p).Id.ToUpperInvariant();
        Assert.Equal([QualityProfile.Q2160p], WishlistAcquisition.ResolveProfile(item, config)!.AllowedResolutions);
    }

    [Fact]
    public void Metadata_ids_disambiguate_titles_and_titles_bridge_newly_added_ids()
    {
        var first = new WishlistItem { Type = MediaType.Movie, Title = "Example Film", Year = 2026, ImdbId = "tt123" };
        var same = new WishlistItem { Type = MediaType.Movie, Title = "Localized Name", Year = 2026, ImdbId = "TT123" };
        Assert.True(WishlistAcquisition.SameTitle(first, same));
        Assert.Equal(WishlistAcquisition.Identity(first), WishlistAcquisition.Identity(same));
        same.ImdbId = "tt124";
        same.Title = first.Title;
        Assert.False(WishlistAcquisition.SameTitle(first, same));
        same.ImdbId = null;
        Assert.True(WishlistAcquisition.SameTitle(first, same));
        same.Year = 1990;
        Assert.False(WishlistAcquisition.SameTitle(first, same));
    }

    [Fact]
    public void Movie_keys_are_one_title_identity_and_other_titles_do_not_match()
    {
        var item = new WishlistItem { Type = MediaType.Movie, Title = "Example Film", Year = 2026, ImdbId = "tt123" };
        Assert.Equal(["movie:imdb:tt123"], WishlistAcquisition.Keys(item, "Example.Film.2026.1080p.WEB.H264-G"));
        Assert.True(WishlistAcquisition.MatchesTitle(item, "Example.Film.2026.1080p.WEB.H264-G"));
        Assert.False(WishlistAcquisition.MatchesTitle(item, "Other.Film.2026.1080p.WEB.H264-G"));
        Assert.False(WishlistAcquisition.MatchesTitle(item, "Example.Film.1990.1080p.WEB.H264-G"));
    }
}
