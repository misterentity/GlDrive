using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class SearchDetailedOutcomeTests
{
    private static FtpListItem Dir(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..], FullName = path, Type = FtpObjectType.Directory
    };
    private static Task<FtpReply> Unsupported(string _, CancellationToken ct) => Task.FromResult(new FtpReply { Code = "500", Message = "Unknown command" });
    private static DataCommandRejectedException Denied() => new("LIST", new FtpReply { Code = "550", Message = "Permission denied" }, true);

    [Theory]
    [InlineData("Small.Prophets.S01.1080p.WEB-GROUP", "prophets 1080p", true)]
    [InlineData("Small_Prophets-S01.1080p", "1080P small", true)]
    [InlineData("Small.Prophets.S01.720p", "small 1080p", false)]
    [InlineData("Small.Prophets.S01", "   ", true)]
    public void Multiword_matching_uses_all_terms_regardless_of_order(string release, string query, bool expected) =>
        Assert.Equal(expected, FtpSearchService.MatchesQuery(release, query));

    [Fact]
    public async Task Auto_cache_hit_avoids_borrowing_a_busy_pool_and_reports_index_age()
    {
        var listCalls = 0;
        var siteCalls = 0;
        await using var f = new Fixture(new() { Method = SearchMethod.Auto, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => { listCalls++; return Task.FromResult(new[] { Dir("/TV/Small.Prophets.1080p-GRP") }); },
            (_, _) => { siteCalls++; throw new InvalidOperationException("Busy pool must not be borrowed"); });
        await f.Search.RefreshIndex();
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Single(result.Results);
        Assert.Equal("Cached index", result.Method);
        Assert.NotNull(result.IndexBuiltAtUtc);
        Assert.False(result.IsPartial);
        Assert.Equal(1, listCalls);
        Assert.Equal(0, siteCalls);
    }

    [Theory]
    [InlineData(SearchMethod.Auto, "No results found")]
    [InlineData(SearchMethod.Auto, "/TV/Small.Prophets.1080p-GRP [unrecognized script format]")]
    [InlineData(SearchMethod.SiteSearch, "No results found")]
    public async Task Empty_or_unrecognized_server_reply_falls_back_to_live(SearchMethod method, string reply)
    {
        await using var f = new Fixture(new() { Method = method, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => Task.FromResult(new[] { Dir("/TV/Small.Prophets.1080p-GRP") }),
            (_, _) => Task.FromResult(new FtpReply { Code = "200", Message = reply }));
        var result = await f.Search.SearchDetailed("1080p prophets");
        Assert.Equal("Live crawl", result.Method);
        Assert.Equal("Small.Prophets.1080p-GRP", Assert.Single(result.Results).ReleaseName);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task Stale_populated_cache_miss_falls_through_to_new_live_release()
    {
        var release = "Old.Show.720p";
        await using var f = new Fixture(new() { Method = SearchMethod.Auto, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => Task.FromResult(new[] { Dir("/TV/" + release) }), Unsupported);
        await f.Search.RefreshIndex();
        release = "Small.Prophets.1080p";
        var result = await f.Search.SearchDetailed("small 1080p");
        Assert.Equal("Live crawl", result.Method);
        Assert.Single(result.Results);
        Assert.Null(result.IndexBuiltAtUtc);
    }

    [Fact]
    public async Task Site_query_uses_one_term_then_filters_all_terms_locally()
    {
        string? sent = null;
        await using var f = new Fixture(new() { Method = SearchMethod.SiteSearch, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => throw new InvalidOperationException("No live fallback needed"),
            (query, _) =>
            {
                sent = query;
                return Task.FromResult(new FtpReply { Code = "200", InfoMessages =
                    "/TV/Small.Prophets.1080p-GRP (2F/12M/1d)\n/TV/Small.Prophets.720p-GRP (2F/8M/1d)" });
            });
        var result = await f.Search.SearchDetailed("1080p prophets");
        Assert.Equal("prophets", sent);
        Assert.Equal("Small.Prophets.1080p-GRP", Assert.Single(result.Results).ReleaseName);
        Assert.Equal("SITE SEARCH", result.Method);
    }

    [Fact]
    public async Task One_internal_borrow_timeout_retries_without_reporting_a_false_empty_root()
    {
        var attempts = 0;
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => ++attempts == 1 ? throw new OperationCanceledException("Internal borrow deadline")
                : Task.FromResult(new[] { Dir("/TV/Small.Prophets.1080p") }), Unsupported);
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Equal(2, attempts);
        Assert.Single(result.Results);
        Assert.False(result.IsPartial);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Denied_root_is_not_retried_and_successful_sibling_results_are_partial()
    {
        var deniedCalls = 0;
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/archive", "/TV"], MaxDepth = 0 },
            (path, _) => path == "/archive" ? Fail() : Task.FromResult(new[] { Dir("/TV/Small.Prophets.1080p") }), Unsupported);
        Task<FtpListItem[]> Fail() { deniedCalls++; throw Denied(); }
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Equal(1, deniedCalls);
        Assert.Single(result.Results);
        Assert.True(result.IsPartial);
        Assert.Equal("/archive", Assert.Single(result.Failures).Path);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Failed_empty_search_is_distinct_from_successful_no_matches()
    {
        var failed = true;
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => failed ? throw Denied() : Task.FromResult(Array.Empty<FtpListItem>()), Unsupported);
        var incomplete = await f.Search.SearchDetailed("wanted");
        failed = false;
        var complete = await f.Search.SearchDetailed("wanted");
        Assert.Empty(incomplete.Results);
        Assert.True(incomplete.IsPartial);
        Assert.Single(incomplete.Failures);
        Assert.Empty(complete.Results);
        Assert.False(complete.IsPartial);
    }

    [Fact]
    public async Task Incomplete_cached_index_keeps_its_coverage_warning_with_hits()
    {
        await using var f = new Fixture(new() { Method = SearchMethod.Auto, SearchPaths = ["/TV", "/archive"], MaxDepth = 0 },
            (path, _) => path == "/archive" ? throw Denied() : Task.FromResult(new[] { Dir("/TV/Small.Prophets.1080p") }), Unsupported);
        await f.Search.RefreshIndex();
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Single(result.Results);
        Assert.Equal("Cached index", result.Method);
        Assert.True(result.IsPartial);
        Assert.Equal("/archive", Assert.Single(result.Failures).Path);
    }

    [Fact]
    public async Task Deadline_returns_found_results_with_timeout_while_caller_cancel_throws()
    {
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 1 },
            async (path, ct) =>
            {
                if (path == "/TV") return [Dir("/TV/Small.Prophets.1080p")];
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            }, Unsupported, TimeSpan.FromMilliseconds(80));
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Single(result.Results);
        Assert.True(result.TimedOut);
        Assert.True(result.IsPartial);
        // A 20 ms caller timer racing the same 80 ms search deadline is not
        // deterministic under a loaded test runner: timer callbacks can execute
        // out of due-time order. Cancel an actual in-flight listing explicitly,
        // with no competing internal deadline in this caller-cancellation case.
        var listingStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var callerFixture = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 1 },
            async (path, ct) =>
            {
                if (path == "/TV") return [Dir("/TV/Small.Prophets.1080p")];
                listingStarted.TrySetResult(ct);
                await releaseListing.Task.WaitAsync(ct);
                return [];
            }, Unsupported, Timeout.InfiniteTimeSpan);
        using var cancel = new CancellationTokenSource();
        var search = callerFixture.Search.SearchDetailed("prophets", ct: cancel.Token);
        try
        {
            var operationToken = await listingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(search.IsCompleted);
            cancel.Cancel();
            Assert.True(operationToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancel.Cancel();
            releaseListing.TrySetResult();
            try { await search.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task A_listing_that_finishes_after_the_deadline_still_reports_timeout()
    {
        // Cancellation callbacks run in reverse registration order. Complete the
        // listing in the operation token's newest callback, while its older
        // callback has not yet cancelled the traversal token. Holding that
        // callback until the search finishes makes the production race explicit.
        var listing = new TaskCompletionSource<FtpListItem[]>();
        using var searchFinished = new ManualResetEventSlim();
        var finishedBeforeCancellationPropagated = false;
        CancellationTokenRegistration completionRegistration = default;
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, ct) =>
            {
                completionRegistration = ct.Register(() =>
                {
                    listing.TrySetResult([]);
                    finishedBeforeCancellationPropagated = searchFinished.Wait(TimeSpan.FromSeconds(5));
                });
                return listing.Task;
            }, Unsupported, TimeSpan.FromMilliseconds(100));
        SearchOutcome result;
        try
        {
            // The test runner's synchronization context must not defer the
            // listing continuation until after cancellation has propagated.
            result = await Task.Run(async () =>
            {
                try { return await f.Search.SearchDetailed("wanted"); }
                finally { searchFinished.Set(); }
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { completionRegistration.Dispose(); }
        Assert.True(finishedBeforeCancellationPropagated);
        Assert.Empty(result.Results);
        Assert.True(result.TimedOut);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_hidden_by_a_transport_exception()
    {
        using var cancel = new CancellationTokenSource();
        await using var f = new Fixture(new() { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) =>
            {
                cancel.Cancel();
                throw new System.IO.IOException("Socket closed during cancellation");
            }, Unsupported);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Search.SearchDetailed("wanted", ct: cancel.Token));
    }

    [Fact]
    public async Task Result_cap_is_reported_for_live_and_cache_searches()
    {
        var config = new SearchConfig { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"], MaxDepth = 0 };
        await using var f = new Fixture(config,
            (_, _) => Task.FromResult(Enumerable.Range(0, 201).Select(i => Dir($"/TV/Wanted.Show.{i}")).ToArray()), Unsupported);
        var live = await f.Search.SearchDetailed("wanted show");
        Assert.Equal(200, live.Results.Count);
        Assert.True(live.Truncated);
        Assert.True(live.IsPartial);
        await f.Search.RefreshIndex();
        config.Method = SearchMethod.CachedIndex;
        var cached = await f.Search.SearchDetailed("");
        Assert.Equal(200, cached.Results.Count);
        Assert.True(cached.Truncated);
    }

    [Fact]
    public async Task Truncated_site_matches_survive_a_failed_live_fallback()
    {
        var reply = string.Join('\n', Enumerable.Range(0, 200).Select(i =>
            $"/TV/Prophets.{(i == 0 ? "1080p" : "720p")}.{i} (2F/8M/1d)"));
        await using var f = new Fixture(new() { Method = SearchMethod.Auto, SearchPaths = ["/TV"], MaxDepth = 0 },
            (_, _) => throw Denied(), (_, _) => Task.FromResult(new FtpReply { Code = "200", InfoMessages = reply }));
        var result = await f.Search.SearchDetailed("prophets 1080p");
        Assert.Equal("Prophets.1080p.0", Assert.Single(result.Results).ReleaseName);
        Assert.True(result.Truncated);
        Assert.True(result.IsPartial);
        Assert.Single(result.Failures);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly FtpConnectionPool _pool = new(null!, 1);
        public FtpSearchService Search { get; }
        public Fixture(SearchConfig config, FtpSearchService.DirectoryLister list,
            Func<string, CancellationToken, Task<FtpReply>> site, TimeSpan? deadline = null)
        {
            Search = new(_pool, config, list, site, deadline, TimeSpan.Zero);
        }
        public async ValueTask DisposeAsync()
        {
            Search.Dispose();
            await _pool.DisposeAsync();
        }
    }
}
