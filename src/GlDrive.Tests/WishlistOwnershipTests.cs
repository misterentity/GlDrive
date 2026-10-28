using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private static WishlistItem Movie() => new()
    {
        Id = "wanted-movie", Source = "PlexWatchlist", PlexWatchlistId = "plex://movie/synthetic",
        Type = MediaType.Movie, Title = "Synthetic Film", Year = 2024, ImdbId = "tt1234567",
        Quality = QualityProfile.Q1080p, ProfileId = ScoredQualityProfile.Legacy(QualityProfile.Q1080p).Id
    };
    private static LibraryIndexEntry Entry(string title = "Synthetic Film", int? year = 2024,
        QualityProfile quality = QualityProfile.Q1080p, bool verified = true, int? season = null, int? episode = null,
        IReadOnlyList<string>? guids = null) =>
        new(new(title, year, season, episode, quality), "synthetic.mkv", verified ? "disk" : "plex", verified, null, guids ?? []);
    private static LibraryIndexSnapshot Snapshot(params LibraryIndexEntry[] entries) => new(Now.UtcDateTime, entries, 1, entries.Length, 0, true);
    private static DownloadItem Download(string release = "Synthetic.Film.2024.1080p.WEB-GROUP", DownloadStatus status = DownloadStatus.Completed) =>
        new() { Id = "download", WishlistItemId = "wanted-movie", ReleaseName = release, Status = status,
            DownloadedBytes = 100, TotalBytes = 100, CompletedAt = Now.AddDays(-1).UtcDateTime };

    [Theory]
    [InlineData(QualityProfile.Q1080p)]
    [InlineData(QualityProfile.Q2160p)]
    public void Verified_library_copy_at_or_above_default_cutoff_proves_ownership(QualityProfile quality) =>
        Assert.True(WishlistOwnership.IsOwned(Movie(), new(), Snapshot(Entry(quality: quality)), [], Now));

    [Theory]
    [InlineData("Different Film", 2024, QualityProfile.Q1080p, true)]
    [InlineData("Synthetic Film", 2023, QualityProfile.Q1080p, true)]
    [InlineData("Synthetic Film", 2024, QualityProfile.Q720p, true)]
    [InlineData("Synthetic Film", 2024, QualityProfile.Any, true)]
    [InlineData("Synthetic Film", 2024, QualityProfile.Q2160p, false)]
    public void Approximate_missing_or_below_cutoff_library_evidence_keeps_item_open(
        string title, int year, QualityProfile quality, bool verified) =>
        Assert.False(WishlistOwnership.IsOwned(Movie(), new(), Snapshot(Entry(title, year, quality, verified)), [], Now));

    [Fact]
    public void Catalog_only_high_quality_does_not_upgrade_lower_verified_copy() =>
        Assert.False(WishlistOwnership.IsOwned(Movie(), new(),
            Snapshot(Entry(quality: QualityProfile.Q720p), Entry(quality: QualityProfile.Q2160p, verified: false)), [], Now));

    [Fact]
    public void Verified_external_identity_can_match_an_alternate_library_title() =>
        Assert.True(WishlistOwnership.IsOwned(Movie(), new(),
            Snapshot(Entry(title: "Alternate Catalog Title", year: null, guids: ["imdb://tt1234567"])), [], Now));

    [Fact]
    public void Conflicting_library_external_id_does_not_fall_back_to_title() =>
        Assert.False(WishlistOwnership.IsOwned(Movie(), new(), Snapshot(Entry(guids: ["imdb://tt7654321"])), [], Now));

    [Fact]
    public void Successful_matching_download_proves_ownership_without_a_library_refresh() =>
        Assert.True(WishlistOwnership.IsOwned(Movie(), new(), LibraryIndexSnapshot.Empty, [Download()], Now));

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Extracting)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void Incomplete_download_never_proves_ownership(DownloadStatus status) =>
        Assert.False(WishlistOwnership.IsOwned(Movie(), new(), LibraryIndexSnapshot.Empty, [Download(status: status)], Now));

    [Theory]
    [InlineData("Different.Film.2024.1080p.WEB-GROUP")]
    [InlineData("Synthetic.Film.2023.1080p.WEB-GROUP")]
    [InlineData("Synthetic.Film.2024.720p.WEB-GROUP")]
    [InlineData("Synthetic.Film.2024.WEB-GROUP")]
    public void Wishlist_reference_does_not_override_download_identity_or_quality(string release) =>
        Assert.False(WishlistOwnership.IsOwned(Movie(), new(), LibraryIndexSnapshot.Empty, [Download(release)], Now));

    [Fact]
    public void Grabbed_names_and_pending_reservations_are_not_completed_downloads()
    {
        var item = Movie();
        item.GrabbedReleases = ["Synthetic.Film.2024.1080p.WEB-GROUP"];
        item.PendingGrabs = [new() { ReleaseName = item.GrabbedReleases[0] }];
        Assert.False(WishlistOwnership.IsOwned(item, new(), LibraryIndexSnapshot.Empty, [], Now));
    }

    [Fact]
    public void Custom_profile_cutoff_must_have_positive_score_evidence()
    {
        var item = Movie(); item.ProfileId = "high-cutoff";
        var config = new DownloadConfig { Profiles = [new() { Id = "high-cutoff", CutoffScore = 4300 }] };
        Assert.False(WishlistOwnership.IsOwned(item, config, LibraryIndexSnapshot.Empty, [Download()], Now));
        Assert.True(WishlistOwnership.IsOwned(item, config, LibraryIndexSnapshot.Empty,
            [Download("Synthetic.Film.2024.1080p.BluRay-GROUP")], Now));
    }

    private static WishlistItem Series(bool ended = true) => new()
    {
        Id = "wanted-series", Source = "PlexWatchlist", PlexWatchlistId = "plex://show/synthetic",
        Type = MediaType.TvShow, Title = "Synthetic Series", Year = 2024, Quality = QualityProfile.Q1080p,
        ProfileId = ScoredQualityProfile.Legacy(QualityProfile.Q1080p).Id,
        WatchlistSeriesEnded = ended,
        SeriesInventory =
        [
            new() { TvMazeEpisodeId = 1, Season = 1, Number = 1, AirAtUtc = Now.AddMonths(-2) },
            new() { TvMazeEpisodeId = 2, Season = 1, Number = 2, AirAtUtc = Now.AddMonths(-1) }
        ],
        SeriesInventoryUpdatedAtUtc = Now.AddHours(-1)
    };
    private static LibraryIndexEntry Episode(int episode, QualityProfile quality = QualityProfile.Q1080p) =>
        Entry("Synthetic Series", 2024, quality, season: 1, episode: episode);

    [Fact]
    public void Ended_series_requires_every_known_episode_at_cutoff()
    {
        Assert.False(WishlistOwnership.IsOwned(Series(), new(), Snapshot(Episode(1)), [], Now));
        Assert.False(WishlistOwnership.IsOwned(Series(), new(), Snapshot(Episode(1), Episode(2, QualityProfile.Q720p)), [], Now));
        Assert.True(WishlistOwnership.IsOwned(Series(), new(), Snapshot(Episode(1), Episode(2)), [], Now));
    }

    [Fact]
    public void Ongoing_series_stays_open_even_when_current_episodes_are_owned() =>
        Assert.False(WishlistOwnership.IsOwned(Series(ended: false), new(), Snapshot(Episode(1), Episode(2)), [], Now));

    [Fact]
    public void Ended_status_without_episode_inventory_does_not_claim_the_whole_series()
    {
        var item = Series(); item.SeriesInventory = [];
        Assert.False(WishlistOwnership.IsOwned(item, new(), Snapshot(Episode(1), Episode(2)), [], Now));
    }

    [Fact]
    public void Ended_series_can_combine_verified_library_and_successful_download_evidence()
    {
        var download = Download("Synthetic.Series.S01E02.1080p.WEB-GROUP");
        download.WishlistItemId = "wanted-series";
        Assert.True(WishlistOwnership.IsOwned(Series(), new(), Snapshot(Episode(1)), [download], Now));
    }
}
