using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class ReleaseInfoLookupTests
{
    private static TmdbSearchResult R(int id, string type, string title, string? date = null) =>
        new() { Id = id, MediaType = type, Title = type == "movie" ? title : "", Name = type == "tv" ? title : "",
            ReleaseDate = type == "movie" ? date : null, FirstAirDate = type == "tv" ? date : null };

    [Fact]
    public void Picks_the_right_kind_then_exact_title_then_year()
    {
        var results = new[] { R(1, "tv", "Dune Prophecy", "2024"), R(2, "movie", "Dune", "1984"), R(3, "movie", "Dune", "2021") };
        Assert.Equal(3, ReleaseInfoLookup.PickMatch(results, SceneNameParser.Parse("Dune.2021.2160p.UHD.BluRay.x265-GRP"), false)!.Id);
        Assert.Equal(2, ReleaseInfoLookup.PickMatch(results, SceneNameParser.Parse("Dune.1984.1080p.BluRay.x264-GRP"), false)!.Id);
    }

    [Fact]
    public void An_episode_name_prefers_the_show_and_rejects_loose_cross_kind_hits()
    {
        var parsed = SceneNameParser.Parse("Severance.S02E03.1080p.WEB.h264-GRP");
        Assert.Equal(7, ReleaseInfoLookup.PickMatch([R(6, "movie", "Severance", "2006"), R(7, "tv", "Severance", "2022")], parsed, true)!.Id);
        Assert.Null(ReleaseInfoLookup.PickMatch([R(8, "movie", "Severance Pay", "2010")], parsed, true));
        Assert.Equal(6, ReleaseInfoLookup.PickMatch([R(6, "movie", "Severance", "2006")], parsed, true)!.Id);
    }

    [Fact]
    public void Country_tag_is_dropped_on_a_second_search() =>
        Assert.Equal(["MasterChef US", "MasterChef"], ReleaseInfoLookup.SearchQueries("MasterChef US").ToArray());

    [Fact]
    public void Library_status_reports_plex_and_disk_copies()
    {
        var parsed = SceneNameParser.Parse("Example.Movie.2024.1080p.WEB-DL-GRP");
        var info = new ReleaseInfo("Example.Movie.2024.1080p.WEB-DL-GRP", parsed, false, "found", 42, "Example Movie", 2024);
        var plex = new LibraryIndexEntry(new("Example Movie", 2024, null, null, QualityProfile.Q2160p), "/m/x.mkv", "plex", false, "m/1", ["tmdb://42"]);
        var index = new LibraryIndexSnapshot(DateTime.UtcNow, [plex], 0, 0, 0, false) { PlexComplete = true };
        Assert.Equal("This movie is already in Plex (2160p).", ReleaseInfoLookup.LibraryStatus(index, info));
        Assert.Equal("This movie is not in your library.", ReleaseInfoLookup.LibraryStatus(index with { Entries = [] }, info));
        Assert.StartsWith("Library not indexed", ReleaseInfoLookup.LibraryStatus(LibraryIndexSnapshot.Empty, info));
    }
}
