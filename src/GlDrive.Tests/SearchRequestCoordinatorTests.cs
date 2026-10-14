using System.IO;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class SearchRequestCoordinatorTests
{
    [Fact]
    public void Starting_new_query_cancels_previous_and_preserves_each_captured_query()
    {
        using var coordinator = new SearchRequestCoordinator();
        using var old = coordinator.Begin("  first query  ");
        using var current = coordinator.Begin("second query");
        Assert.Equal("first query", old.Query);
        Assert.Equal("second query", current.Query);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(coordinator.CanPublish(old));
        Assert.True(coordinator.CanPublish(current));
        old.Dispose();
        Assert.True(coordinator.CanPublish(current));
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("disposed")]
    public void Progress_callback_checks_ownership_when_the_queued_UI_callback_executes(string endState)
    {
        var context = new QueuedContext();
        var previousContext = SynchronizationContext.Current;
        using var coordinator = new SearchRequestCoordinator();
        using var request = coordinator.Begin("old");
        var messages = new List<string>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var progress = coordinator.ProgressFor(request, messages.Add);
            progress.Report("current progress");
            context.Drain();
            Assert.Equal("current progress", Assert.Single(messages));
            progress.Report("late progress");
            Assert.Single(messages);
            switch (endState)
            {
                case "superseded": using (coordinator.Begin("new")) { } break;
                case "completed": request.Dispose(); break;
                case "cancelled": coordinator.Cancel(); break;
                case "disposed": coordinator.Dispose(); break;
            }
            context.Drain();
            Assert.Equal("current progress", Assert.Single(messages));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    [Fact]
    public async Task One_failed_site_does_not_throw_away_other_sites_matches()
    {
        using var coordinator = new SearchRequestCoordinator();
        using var request = coordinator.Begin("wanted");
        var rows = await Task.WhenAll(
            SearchRequestCoordinator.SearchSiteAsync(request, "working", (query, _, _) =>
            {
                Assert.Equal("wanted", query);
                return Task.FromResult(Outcome(new SearchResult { ReleaseName = "Wanted.Release" }));
            }),
            SearchRequestCoordinator.SearchSiteAsync(request, "offline", (_, _, _) => throw new IOException("Pool unavailable")));
        Assert.Equal("Wanted.Release", Assert.Single(rows[0].Outcome!.Results).ReleaseName);
        Assert.Equal("Pool unavailable", rows[1].Error);
        var summary = SearchRequestCoordinator.Describe(rows, 2, DateTime.UtcNow);
        Assert.Contains("1 result(s)", summary);
        Assert.Contains("1/2 server(s) unavailable", summary);
        Assert.Contains("offline: unavailable", summary);
    }

    [Fact]
    public async Task Completed_site_is_available_while_another_site_is_still_waiting()
    {
        using var coordinator = new SearchRequestCoordinator();
        using var request = coordinator.Begin("wanted");
        var delayed = new TaskCompletionSource<SearchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = SearchRequestCoordinator.SearchSiteAsync(request, "slow", (_, _, _) => delayed.Task);
        var fast = await SearchRequestCoordinator.SearchSiteAsync(request, "fast", (_, _, _) => Task.FromResult(Outcome(new SearchResult())));
        Assert.False(slow.IsCompleted);
        Assert.Single(fast.Outcome!.Results);
        Assert.Contains("1/2 servers checked", SearchRequestCoordinator.Describe([fast], 2, DateTime.UtcNow));
        delayed.SetResult(Outcome());
        await slow;
    }

    [Fact]
    public async Task Cancelled_site_cannot_publish_results_even_when_it_ignores_the_token()
    {
        using var coordinator = new SearchRequestCoordinator();
        using var request = coordinator.Begin("old");
        var delayed = new TaskCompletionSource<SearchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = SearchRequestCoordinator.SearchSiteAsync(request, "late", (_, _, _) => delayed.Task);
        coordinator.Cancel();
        delayed.SetResult(Outcome(new SearchResult()));
        var row = await task;
        Assert.Null(row.Outcome);
        Assert.Null(row.Error);
        Assert.False(coordinator.CanPublish(request));
    }

    [Fact]
    public void Partial_empty_search_keeps_failure_and_timeout_visible()
    {
        var result = new SearchOutcome([], "Live crawl", null, true, true, false,
            [new SearchListingFailure("/archive", "Connection pool is busy")]);
        var summary = SearchRequestCoordinator.Describe([new("busy", result, null)], 1, DateTime.UtcNow);
        Assert.Contains("busy: timed out", summary);
        Assert.Contains("partial search", summary);
        Assert.Contains("Connection pool is busy", summary);
    }

    [Fact]
    public void Cache_age_and_truncation_survive_final_summary()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var result = new SearchOutcome([], "Cached index", now.AddMinutes(-73), false, false, true, []);
        var summary = SearchRequestCoordinator.Describe([new("cached", result, null)], 1, now);
        Assert.Contains("result limit reached", summary);
        Assert.Contains("cached index 73m old", summary);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 20)]
    public void Other_media_providers_cannot_erase_the_FTP_warning(int tmdb, int torrent)
    {
        var status = SearchRequestCoordinator.DescribeMedia(tmdb, torrent, "busy: timed out — partial search");
        Assert.Contains("FTP: busy: timed out — partial search", status);
        Assert.DoesNotContain("No results", status);
    }

    private static SearchOutcome Outcome(params SearchResult[] results) => new(results, "Live crawl", null, false, false, false, []);

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Enqueue((d, state));
        internal void Drain()
        {
            while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}
