using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class CollectionGapServiceTests
{
    private static readonly TmdbCollectionFilm Film = new(101, "Fixture Film", 2026);
    private static readonly TmdbCollectionData Collection = new(51, "Fixture Collection", [Film]);
    private static LibraryIndexSnapshot CompleteIndex => new(DateTime.UtcNow, [], 1, 0, 0, true);
    private static WishlistStore Store() => new(() => null, _ => { });
    private static CollectionGapService Service(WishlistStore? store = null, AppConfig? config = null,
        Func<LibraryIndexSnapshot>? index = null, Func<IEnumerable<DownloadItem>>? downloads = null,
        Func<int, CancellationToken, Task<TmdbCollectionResult>>? provider = null) =>
        new(config ?? new(), store ?? Store(), index ?? (() => CompleteIndex), downloads ?? (() => []),
            provider ?? ((_, _) => Task.FromResult(new TmdbCollectionResult("available", Collection))));

    [Fact]
    public async Task Only_explicit_refresh_calls_provider_and_add_publishes_local_projection()
    {
        var calls = 0;
        var store = Store();
        var service = Service(store, provider: (_, _) => { calls++; return Task.FromResult(new TmdbCollectionResult("available", Collection)); });
        Assert.Equal("not-refreshed", service.Snapshot.Status);
        Assert.Equal("unknown-member", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Equal(0, calls);
        Assert.Equal("missing", Assert.Single((await service.RefreshAsync(7)).Films).Status);
        var add = service.AddMissing(7, Film.TmdbId);
        Assert.Equal("added", add.Status);
        Assert.Equal(add.WishlistId, Assert.Single(store.Items).Id);
        Assert.Equal("wanted", Assert.Single(service.Snapshot.Films).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_refresh_returns_busy_without_new_provider_or_replacing_snapshot()
    {
        var pending = new TaskCompletionSource<TmdbCollectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = Service(provider: (_, _) => { calls++; return pending.Task; });
        var first = service.RefreshAsync(7);
        Assert.Equal("busy", (await service.RefreshAsync(8)).Status);
        Assert.Equal("not-refreshed", service.Snapshot.Status);
        Assert.Equal(1, calls);
        pending.SetResult(new("available", Collection));
        Assert.Equal(7, (await first).MovieTmdbId);
        Assert.Equal(7, service.Snapshot.MovieTmdbId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_unknown_refresh_clears_old_rows_and_add_authority(bool throws)
    {
        var calls = 0;
        var service = Service(provider: (_, _) => ++calls == 1
            ? Task.FromResult(new TmdbCollectionResult("available", Collection))
            : throws ? Task.FromException<TmdbCollectionResult>(new IOException("fixture unavailable"))
            : Task.FromResult(new TmdbCollectionResult("unknown", null)));
        await service.RefreshAsync(7);
        var failed = await service.RefreshAsync(8);
        Assert.Equal("unknown", failed.Status);
        Assert.Equal(8, failed.MovieTmdbId);
        Assert.Null(failed.CollectionId); Assert.Null(failed.CollectionName); Assert.Empty(failed.Films);
        Assert.Equal("unknown-member", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Equal("unknown-member", service.AddMissing(8, Film.TmdbId).Status);
    }

    [Fact]
    public async Task Cancellation_releases_refresh_slot_and_retains_only_previous_completed_result()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(provider: async (_, ct) =>
        {
            if (++calls == 2) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            return new("available", Collection);
        });
        await service.RefreshAsync(7);
        using var cancel = new CancellationTokenSource();
        var second = service.RefreshAsync(8, cancel.Token);
        await entered.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(7, service.Snapshot.MovieTmdbId);
        Assert.Equal("available", (await service.RefreshAsync(9)).Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task No_collection_is_an_explicit_empty_result_and_partial_members_can_be_added()
    {
        var calls = 0;
        var service = Service(provider: (_, _) => Task.FromResult(++calls == 1
            ? new TmdbCollectionResult("no-collection", null) : new("partial", Collection)));
        Assert.Equal("no-collection", (await service.RefreshAsync(7)).Status);
        Assert.Equal("unknown-member", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Equal("partial", (await service.RefreshAsync(7)).Status);
        Assert.Equal("added", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Equal("partial", service.Snapshot.Status);
    }

    [Fact]
    public async Task Add_rejects_unknown_members_and_stale_seed_from_another_window()
    {
        var store = Store(); var service = Service(store);
        await service.RefreshAsync(7);
        Assert.Equal("unknown-member", service.AddMissing(7, 999).Status);
        await service.RefreshAsync(8);
        Assert.Equal("unknown-member", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Empty(store.Items);
        Assert.Equal("added", service.AddMissing(8, Film.TmdbId).Status);
    }

    [Fact]
    public async Task Add_rechecks_new_library_ownership_without_provider_refresh()
    {
        var current = CompleteIndex; var store = Store();
        var service = Service(store, index: () => current);
        await service.RefreshAsync(7);
        var entry = new LibraryIndexEntry(new(Film.Title, Film.Year, null, null, QualityProfile.Q1080p),
            Path.Combine(Path.GetTempPath(), "Fixture.Film.2026.1080p.WEB-GROUP.mkv"), "disk", true, null, []);
        current = CompleteIndex with { Entries = [entry] };
        Assert.Equal("owned", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Empty(store.Items);
        Assert.Equal("owned", Assert.Single(service.Snapshot.Films).Status);
    }

    [Fact]
    public async Task Add_rechecks_new_download_and_preserves_its_queue()
    {
        var downloads = new List<DownloadItem>(); var store = Store();
        var service = Service(store, downloads: () => downloads);
        await service.RefreshAsync(7);
        var downloading = new DownloadItem { ReleaseName = "Fixture.Film.2026.1080p.WEB-GROUP", Status = DownloadStatus.Downloading };
        downloads.Add(downloading);
        Assert.Equal("downloading", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Same(downloading, Assert.Single(downloads)); Assert.Empty(store.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_unreadable_local_index_requires_refresh_before_add(bool throws)
    {
        var store = Store();
        var service = Service(store, index: () => throws ? throw new IOException("fixture unavailable") : LibraryIndexSnapshot.Empty);
        Assert.Equal("unknown", Assert.Single((await service.RefreshAsync(7)).Films).Status);
        Assert.Equal("ownership-unknown", service.AddMissing(7, Film.TmdbId).Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Unreadable_download_queue_keeps_absence_unknown_even_with_complete_index()
    {
        var service = Service(downloads: () => throw new IOException("fixture unavailable"));
        Assert.Equal("unknown", Assert.Single((await service.RefreshAsync(7)).Films).Status);
        Assert.Equal("ownership-unknown", service.AddMissing(7, Film.TmdbId).Status);
    }

    [Fact]
    public async Task Explicit_library_refresh_updates_cached_collection_without_metadata_lookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-collection-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Fixture.Film.2026.1080p.WEB-GROUP.mkv"), [1]);
            var config = new AppConfig { Library = new() { MovieRoot = root } };
            var current = LibraryIndexSnapshot.Empty;
            var lookups = 0;
            var reads = 0;
            var collection = Collection with { Films = [Film, new(102, "Absent Fixture", 2027)] };
            var service = new CollectionGapService(config, Store(), () => current, () => [],
                (_, _) => { lookups++; return Task.FromResult(new TmdbCollectionResult("available", collection)); },
                async ct => { reads++; return current = (await LibraryInventoryReader.ReadAsync(config, ct, _ => null)).Snapshot; });

            Assert.Null(service.LibraryStatus.RefreshedAtUtc);
            Assert.Equal(0, reads);
            await service.RefreshAsync(7);
            Assert.All(service.Snapshot.Films, row => Assert.Equal("unknown", row.Status));
            var status = await service.RefreshLibraryAsync();

            Assert.True(status.Complete);
            Assert.NotNull(status.RefreshedAtUtc);
            Assert.Equal(1, status.ScannedRoots);
            Assert.Equal("owned", service.Snapshot.Films[0].Status);
            Assert.Equal("missing", service.Snapshot.Films[1].Status);
            Assert.Equal("owned", service.AddMissing(7, 101).Status);
            Assert.Equal("added", service.AddMissing(7, 102).Status);
            Assert.Equal(1, reads);
            Assert.Equal(1, lookups);
            Assert.False(config.ControlApi.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Undated_collection_member_cannot_create_a_yearless_wish()
    {
        var store = Store();
        var undated = Film with { Year = null };
        var service = Service(store, provider: (_, _) => Task.FromResult(new TmdbCollectionResult("available", Collection with { Films = [undated] })));
        await service.RefreshAsync(7);
        Assert.Equal("year-required", service.AddMissing(7, undated.TmdbId).Status);
        Assert.Equal("year-required", store.AddCollectionMovie(undated, QualityProfile.Q1080p).Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Add_uses_current_normal_quality_and_preserves_source_floor_default()
    {
        var store = Store(); var config = new AppConfig(); var service = Service(store, config);
        await service.RefreshAsync(7);
        config.Downloads.QualityDefault = "2160p";
        Assert.Equal("added", service.AddMissing(7, Film.TmdbId).Status);
        var added = Assert.Single(store.Items);
        Assert.Equal(QualityProfile.Q2160p, added.Quality);
        Assert.Equal(ScoredQualityProfile.Legacy(added.Quality).Id, added.ProfileId);
        Assert.Null(added.AllowLowQualitySources);
        Assert.False(WishlistSourceFloor.Allows(added, "Fixture.Film.2026.2160p.CAM-GROUP", config.Downloads));
        Assert.Equal(WishlistStatus.Watching, added.Status); Assert.False(added.Dead);
        Assert.Empty(added.PendingGrabs); Assert.Empty(added.BestOwned);
    }

    [Theory]
    [InlineData(WishlistStatus.Paused, false)]
    [InlineData(WishlistStatus.Completed, true)]
    public async Task Concurrent_existing_wish_retains_status_copies_and_source_permissions(WishlistStatus status, bool dead)
    {
        var store = Store(); var service = Service(store);
        await service.RefreshAsync(7);
        var existing = new WishlistItem { Type = MediaType.Movie, TmdbId = Film.TmdbId, Title = Film.Title,
            Year = Film.Year, Status = status, Dead = dead, Quality = QualityProfile.Q720p,
            ProfileId = "custom", AllowLowQualitySources = true };
        var copy = new WishlistCopy { DownloadId = "existing-copy" }; existing.BestOwned.Add(copy); store.Add(existing);
        var result = service.AddMissing(7, Film.TmdbId);
        Assert.Equal("already-wanted", result.Status); Assert.Equal(existing.Id, result.WishlistId);
        Assert.Same(existing, Assert.Single(store.Items)); Assert.Equal(status, existing.Status); Assert.Equal(dead, existing.Dead);
        Assert.Equal("custom", existing.ProfileId); Assert.True(existing.AllowLowQualitySources);
        Assert.Same(copy, Assert.Single(existing.BestOwned));
    }

    [Fact]
    public async Task Atomic_add_race_creates_one_normalized_item_and_notifies_once()
    {
        var store = Store(); var notices = 0; store.Changed += () => Interlocked.Increment(ref notices);
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => store.AddCollectionMovie(Film, QualityProfile.Q1080p))));
        Assert.Single(results, result => result.Status == "added");
        Assert.Equal(23, results.Count(result => result.Status == "already-wanted"));
        Assert.Single(results.Select(result => result.WishlistId).Distinct());
        Assert.Single(store.Items); Assert.Equal(1, notices);
        Assert.Equal(ScoredQualityProfile.Legacy(QualityProfile.Q1080p).Id, store.Items[0].ProfileId);
    }

    [Fact]
    public void Atomic_add_reuses_manual_title_identity_but_respects_conflicting_provider_ids()
    {
        var manualStore = Store();
        var manual = new WishlistItem { Title = Film.Title, Type = MediaType.Movie, Status = WishlistStatus.Paused };
        manualStore.Add(manual);
        Assert.Equal(manual.Id, manualStore.AddCollectionMovie(Film, QualityProfile.Q2160p).WishlistId);
        Assert.Equal(WishlistStatus.Paused, manual.Status); Assert.Null(manual.TmdbId);
        var conflictingStore = Store();
        conflictingStore.Add(new() { Type = MediaType.Movie, Title = Film.Title, Year = Film.Year, TmdbId = 999 });
        Assert.Equal("added", conflictingStore.AddCollectionMovie(Film, QualityProfile.Q1080p).Status);
        Assert.Equal(2, conflictingStore.Items.Count);
    }

    [Fact]
    public void Normal_and_collection_add_share_normalization_and_keep_memory_on_failed_write()
    {
        var store = new WishlistStore(() => null, _ => throw new IOException("fixture write failure"));
        var normal = new WishlistItem { Title = "Other Fixture", Quality = QualityProfile.Q720p };
        store.Add(normal);
        Assert.Equal(ScoredQualityProfile.Legacy(normal.Quality).Id, normal.ProfileId);
        Assert.Equal("added", store.AddCollectionMovie(Film, QualityProfile.Q1080p).Status);
        Assert.Equal("already-wanted", store.AddCollectionMovie(Film, QualityProfile.Q2160p).Status);
        Assert.Equal(2, store.Items.Count);
    }
}
