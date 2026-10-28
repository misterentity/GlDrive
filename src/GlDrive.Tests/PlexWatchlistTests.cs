using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

public sealed class PlexWatchlistTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static PlexWatchlistEntry Entry(string id = "plex://movie/synthetic", string? imdb = "tt1234567") =>
        new(id, "Synthetic Film", MediaType.Movie, 2024, imdb, 100, null);
    private static WishlistItem Mapped(string id = "plex://movie/synthetic", string? imdb = "tt1234567") =>
        PlexWatchlistSync.Map(Entry(id, imdb), new DownloadConfig());

    [Theory]
    [InlineData("2160p", QualityProfile.Q2160p)]
    [InlineData("720p", QualityProfile.Q720p)]
    [InlineData("invalid", QualityProfile.Q1080p)]
    public void Mapping_preserves_external_ids_and_uses_default_quality_profile(string quality, QualityProfile expected)
    {
        var entry = new PlexWatchlistEntry("plex://show/synthetic", "Synthetic Series", MediaType.TvShow, 2024,
            "tt1234568", 123, 456);
        var item = PlexWatchlistSync.Map(entry, new DownloadConfig { QualityDefault = quality });
        Assert.Equal("PlexWatchlist", item.Source);
        Assert.Equal(entry.Id, item.PlexWatchlistId);
        Assert.Equal(entry.ImdbId, item.ImdbId);
        Assert.Equal(entry.TmdbId, item.TmdbId);
        Assert.Equal(entry.TvdbId, item.TvdbId);
        Assert.Equal(entry.Title, item.Title);
        Assert.Equal(entry.Type, item.Type);
        Assert.Equal(expected, item.Quality);
        Assert.Equal(ScoredQualityProfile.Legacy(expected).Id, item.ProfileId);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.Equal(SeriesMonitorMode.Legacy, item.MonitorMode);
        Assert.False(item.Dead);
    }

    [Fact]
    public void Optional_wrong_types_and_one_bad_row_do_not_void_a_page()
    {
        var page = PlexClient.ParseWatchlistPage("""
            {"MediaContainer":{"size":3,"offset":0,"totalSize":3,"Metadata":[
              {"guid":"plex://movie/first","ratingKey":"first","title":"Synthetic Film","type":"movie",
               "year":{"unexpected":true},"Guid":[null,{"id":12},{"id":"imdb://tt1234567"},{"id":"tmdb://100"}]},
              {"guid":"plex://movie/bad","title":{},"type":"movie"},
              {"guid":"plex://show/second","ratingKey":"second","title":"Synthetic Series","type":"show","year":2023,
               "Guid":[{"id":"tvdb://456"},{"id":"tmdb://oops"}]}
            ]}}
            """);
        Assert.Equal(3, page.RawCount);
        Assert.Equal(3, page.TotalSize);
        Assert.Equal(1, page.Skipped);
        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.Items[0].Year);
        Assert.Equal("tt1234567", page.Items[0].ImdbId);
        Assert.Equal(100, page.Items[0].TmdbId);
        Assert.Equal(MediaType.TvShow, page.Items[1].Type);
        Assert.Equal(456, page.Items[1].TvdbId);
        Assert.Null(page.Items[1].TmdbId);
    }

    [Theory]
    [InlineData("{\"MediaContainer\":{\"size\":0,\"Metadata\":[]}}")]
    [InlineData("{\"MediaContainer\":{\"size\":0}}")]
    public void Empty_watchlist_is_a_valid_empty_page(string json)
    {
        var page = PlexClient.ParseWatchlistPage(json);
        Assert.Equal(0, page.RawCount);
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MediaContainer\":[]}")]
    [InlineData("{\"MediaContainer\":{\"size\":1,\"Metadata\":{}}}")]
    [InlineData("{\"MediaContainer\":{\"size\":0,\"offset\":5,\"Metadata\":[]}}")]
    public void Malformed_page_envelope_is_unknown_instead_of_empty_success(string json) =>
        Assert.Throws<PlexException>(() => PlexClient.ParseWatchlistPage(json));

    [Fact]
    public async Task Reader_uses_https_GET_header_token_and_raw_pagination_after_skipped_row()
    {
        using var handler = new Handler((request, count, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("discover.provider.plex.tv", request.RequestUri.Host);
            Assert.Equal("/library/sections/watchlist/all", request.RequestUri.AbsolutePath);
            Assert.Contains("X-Plex-Container-Size=10", request.RequestUri.Query);
            Assert.Contains($"X-Plex-Container-Start={(count == 1 ? 0 : 2)}", request.RequestUri.Query);
            Assert.Equal("synthetic-token", request.Headers.GetValues("X-Plex-Token").Single());
            Assert.DoesNotContain("synthetic-token", request.RequestUri.ToString());
            return Task.FromResult(Response(count == 1 ? """
                {"MediaContainer":{"size":2,"offset":0,"totalSize":3,"Metadata":[
                 {"guid":"plex://movie/first","ratingKey":"first","title":"Synthetic Film","type":"movie"},null]}}
                """ : """
                {"MediaContainer":{"size":1,"offset":2,"totalSize":3,"Metadata":[
                 {"guid":"plex://movie/second","ratingKey":"second","title":"Another Film","type":"movie"}]}}
                """));
        });
        using var client = new PlexClient("test-client", "GlDrive", "test", "synthetic-token", handler);
        var pages = new List<PlexWatchlistPage>();
        await foreach (var page in client.ReadWatchlistAsync(CancellationToken.None)) pages.Add(page);
        Assert.Equal(2, handler.Count);
        Assert.Equal(2, pages.Sum(p => p.Items.Count));
        Assert.Equal(1, pages.Sum(p => p.Skipped));
    }

    [Fact]
    public async Task Repeated_nonempty_page_cannot_create_an_endless_import_loop()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response("""
            {"MediaContainer":{"size":1,"totalSize":2,"Metadata":[
             {"guid":"plex://movie/first","ratingKey":"first","title":"Synthetic Film","type":"movie"}]}}
            """)));
        using var client = new PlexClient("test-client", "GlDrive", "test", "synthetic-token", handler);
        await Assert.ThrowsAsync<PlexException>(async () =>
        {
            await foreach (var _ in client.ReadWatchlistAsync(CancellationToken.None)) { }
        });
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Watchlist_read_cancellation_reaches_transport()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler((_, _, ct) =>
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Response("{}"));
        });
        using var client = new PlexClient("test-client", "GlDrive", "test", "synthetic-token", handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ReadWatchlistAsync(cancel.Token)) { }
        });
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(456, "tt1234568", "?thetvdb=456")]
    [InlineData(null, "tt1234568", "?imdb=tt1234568")]
    public async Task TVMaze_mapping_uses_external_identity_lookup_instead_of_fuzzy_search(int? tvdb, string imdb, string query)
    {
        using var handler = new Handler((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/lookup/shows", request.RequestUri!.AbsolutePath);
            Assert.Equal(query, request.RequestUri.Query);
            return Task.FromResult(Response("""{"id":999,"name":"Synthetic Series","status":"Ended"}"""));
        });
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") },
            _ => Task.CompletedTask);
        var show = await client.LookupExternal(tvdb, imdb);
        Assert.Equal(999, show!.Id);
        Assert.Equal("Ended", show.Status);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Missing_external_TV_identity_is_unknown_without_network_work()
    {
        using var handler = new Handler((_, _, _) => throw new Exception("unexpected HTTP"));
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") },
            _ => Task.CompletedTask);
        Assert.Null(await client.LookupExternal(null, "not-an-imdb-id"));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Offline_TV_lookup_returns_unknown()
    {
        using var handler = new Handler((_, _, _) => throw new HttpRequestException("synthetic offline provider"));
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") },
            _ => Task.CompletedTask);
        Assert.Null(await client.LookupExternal(456, null));
    }

    [Fact]
    public void Reimport_preserves_identity_and_user_changes_to_imported_items()
    {
        var writes = new List<string>();
        var store = new WishlistStore(() => null, writes.Add);
        Assert.Equal(1, store.ImportWatchlistPage([Mapped()], () => true).Added);
        var item = Assert.Single(store.Items);
        var id = item.Id;
        Assert.True(Guid.TryParseExact(id, "N", out _));
        item.Quality = QualityProfile.Q2160p;
        item.ProfileId = "chosen-profile";
        item.Status = WishlistStatus.Paused;
        item.Dead = true;
        store.Update(item);
        var result = store.ImportWatchlistPage([Mapped()], () => true);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Existing);
        item = Assert.Single(store.Items);
        Assert.Equal(id, item.Id);
        Assert.Equal(QualityProfile.Q2160p, item.Quality);
        Assert.Equal("chosen-profile", item.ProfileId);
        Assert.Equal(WishlistStatus.Paused, item.Status);
        Assert.True(item.Dead);
    }

    [Fact]
    public void Existing_manual_identity_is_not_replaced_or_relabelled()
    {
        var manual = new WishlistItem { Id = "manual", Title = "Synthetic Film", Year = 2024, ImdbId = "tt1234567",
            Quality = QualityProfile.Q720p, Status = WishlistStatus.Paused };
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { manual }, Json), _ => { });
        var result = store.ImportWatchlistPage([Mapped()], () => true);
        Assert.Equal(0, result.Added);
        var actual = Assert.Single(store.Items);
        Assert.Equal("manual", actual.Id);
        Assert.NotEqual("PlexWatchlist", actual.Source);
        Assert.Null(actual.PlexWatchlistId);
        Assert.Equal(QualityProfile.Q720p, actual.Quality);
        Assert.Equal(WishlistStatus.Paused, actual.Status);
    }

    [Fact]
    public void Conflicting_external_ids_do_not_merge_identically_named_titles()
    {
        var manual = new WishlistItem { Title = "Synthetic Film", Year = 2024, ImdbId = "tt7654321" };
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { manual }, Json), _ => { });
        Assert.Equal(1, store.ImportWatchlistPage([Mapped()], () => true).Added);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Disabled_import_publishes_and_writes_nothing()
    {
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        Assert.Equal(0, store.ImportWatchlistPage([Mapped()], () => false).Added);
        Assert.Empty(store.Items);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Failed_import_save_retains_memory_and_deduplication()
    {
        var attempts = 0;
        var store = new WishlistStore(() => null, _ => { attempts++; throw new IOException("synthetic write failure"); });
        Assert.Equal(1, store.ImportWatchlistPage([Mapped()], () => true).Added);
        var firstId = Assert.Single(store.Items).Id;
        Assert.Equal(0, store.ImportWatchlistPage([Mapped()], () => true).Added);
        Assert.Equal(firstId, Assert.Single(store.Items).Id);
        Assert.True(attempts >= 1);
    }

    [Fact]
    public void Unreadable_wishlist_quarantines_and_remains_usable_in_memory()
    {
        var quarantined = 0;
        var store = new WishlistStore(() => "{broken", _ => { }, () => quarantined++);
        Assert.Empty(store.Items);
        Assert.Equal(1, quarantined);
        Assert.Equal(1, store.ImportWatchlistPage([Mapped()], () => true).Added);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Watchlist_sync_is_opt_in_and_old_config_remains_valid()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", Json)!;
        Assert.False(config.Plex.WatchlistSyncEnabled);
        Assert.Equal(30, config.Plex.WatchlistSyncMinutes);
        GlDrive.Services.Automation.ConfigurationAutomation.Validate(config);
    }

    [Fact]
    public async Task Disabled_sync_never_constructs_a_provider_or_changes_wishlist()
    {
        var store = new WishlistStore(() => null, _ => throw new Exception("unexpected write"));
        using var sync = new PlexWatchlistSync(new(), store, () => [], () => LibraryIndexSnapshot.Empty,
            () => throw new Exception("unexpected provider"));
        var result = await sync.SyncAsync(force: true);
        Assert.Equal(0, result.Added);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Disabling_during_provider_read_prevents_late_import()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Plex = new() { WatchlistSyncEnabled = true } };
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        async IAsyncEnumerable<PlexWatchlistPage> Read()
        {
            entered.SetResult();
            await release.Task;
            yield return new(0, 1, 1, [Entry()], 0);
        }
        using var sync = new PlexWatchlistSync(config, store, () => [], () => LibraryIndexSnapshot.Empty,
            () => new Reader(_ => Read()));
        var pending = sync.SyncAsync(force: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        config.Plex.WatchlistSyncEnabled = false;
        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(store.Items);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Provider_failure_after_one_page_preserves_imported_items_and_does_not_throw()
    {
        var config = new AppConfig { Plex = new() { WatchlistSyncEnabled = true } };
        var store = new WishlistStore(() => null, _ => { });
        async IAsyncEnumerable<PlexWatchlistPage> Read()
        {
            await Task.Yield();
            yield return new(0, 1, 2, [Entry()], 0);
            throw new HttpRequestException("synthetic offline provider");
        }
        using var sync = new PlexWatchlistSync(config, store, () => [], () => LibraryIndexSnapshot.Empty,
            () => new Reader(_ => Read()));
        var result = await sync.SyncAsync(force: true);
        Assert.Equal(1, result.Added);
        Assert.Equal("Synthetic Film", Assert.Single(store.Items).Title);
    }

    [Fact]
    public async Task Completed_download_reconciles_an_imported_movie_but_preserves_paused_and_manual_choices()
    {
        var watching = Mapped(); watching.Id = "watching";
        var paused = Mapped("plex://movie/paused", "tt1234568"); paused.Id = "paused"; paused.Status = WishlistStatus.Paused;
        var manual = new WishlistItem { Id = "manual", Title = "Synthetic Film", Year = 2024 };
        var config = new AppConfig { Plex = new() { WatchlistSyncEnabled = true } };
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { watching, paused, manual }, Json), _ => { });
        using var sync = new PlexWatchlistSync(config, store, () =>
            [new() { Status = DownloadStatus.Completed, ReleaseName = "Synthetic.Film.2024.1080p.WEB-GROUP", TotalBytes = 100 }],
            () => LibraryIndexSnapshot.Empty, () => new Reader(_ => Pages(new PlexWatchlistPage(0, 0, 0, [], 0))));
        var result = await sync.SyncAsync(force: true);
        Assert.Equal(1, result.Completed);
        Assert.Equal(WishlistStatus.Completed, store.GetById("watching")!.Status);
        Assert.Equal(WishlistStatus.Paused, store.GetById("paused")!.Status);
        Assert.Equal(WishlistStatus.Watching, store.GetById("manual")!.Status);
    }

    [Fact]
    public async Task Disabling_during_TV_metadata_read_prevents_late_metadata_writes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Plex = new() { WatchlistSyncEnabled = true } };
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        var series = new PlexWatchlistEntry("plex://show/synthetic", "Synthetic Series", MediaType.TvShow, 2024,
            "tt1234568", null, 456);
        using var sync = new PlexWatchlistSync(config, store, () => [], () => LibraryIndexSnapshot.Empty,
            () => new Reader(_ => Pages(new PlexWatchlistPage(0, 1, 1, [series], 0))), async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return new WatchlistSeriesMetadata(999, true,
                    [new() { Season = 1, Number = 1, AirAtUtc = DateTimeOffset.UtcNow.AddYears(-1) }]);
            });
        var pending = sync.SyncAsync(force: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var priorWrites = writes;
        config.Plex.WatchlistSyncEnabled = false;
        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(priorWrites, writes);
        var item = Assert.Single(store.Items);
        Assert.Null(item.TvMazeId);
        Assert.False(item.WatchlistSeriesEnded);
        Assert.Equal(WishlistStatus.Watching, item.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agent_cannot_remove_or_mark_dead_a_watchlist_import(bool soft)
    {
        var item = Mapped(); item.Id = "protected";
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { item }, Json), _ => { });
        var validator = new WishlistPruneValidator(() => store);
        var decision = validator.Validate(new AgentChange
        { Target = "/wishlist/items/protected", After = soft ? new { dead = true } : null }, new());
        Assert.False(decision.Ok);
        Assert.Null(decision.Mutate);
        Assert.False(Assert.Single(store.Items).Dead);
    }

    [Fact]
    public void Agent_prune_rechecks_source_at_mutation_boundary()
    {
        var item = new WishlistItem { Id = "changed", Title = "Synthetic Film" };
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { item }, Json), _ => { });
        var validator = new WishlistPruneValidator(() => store);
        var decision = validator.Validate(new AgentChange { Target = "/wishlist/items/changed", After = null }, new());
        Assert.True(decision.Ok);
        store.GetById("changed")!.Source = "PlexWatchlist";
        decision.Mutate!(new());
        Assert.Single(store.Items);
    }

    private static async IAsyncEnumerable<PlexWatchlistPage> Pages(params PlexWatchlistPage[] pages)
    {
        foreach (var page in pages) { await Task.Yield(); yield return page; }
    }
    private sealed class Reader(Func<CancellationToken, IAsyncEnumerable<PlexWatchlistPage>> read) : IPlexWatchlistReader
    {
        public IAsyncEnumerable<PlexWatchlistPage> ReadWatchlistAsync(CancellationToken cancellationToken) => read(cancellationToken);
        public void Dispose() { }
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, ++Count, cancellationToken);
    }
}
