using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class SearchConfigurationSnapshotTests
{
    private static JsonArray Patch(string json) => JsonNode.Parse(json)!.AsArray();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static AppConfig Configuration(SearchMethod method = SearchMethod.CachedIndex) => new()
    {
        Servers = [new() { Id = "snapshot-site", Search = new()
        {
            Method = method, SearchPaths = ["/first", "/second"], MaxDepth = 0
        } }]
    };

    private static readonly string ChangeSearch = """
        [{"op":"replace","path":"/servers/0/search/searchPaths","value":["/new"]},
         {"op":"replace","path":"/servers/0/search/maxDepth","value":1}]
        """;

    private static FtpListItem[] Listing(string path) => path.Count(c => c == '/') == 1
        ? [new() { Name = "container", FullName = path + "/container", Type = FtpObjectType.Directory }]
        : [];

    [Fact]
    public async Task Notification_only_patch_does_not_invalidate_a_suspended_index_crawl()
    {
        var config = Configuration();
        var paths = config.Servers[0].Search.SearchPaths;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        using var search = new FtpSearchService(null!, config.Servers[0].Search, async (path, ct) =>
        {
            calls.Enqueue(path);
            if (path == "/first") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return [];
        }, (_, _) => Task.FromResult(new FtpReply()));
        var crawl = search.RefreshIndex();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            new ConfigurationAutomation(config, _ => { }).Apply(Patch("""
                [{"op":"replace","path":"/servers/0/notifications/watchPath","value":"/changed"}]
                """));
        }
        finally { release.TrySetResult(); }

        await crawl.WaitAsync(Deadline);
        Assert.Same(paths, config.Servers[0].Search.SearchPaths);
        Assert.Equal(new[] { "/first", "/second" }, calls.ToArray());
        Assert.False(search.GetCachedIndexSnapshot().IsPartial);
    }

    [Theory]
    [InlineData(SearchMethod.CachedIndex)]
    [InlineData(SearchMethod.LiveCrawl)]
    [InlineData(SearchMethod.SiteSearch)]
    [InlineData(SearchMethod.Auto)]
    public async Task Search_edits_preserve_active_roots_and_depth_and_apply_to_the_next_run(SearchMethod method)
    {
        var config = Configuration(method);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        using var search = new FtpSearchService(null!, config.Servers[0].Search, async (path, ct) =>
        {
            calls.Enqueue(path);
            if (path == "/first" && method is SearchMethod.CachedIndex or SearchMethod.LiveCrawl)
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Listing(path);
        }, async (_, ct) =>
        {
            // SITE SEARCH may switch to live crawl after its command completes.
            // Its original roots/depth must survive edits during that await too.
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new FtpReply { Code = "500", Message = "Unsupported" };
        });
        async Task Run()
        {
            if (method == SearchMethod.CachedIndex) await search.RefreshIndex();
            else Assert.False((await search.SearchDetailed("")).IsPartial);
        }

        var pending = Run();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            new ConfigurationAutomation(config, _ => { }).Apply(Patch(ChangeSearch));
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Deadline);
        Assert.Equal(new[] { "/first", "/second" }, calls.ToArray());

        calls.Clear();
        await Run().WaitAsync(Deadline);
        Assert.Equal(new[] { "/new", "/new/container" }, calls.ToArray());
    }

    [Fact]
    public async Task Reschedule_drains_a_suspended_listing_before_crawling_updated_roots_and_depth()
    {
        var config = Configuration();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        using var search = new FtpSearchService(null!, config.Servers[0].Search, async (path, ct) =>
        {
            calls.Enqueue(path);
            if (path == "/first")
            {
                entered.TrySetResult();
                await release.Task;
                ct.ThrowIfCancellationRequested();
            }
            return Listing(path);
        }, (_, _) => Task.FromResult(new FtpReply()));
        search.IndexPublished += _ => published.TrySetResult();
        Task? reschedule = null;
        var automation = new ConfigurationAutomation(config, _ => { },
            settingsApplied: () => reschedule = search.RescheduleIndexerAsync());
        search.StartIndexer();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            automation.Apply(Patch(ChangeSearch));
            Assert.NotNull(reschedule);
            Assert.False(reschedule.IsCompleted);
            Assert.Equal(new[] { "/first" }, calls.ToArray());
        }
        finally { release.TrySetResult(); }

        await reschedule!.WaitAsync(Deadline);
        await published.Task.WaitAsync(Deadline);
        await search.StopIndexerAsync(Deadline);
        Assert.Equal(new[] { "/first", "/new", "/new/container" }, calls.ToArray());
        Assert.All(search.GetCachedIndexSnapshot().Entries,
            entry => Assert.StartsWith("/new/", entry.RemotePath));
    }
}
