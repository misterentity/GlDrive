using System.IO;
using System.Reflection;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RequestSourceSearchLifetimeTests
{
    private const string Release = "Wanted.Show.S01E01.1080p.WEB-GRP";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FtpListItem[] Match() => [new() { Name = Release, FullName = "/TV/" + Release, Type = FtpObjectType.Directory }];

    [Theory]
    [InlineData(SearchMethod.LiveCrawl)]
    [InlineData(SearchMethod.SiteSearch)]
    public async Task First_source_returns_while_losing_command_drains_without_cancellation(SearchMethod method)
    {
        using var f = new Fixture();
        var slowStarted = Signal();
        var slowFinish = Signal();
        var slowFinished = Signal();
        CancellationToken wireToken = default;
        var slowCommands = 0;
        f.Add("slow", method, async ct =>
        {
            Interlocked.Increment(ref slowCommands);
            wireToken = ct;
            slowStarted.TrySetResult();
            try { await slowFinish.Task.WaitAsync(ct); }
            finally { slowFinished.TrySetResult(); }
        });
        f.Add("fast", SearchMethod.LiveCrawl, async ct => await slowStarted.Task.WaitAsync(ct));

        try
        {
            var found = await f.Manager.SearchRequestSource(Release, [], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("fast", found?.ServerId);
            Assert.False(slowFinished.Task.IsCompleted);
            Assert.False(wireToken.IsCancellationRequested);
        }
        finally { slowFinish.TrySetResult(); }
        await slowFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, slowCommands);
    }

    [Theory]
    [InlineData(SearchMethod.LiveCrawl, true)]
    [InlineData(SearchMethod.SiteSearch, true)]
    [InlineData(SearchMethod.LiveCrawl, false)]
    [InlineData(SearchMethod.SiteSearch, false)]
    public async Task Source_budget_stops_traversal_but_allows_current_command_to_drain(SearchMethod method, bool request)
    {
        using var f = new Fixture();
        var started = Signal();
        var finish = Signal();
        CancellationToken wireToken = default;
        var commands = 0;
        f.Add("slow", method, async ct =>
        {
            Interlocked.Increment(ref commands);
            wireToken = ct;
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
        });
        var lookup = request
            ? f.Manager.SearchRequestSource(Release, [], CancellationToken.None)
            : f.Manager.SearchReleaseOnServers(Release, [], CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The minimum configured source-search budget is five seconds.
            await Task.Delay(TimeSpan.FromSeconds(5.2));
            Assert.False(wireToken.IsCancellationRequested);
            Assert.False(lookup.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        Assert.Null(await lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_caller_cancellation_still_interrupts_the_wire_command(bool request)
    {
        using var f = new Fixture();
        using var caller = new CancellationTokenSource();
        var started = Signal();
        CancellationToken wireToken = default;
        f.Add("slow", SearchMethod.LiveCrawl, async ct =>
        {
            wireToken = ct;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        var lookup = request
            ? f.Manager.SearchRequestSource(Release, [], caller.Token)
            : f.Manager.SearchReleaseOnServers(Release, [], caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(wireToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Ending_race_does_not_interrupt_wire_command_or_return_its_late_match()
    {
        using var f = new Fixture();
        using var stop = new CancellationTokenSource();
        var started = Signal();
        var finish = Signal();
        var commands = 0;
        CancellationToken wireToken = default;
        f.Add("slow", SearchMethod.LiveCrawl, async ct =>
        {
            commands++;
            wireToken = ct;
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
        });
        var lookup = f.Manager.SearchReleaseOnServers(Release, [], CancellationToken.None, stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            Assert.False(wireToken.IsCancellationRequested);
            Assert.False(lookup.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_service_hard_deadline_still_bounds_an_unresponsive_command(bool request)
    {
        using var f = new Fixture();
        CancellationToken wireToken = default;
        f.Add("slow", SearchMethod.LiveCrawl, async ct =>
        {
            wireToken = ct;
            await Task.Delay(Timeout.Infinite, ct);
        }, deadline: TimeSpan.FromMilliseconds(100));
        var lookup = request
            ? f.Manager.SearchRequestSource(Release, [], CancellationToken.None)
            : f.Manager.SearchReleaseOnServers(Release, [], CancellationToken.None);
        Assert.Null(await lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(wireToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Excluded_site_and_never_source_are_not_selected_but_destination_skiplist_does_not_reject_source()
    {
        using var f = new Fixture();
        var excludedCommands = 0;
        f.Add("excluded", SearchMethod.LiveCrawl, _ => { excludedCommands++; return Task.CompletedTask; });
        var never = f.Add("never", SearchMethod.LiveCrawl, _ => Task.CompletedTask);
        never.SpreadSite.NeverSource = true;
        var allowed = f.Add("allowed", SearchMethod.LiveCrawl, _ => Task.CompletedTask);
        allowed.SpreadSite.Skiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });

        var found = await f.Manager.SearchRequestSource(Release, ["excluded"], CancellationToken.None);
        Assert.Equal("allowed", found?.ServerId);
        Assert.Equal(0, excludedCommands);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-request-source-" + Guid.NewGuid().ToString("N"));
        private readonly AppConfig _config = new();
        private readonly Dictionary<string, FtpSearchService> _searches = [];
        public SpreadManager Manager { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            _config.Spread.AlternateSourceSearchTimeoutSeconds = 5;
            Manager = new SpreadManager(_config, _directory) { _getSearchService = id => _searches.GetValueOrDefault(id) };
        }

        public ServerConfig Add(string id, SearchMethod method, Func<CancellationToken, Task> command, TimeSpan? deadline = null)
        {
            var config = new ServerConfig { Id = id, Name = id };
            _config.Servers.Add(config);
            var pool = new FtpConnectionPool(null!, 1);
            ((Dictionary<string, FtpConnectionPool>)typeof(SpreadManager)
                .GetField("_spreadPools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Manager)!).Add(id, pool);
            _searches[id] = new FtpSearchService(pool, new SearchConfig { Method = method, SearchPaths = ["/TV", "/Movies"] },
                async (_, ct) => { await command(ct); return Match(); },
                async (_, ct) => { await command(ct); return new FtpReply { Code = "200", Message = "/TV/" + Release + " (1F/1M/1d)" }; }, deadline: deadline);
            return config;
        }

        public void Dispose()
        {
            foreach (var search in _searches.Values) search.Dispose();
            Manager.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
