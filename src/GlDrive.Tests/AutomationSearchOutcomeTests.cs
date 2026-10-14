using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationSearchOutcomeTests
{
    private static SearchOutcome Complete(params SearchResult[] results) =>
        new(results, "SITE SEARCH", null, false, false, false, []);
    private static AutomationService.SearchTarget Target(string id, SearchOutcome outcome) =>
        new(id, id + " name", (_, _) => Task.FromResult(outcome));

    [Fact]
    public async Task Partial_and_unavailable_servers_keep_good_results_and_describe_coverage()
    {
        var built = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        var partial = new SearchOutcome([new SearchResult { ReleaseName = "Wanted.Release" }],
            "Cached index", built, true, false, false, [new("/archive", "Connection pool is busy")]);
        var response = await AutomationService.QuerySearchTargetsAsync(
            [Target("partial", partial), new("offline", "offline name", (_, _) => throw new IOException("Disconnected"))],
            "wanted", 200, default);

        Assert.Equal("Wanted.Release", Assert.Single(response.Results).ReleaseName);
        Assert.Equal("partial", response.Results[0].ServerId);
        Assert.Equal(1, response.Total);
        Assert.True(response.IsPartial);
        Assert.Equal(2, response.Errors.Count);
        Assert.Contains(response.Errors, error => error.ServerId == "partial" && error.Message.Contains("Connection pool is busy"));
        Assert.Contains(response.Errors, error => error.ServerId == "offline" && error.Message == "Disconnected");
        Assert.Equal("Cached index", response.Servers[0].Method);
        Assert.Equal(built, response.Servers[0].IndexBuiltAtUtc);
        Assert.Equal(1, response.Servers[0].FailedListings);
        Assert.True(response.Servers[1].Unavailable);
    }

    [Fact]
    public async Task Empty_timed_out_search_is_not_a_clean_no_match_response()
    {
        var timedOut = new SearchOutcome([], "Live crawl", null, true, true, false, []);
        var response = await AutomationService.QuerySearchTargetsAsync([Target("busy", timedOut)], "query", 20, default);
        Assert.Empty(response.Results);
        Assert.True(response.IsPartial);
        Assert.True(response.Servers[0].TimedOut);
        Assert.Contains("time limit", Assert.Single(response.Errors).Message);
    }

    [Fact]
    public async Task Genuine_no_matches_remain_clean_and_report_the_method()
    {
        var response = await AutomationService.QuerySearchTargetsAsync([Target("ready", Complete())], "query", 20, default);
        Assert.Empty(response.Results);
        Assert.Empty(response.Errors);
        Assert.False(response.IsPartial);
        Assert.False(response.Servers[0].Unavailable);
        Assert.Equal("SITE SEARCH", response.Servers[0].Method);
    }

    [Fact]
    public async Task Response_preserves_legacy_json_fields_and_does_not_mutate_source_results()
    {
        var source = new SearchResult
        {
            ReleaseName = "Movie", RemotePath = "/MOVIES/Movie", Category = "MOVIES", Size = 123,
            ServerId = "original", Files = ["movie.mkv"]
        };
        var response = await AutomationService.QuerySearchTargetsAsync([Target("server", Complete(source))], "Movie", 20, default);
        source.Files.Clear();
        Assert.Equal("original", source.ServerId);
        var json = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("errors").GetArrayLength());
        var row = json.GetProperty("results")[0];
        Assert.Equal("Movie", row.GetProperty("releaseName").GetString());
        Assert.Equal("server", row.GetProperty("serverId").GetString());
        Assert.Equal("/MOVIES/Movie", row.GetProperty("remotePath").GetString());
        Assert.Equal("movie.mkv", row.GetProperty("files")[0].GetString());
        Assert.Equal("SITE SEARCH", json.GetProperty("servers")[0].GetProperty("method").GetString());
    }

    [Fact]
    public async Task Api_limit_keeps_total_and_marks_truncation()
    {
        var response = await AutomationService.QuerySearchTargetsAsync(
            [Target("server", Complete(new(), new(), new()))], "query", 2, default);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal(3, response.Total);
        Assert.Equal(3, response.Servers[0].ResultCount);
        Assert.True(response.Truncated);
        Assert.True(response.IsPartial);
        Assert.Empty(response.Errors);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_even_if_search_ignores_its_token()
    {
        using var cts = new CancellationTokenSource();
        var delayed = new TaskCompletionSource<SearchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = AutomationService.QuerySearchTargetsAsync([new("late", "late", (_, _) => delayed.Task)], "query", 20, cts.Token);
        cts.Cancel();
        delayed.SetResult(Complete(new SearchResult()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Missing_service_or_local_timeout_does_not_discard_another_server()
    {
        var response = await AutomationService.QuerySearchTargetsAsync(
            [new("missing", "missing", null), new("timeout", "timeout", (_, _) => throw new OperationCanceledException()),
                Target("good", Complete(new SearchResult()))], "query", 20, default);
        Assert.Single(response.Results);
        Assert.Equal(2, response.Errors.Count);
        Assert.Equal(2, response.Servers.Count(server => server.Unavailable));
    }

    [Fact]
    public async Task No_mounted_servers_is_an_explicit_unavailable_error()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AutomationService.QuerySearchTargetsAsync([], "query", 20, default));
    }
}
