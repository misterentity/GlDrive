using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public class WishlistOwnershipRegressionTests
{
    [Theory]
    [InlineData("legacy-custom")]
    [InlineData("legacy-q1080p")]
    public void Explicit_custom_profile_is_not_bypassed_by_its_name(string profileId)
    {
        var item = new WishlistItem { Title = "Example", Year = 2026, Quality = QualityProfile.Q1080p, ProfileId = profileId };
        var config = new DownloadConfig
        {
            Profiles = [new ScoredQualityProfile { Id = profileId, CutoffScore = 4000 }]
        };
        Assert.False(WishlistOwnership.MeetsReleaseCutoff(item, "Example.2026.1080p.WEB.H264-GROUP", config));
        Assert.True(WishlistOwnership.MeetsReleaseCutoff(item, "Example.2026.1080p.BluRay.H264-GROUP", config));
        var entry = new LibraryIndexEntry(new("Example", 2026, null, null, QualityProfile.Q1080p),
            @"C:\Library\Example.2026.1080p.WEB.H264-GROUP.mkv", "disk", true, null, []);
        Assert.False(WishlistOwnership.MeetsLibraryCutoff(item, entry, config));
    }

    [Theory]
    [InlineData("imdb://tt1234567", true)]
    [InlineData("imdb://tt7654321", false)]
    public void Verified_movie_guid_handles_localized_title_without_ignoring_identity_conflicts(string guid, bool owned)
    {
        var item = new WishlistItem { Title = "Example", Year = 2026, ImdbId = "tt1234567", Quality = QualityProfile.Q1080p };
        var entry = new LibraryIndexEntry(new("Localized Example", 2026, null, null, QualityProfile.Q1080p),
            @"C:\Library\Localized.Example.2026.1080p.WEB-GROUP.mkv", "both", true, "synthetic-id", [guid]);
        var snapshot = new LibraryIndexSnapshot(DateTime.UtcNow, [entry], 1, 1, 0, false);
        Assert.Equal(owned, WishlistOwnership.IsOwned(item, new(), snapshot, [], DateTimeOffset.UtcNow));
        Assert.False(WishlistOwnership.IsOwned(item, new(), snapshot with { Entries = [entry with { VerifiedOnDisk = false }] }, [], DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("Example.Show.1990.S01E01.1080p.WEB-GROUP", false)]
    [InlineData("Example.Show.2020.S01E01.1080p.WEB-GROUP", true)]
    [InlineData("Example.Show.S01E01.1080p.WEB-GROUP", true)]
    public void Known_tv_remake_year_conflict_is_not_owned_but_ordinary_yearless_episodes_are_supported(string release, bool owned)
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var item = new WishlistItem
        {
            Type = MediaType.TvShow, Title = "Example Show", Year = 2020, Quality = QualityProfile.Q1080p,
            WatchlistSeriesEnded = true, SeriesInventoryUpdatedAtUtc = now,
            SeriesInventory = [new() { Season = 1, Number = 1, AirDate = new DateOnly(2020, 1, 1) }]
        };
        var download = new DownloadItem { ReleaseName = release, Status = DownloadStatus.Completed, TotalBytes = 100 };
        Assert.Equal(owned, WishlistOwnership.IsOwned(item, new(), LibraryIndexSnapshot.Empty, [download], now));
    }
}
