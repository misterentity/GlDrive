using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Filesystem;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class LiveConfigConsumerTests
{
    [Fact]
    public async Task Reschedule_does_not_restart_a_crawler_disposed_during_its_previous_listing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listings = 0;
        using var search = new FtpSearchService(null!, new SearchConfig(), async (_, ct) =>
        {
            Interlocked.Increment(ref listings);
            entered.TrySetResult();
            await release.Task;
            ct.ThrowIfCancellationRequested();
            return [];
        }, (_, _) => Task.FromResult(new FtpReply()));
        search.StartIndexer();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reschedule = search.RescheduleIndexerAsync();
        Assert.False(reschedule.IsCompleted);
        search.Dispose();
        release.SetResult();
        await reschedule.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, listings);
    }

    [Fact]
    public void Cache_ttl_changes_affect_existing_entries()
    {
        var cache = new DirectoryCache();
        cache.Set("/release", [new FtpListItem { Name = "file" }]);
        Assert.True(cache.TryGet("/release", out _));
        cache.UpdateOptions(0, 500);
        Assert.False(cache.TryGet("/release", out _));
        Assert.Null(cache.FindItem("/release/file"));
    }

    [Fact]
    public void Cache_capacity_reduction_evicts_existing_entries_and_bounds_future_inserts()
    {
        var cache = new DirectoryCache(maxEntries: 100);
        for (var i = 0; i < 20; i++) cache.Set($"/{i}", []);
        cache.UpdateOptions(30, 2);
        Assert.InRange(Enumerable.Range(0, 20).Count(i => cache.TryGet($"/{i}", out _)), 1, 2);
        cache.Set("/new", []);
        Assert.True(cache.TryGet("/new", out _));
        Assert.InRange(Enumerable.Range(0, 20).Count(i => cache.TryGet($"/{i}", out _)), 0, 1);
    }

    [Fact]
    public void Server_speed_limit_changes_without_replacing_downloader()
    {
        var downloader = new StreamingDownloader(null!, "test", new(), 10, null);
        Assert.Equal(10 * 1024L, downloader.ServerBytesPerSecond);
        downloader.UpdateSpeedLimit(25);
        Assert.Equal(25 * 1024L, downloader.ServerBytesPerSecond);
        downloader.UpdateSpeedLimit(0);
        Assert.Equal(0, downloader.ServerBytesPerSecond);
    }

    [Fact]
    public void Affiliation_is_rechecked_after_settings_mutation()
    {
        var server = new ServerConfig();
        const string release = "Example.Movie.2026.1080p-GROUP";
        Assert.False(LiveSpreadPolicy.IsAffiliated(server, release));
        server.SpreadSite.Affils.Add("GROUP");
        Assert.True(LiveSpreadPolicy.IsAffiliated(server, release));
        server.SpreadSite.Affils.Clear();
        Assert.False(LiveSpreadPolicy.IsAffiliated(server, release));
    }

    [Theory]
    [InlineData("example.nfo")]
    [InlineData("CD1/example.nfo")]
    public void Dispatch_rechecks_file_rules_using_basename(string file)
    {
        var server = new ServerConfig();
        var spread = new SpreadConfig();
        var rules = new SkiplistEvaluator();
        SkiplistAction Evaluate() => LiveSpreadPolicy.FileAction(rules, server, spread, "VIDEO", "Example.Movie.2026.1080p-GROUP", file);
        Assert.Equal(SkiplistAction.Allow, Evaluate());
        server.SpreadSite.Skiplist.Add(new() { Pattern = "example.nfo", MatchDirectories = false });
        Assert.Equal(SkiplistAction.Deny, Evaluate());
        server.SpreadSite.Skiplist.Clear();
        Assert.Equal(SkiplistAction.Allow, Evaluate());
    }

    [Fact]
    public void Dispatch_rechecks_directory_and_mapping_rules()
    {
        var server = new ServerConfig();
        var spread = new SpreadConfig();
        var rules = new SkiplistEvaluator();
        SkiplistAction Evaluate() => LiveSpreadPolicy.FileAction(rules, server, spread, "VIDEO", "Example.Movie.2026.1080p-GROUP", "file.rar");
        server.SpreadSite.Skiplist.Add(new() { Pattern = "Example.*", MatchFiles = false });
        Assert.Equal(SkiplistAction.Deny, Evaluate());
        server.SpreadSite.Skiplist.Clear();
        server.SpreadSite.SectionMappings.Add(new() { IrcSection = "VIDEO", TagRules = [new() { Pattern = "*.rar", Action = SkiplistAction.Unique }] });
        Assert.Equal(SkiplistAction.Unique, Evaluate());
    }
}
