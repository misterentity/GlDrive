using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class SearchLifetimeTests
{
    [Theory]
    [InlineData(SearchMethod.LiveCrawl)]
    [InlineData(SearchMethod.SiteSearch)]
    [InlineData(SearchMethod.Auto)]
    [InlineData(SearchMethod.CachedIndex)]
    public async Task Ended_race_cannot_start_a_search(SearchMethod method)
    {
        var calls = 0;
        await using var pool = new FtpConnectionPool(null!, 1);
        using var search = new FtpSearchService(pool, new SearchConfig { Method = method, SearchPaths = ["/TV"] },
            (_, _) => { calls++; return Task.FromResult(Array.Empty<FtpListItem>()); },
            (_, _) => { calls++; return Task.FromResult(new FtpReply { Code = "500" }); });
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchDetailed("wanted", null, default, stop.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(SearchMethod.LiveCrawl)]
    [InlineData(SearchMethod.SiteSearch)]
    [InlineData(SearchMethod.Auto)]
    public async Task Race_end_drains_wire_command_without_starting_another_or_returning_late_results(SearchMethod method)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken wireToken = default;
        async Task Wait(CancellationToken ct)
        {
            calls++;
            wireToken = ct;
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
        }
        await using var pool = new FtpConnectionPool(null!, 1);
        using var search = new FtpSearchService(pool,
            new SearchConfig { Method = method, SearchPaths = ["/TV", "/Movies"], MaxDepth = 2 },
            async (_, ct) =>
            {
                await Wait(ct);
                return [new FtpListItem { Name = "Wanted.Show", FullName = "/TV/Wanted.Show", Type = FtpObjectType.Directory }];
            },
            async (_, ct) => { await Wait(ct); return new FtpReply { Code = "500", Message = "Unsupported" }; });
        using var stop = new CancellationTokenSource();
        var pending = search.SearchDetailed("wanted", null, default, stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            Assert.False(wireToken.IsCancellationRequested);
            Assert.False(pending.IsCompleted);
        }
        finally { finish.TrySetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ordinary_caller_cancellation_still_interrupts_the_wire_command()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new FtpConnectionPool(null!, 1);
        using var search = new FtpSearchService(pool,
            new SearchConfig { Method = SearchMethod.LiveCrawl, SearchPaths = ["/TV"] },
            async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return []; },
            (_, _) => throw new InvalidOperationException("Unexpected SITE SEARCH"));
        using var cancel = new CancellationTokenSource();
        var pending = search.SearchDetailed("wanted", ct: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
