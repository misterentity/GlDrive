using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class CollectionGapFinderTests
{
    private static readonly TmdbCollectionData Set = new(9, "Example Collection", [new(42, "Example Movie", 2024)]);
    private static LibraryIndexSnapshot Index(bool complete, params LibraryIndexEntry[] entries) => new(DateTime.UtcNow, entries, 1, entries.Length, 0, complete);
    private static DownloadItem Download(string release = "Example.Movie.2024.720p.WEB-DL-GRP", DownloadStatus status = DownloadStatus.Completed) =>
        new() { Id = "download", ReleaseName = release, Status = status };
    private static LibraryIndexEntry Library(bool verified = true, string path = @"C:\synthetic\Example.Movie.2024.720p.WEB-DL-GRP.mkv", params string[] guids) =>
        new(new("Example Movie", 2024, null, null, QualityProfile.Q720p), path, "disk", verified, null, guids);
    private static string Status(LibraryIndexSnapshot index, DownloadItem[]? downloads = null, WishlistItem[]? wishlist = null) =>
        Assert.Single(CollectionGapFinder.Compare(Set, index, downloads ?? [], wishlist ?? [], new DownloadConfig { QualityDefault = "2160p" })).Status;

    [Fact]
    public void Good_lower_quality_library_and_completed_downloads_complete_a_collection()
    {
        Assert.Equal("owned", Status(Index(false, Library())));
        Assert.Equal("owned", Status(LibraryIndexSnapshot.Empty, [Download()]));
    }

    [Theory]
    [InlineData("CAM")]
    [InlineData("TS")]
    [InlineData("TC")]
    public void Low_source_copies_do_not_complete_a_collection(string source)
    {
        Assert.Equal("missing", Status(Index(true), [Download($"Example.Movie.2024.1080p.{source}.H264-GRP")]));
        Assert.Equal("missing", Status(Index(true, Library(path: $@"C:\synthetic\Example.Movie.2024.1080p.{source}.H264-GRP.mkv"))));
    }

    [Fact]
    public void Unknown_and_incomplete_index_do_not_claim_a_film_is_missing()
    {
        Assert.Equal("unknown", Status(LibraryIndexSnapshot.Empty));
        Assert.Equal("unknown", Status(Index(false)));
        Assert.Equal("missing", Status(Index(true)));
    }

    [Fact]
    public void Catalog_only_or_conflicting_provider_id_is_not_ownership()
    {
        Assert.Equal("unknown", Status(Index(false, Library(verified: false, guids: ["tmdb://42"]))));
        Assert.Equal("missing", Status(Index(true, Library(guids: ["tmdb://99"]))));
    }

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Extracting)]
    [InlineData(DownloadStatus.Paused)]
    public void Positive_pending_movie_download_is_distinguished_from_missing(DownloadStatus status) =>
        Assert.Equal("downloading", Status(Index(true), [Download(status: status)]));

    [Theory]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void Failed_or_cancelled_download_is_not_ownership_or_pending(DownloadStatus status) =>
        Assert.Equal("missing", Status(Index(true), [Download(status: status)]));

    [Fact]
    public void Different_year_or_low_source_pending_copy_does_not_hide_a_gap()
    {
        Assert.Equal("missing", Status(Index(true), [Download("Example.Movie.2023.1080p.WEB-DL-GRP", DownloadStatus.Downloading)]));
        Assert.Equal("missing", Status(Index(true), [Download("Example.Movie.2024.1080p.CAM.H264-GRP", DownloadStatus.Queued)]));
    }

    [Theory]
    [InlineData(WishlistStatus.Watching, false)]
    [InlineData(WishlistStatus.Paused, false)]
    [InlineData(WishlistStatus.Completed, false)]
    [InlineData(WishlistStatus.Watching, true)]
    public void Existing_wishes_including_inactive_and_paused_are_never_reactivated(WishlistStatus status, bool dead)
    {
        var wish = new WishlistItem { Type = MediaType.Movie, TmdbId = 42, Title = "Example Movie", Year = 2024, Status = status, Dead = dead };
        Assert.Equal("wanted", Status(Index(true), wishlist: [wish]));
        Assert.Equal(status, wish.Status); Assert.Equal(dead, wish.Dead);
    }

    [Fact]
    public void Manual_title_only_wish_suppresses_duplicate_but_a_conflicting_positive_id_does_not()
    {
        var manual = new WishlistItem { Type = MediaType.Movie, Title = "Example Movie" };
        Assert.Equal("wanted", Status(Index(true), wishlist: [manual]));
        manual.TmdbId = 99;
        Assert.Equal("missing", Status(Index(true), wishlist: [manual]));
    }

    [Fact]
    public void Missing_year_is_unknown_unless_positive_tmdb_evidence_proves_ownership()
    {
        var unknownYear = Set with { Films = [new(42, "Example Movie", null)] };
        Assert.Equal("unknown", CollectionGapFinder.Compare(unknownYear, Index(true, Library()), [], [], new()).Single().Status);
        Assert.Equal("owned", CollectionGapFinder.Compare(unknownYear, Index(true, Library(guids: ["tmdb://42"])), [], [], new()).Single().Status);
        var wish = new WishlistItem { Id = "wish", Type = MediaType.Movie, TmdbId = 42, Title = "Example Movie" };
        var download = Download(); download.WishlistItemId = wish.Id;
        Assert.Equal("owned", CollectionGapFinder.Compare(unknownYear, LibraryIndexSnapshot.Empty, [download], [wish], new()).Single().Status);
    }
}
