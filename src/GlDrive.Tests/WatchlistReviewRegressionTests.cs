using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

public sealed class WatchlistReviewRegressionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static WishlistItem Imported(MediaType type = MediaType.Movie) => new()
    {
        Id = "wanted", Source = PlexWatchlistSync.Source, PlexWatchlistId = "plex://synthetic/title",
        Title = "Synthetic Title", Type = type, Year = 2026, ImdbId = "tt1234567", TmdbId = 123,
        Quality = QualityProfile.Q1080p
    };

    private static WishlistStore Store(WishlistItem item, Action<string>? write = null) =>
        new(() => JsonSerializer.Serialize(new[] { item }, Json), write ?? (_ => { }));

    [Theory]
    [InlineData(MediaType.Movie, null)]
    [InlineData(MediaType.TvShow, null)]
    [InlineData(MediaType.TvShow, 456)]
    public void Title_only_manual_choice_prevents_watchlist_duplicate_without_changing_user_choices(MediaType type, int? tvMazeId)
    {
        var manual = new WishlistItem
        {
            Id = "manual", Type = type, Title = "Synthetic: Title", TvMazeId = tvMazeId,
            Quality = QualityProfile.Q720p, ProfileId = "user-profile", Status = WishlistStatus.Paused
        };
        var store = Store(manual);
        var result = store.ImportWatchlistPage([Imported(type)], () => true);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Existing);
        var actual = Assert.Single(store.Items);
        Assert.Equal("manual", actual.Id);
        Assert.Equal(QualityProfile.Q720p, actual.Quality);
        Assert.Equal("user-profile", actual.ProfileId);
        Assert.Equal(WishlistStatus.Paused, actual.Status);
        Assert.Equal(tvMazeId, actual.TvMazeId);
        Assert.Null(actual.ImdbId);
        Assert.Null(actual.TmdbId);
        Assert.Null(actual.PlexWatchlistId);
        Assert.Equal("", actual.Source);
    }

    [Fact]
    public void Missing_year_dedupe_does_not_cross_media_type_or_override_known_identity_conflicts()
    {
        var incoming = Imported();
        var manual = new WishlistItem { Title = incoming.Title, Type = MediaType.TvShow };
        Assert.False(WishlistOwnership.SameIdentity(manual, incoming));
        manual.Type = MediaType.Movie;
        manual.ImdbId = "tt7654321";
        Assert.False(WishlistOwnership.SameIdentity(manual, incoming));
        manual.ImdbId = null;
        manual.Year = 1990;
        Assert.False(WishlistOwnership.SameIdentity(manual, incoming));
    }

    [Fact]
    public async Task Slow_ownership_evaluation_does_not_hold_the_shared_wishlist_lock()
    {
        var store = Store(Imported());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reconcile = Task.Run(() => store.CompleteOwnedWatchlist(_ =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            return true;
        }, () => true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = Task.Run(() => store.Items.Count);
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); }
        Assert.Equal(1, await reconcile.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changes_during_ownership_evaluation_win_over_the_stale_snapshot(bool remove)
    {
        var store = Store(Imported());
        var count = store.CompleteOwnedWatchlist(snapshot =>
        {
            var live = store.GetById(snapshot.Id)!;
            Assert.NotSame(live, snapshot);
            if (remove) store.Remove(live.Id);
            else
            {
                live.Quality = QualityProfile.Q2160p;
                store.Update(live);
            }
            return true;
        }, () => true);
        Assert.Equal(0, count);
        Assert.Equal(WishlistStatus.Watching, Assert.Single(store.Items).Status);
        Assert.Equal(remove, store.Items[0].Dead);
    }

    [Fact]
    public void Reconcile_prepares_each_completed_download_once_for_many_items()
    {
        var parseCount = 0;
        var prepared = WishlistOwnership.PrepareDownloads(
        [
            new() { Status = DownloadStatus.Completed, ReleaseName = "Synthetic.Title.2026.1080p.WEB-GROUP" },
            new() { Status = DownloadStatus.Completed, ReleaseName = "Other.Title.2026.1080p.WEB-GROUP" },
            new() { Status = DownloadStatus.Queued, ReleaseName = "Pending.Title.2026.1080p.WEB-GROUP" }
        ], release => { parseCount++; return SceneNameParser.Parse(release); });
        for (var i = 0; i < 40; i++)
            Assert.True(WishlistOwnership.IsOwnedPrepared(Imported(), new(), LibraryIndexSnapshot.Empty, prepared, Now));
        Assert.Equal(2, parseCount);
        Assert.Equal(2, prepared.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_running_or_inventoryless_series_does_not_enumerate_downloads(bool ended, bool inventory)
    {
        var item = Imported(MediaType.TvShow);
        item.WatchlistSeriesEnded = ended;
        item.SeriesInventoryUpdatedAtUtc = Now;
        if (inventory) item.SeriesInventory = [new() { Season = 1, Number = 1, AirDate = new(2025, 1, 1) }];
        var downloads = new DownloadItem[] { new() }.Select<DownloadItem, DownloadItem>(_ =>
            throw new InvalidOperationException("Downloads must not be enumerated."));
        Assert.False(WishlistOwnership.IsOwned(item, new(), LibraryIndexSnapshot.Empty, downloads, Now));
    }

    [Fact]
    public void Removed_import_stays_inactive_after_restart_and_reimport()
    {
        string? saved = null;
        var store = Store(Imported(), json => saved = json);
        store.Remove("wanted");
        Assert.True(Assert.Single(store.Items).Dead);
        var reloaded = new WishlistStore(() => saved, json => saved = json);
        var result = reloaded.ImportWatchlistPage([Imported()], () => true);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Existing);
        Assert.True(Assert.Single(reloaded.Items).Dead);
        Assert.Equal(0, reloaded.CompleteOwnedWatchlist(_ => throw new InvalidOperationException("Inactive item evaluated"), () => true));
        Assert.False(reloaded.TryBeginReleaseEvaluation("wanted", "Synthetic.Title.2026.1080p.WEB-GROUP"));
    }

    [Fact]
    public void Removed_import_stays_inactive_when_persistence_fails_but_manual_remove_still_deletes()
    {
        var store = Store(Imported(), _ => throw new IOException("synthetic failure"));
        store.Remove("wanted");
        Assert.Equal(0, store.ImportWatchlistPage([Imported()], () => true).Added);
        Assert.True(Assert.Single(store.Items).Dead);
        store.Add(new() { Id = "manual", Title = "Manual Title" });
        store.Remove("manual");
        Assert.Null(store.GetById("manual"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("another-wanted-id", false)]
    [InlineData("wanted", true)]
    public void Movie_without_a_year_needs_an_exact_wishlist_link_to_accept_a_completed_download(string? wishlistId, bool expected)
    {
        var item = Imported();
        item.Year = null;
        var download = new DownloadItem
        {
            ReleaseName = "Synthetic.Title.1990.1080p.WEB-GROUP", Status = DownloadStatus.Completed,
            WishlistItemId = wishlistId
        };
        Assert.Equal(expected, WishlistOwnership.IsOwned(item, new(), LibraryIndexSnapshot.Empty, [download], Now));
    }

    [Fact]
    public void Movie_with_a_year_needs_matching_year_evidence_even_on_a_linked_download()
    {
        var item = Imported();
        var download = new DownloadItem
        {
            ReleaseName = "Synthetic.Title.1080p.WEB-GROUP", Status = DownloadStatus.Completed,
            WishlistItemId = item.Id
        };
        Assert.False(WishlistOwnership.IsOwned(item, new(), LibraryIndexSnapshot.Empty, [download], Now));
    }
}
