using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class SeriesInventoryRefresherTests
{
    private static TvMazeScheduleEpisode[] Episodes() =>
    [
        new() { Id = 1, Season = 1, Number = 1, Name = "One", Airdate = "2026-01-01", Airstamp = "2026-01-01T20:00:00Z" },
        new() { Id = 2, Season = 1, Number = 2, Name = "Two", Airdate = "2026-01-02" }
    ];

    [Fact]
    public async Task DailyAttemptPersistsAcrossRestartAndSuccessfulInventoryIsSaved()
    {
        string? json = null;
        var item = SeriesMonitorPolicyTests.Series();
        var store = new WishlistStore(() => null, value => json = value); store.Add(item);
        var now = SeriesMonitorPolicyTests.Now; var requests = 0;
        Task<TvMazeScheduleEpisode[]?> Fetch(int _, CancellationToken ct) { requests++; return Task.FromResult<TvMazeScheduleEpisode[]?>(Episodes()); }
        var refresh = new SeriesInventoryRefresher(store, Fetch, () => now);
        Assert.Equal(1, await refresh.RefreshDueAsync());
        Assert.Equal(0, await refresh.RefreshDueAsync());
        Assert.Equal(2, item.SeriesInventory.Count);
        var restarted = new WishlistStore(() => json, value => json = value);
        var next = new SeriesInventoryRefresher(restarted, Fetch, () => now);
        Assert.Equal(0, await next.RefreshDueAsync()); Assert.Equal(1, requests);
        now = now.AddDays(1);
        Assert.Equal(1, await next.RefreshDueAsync()); Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("exception")]
    public async Task FailedOrInvalidRefreshPreservesPriorInventoryAndDailyBudget(string response)
    {
        var item = SeriesMonitorPolicyTests.Series(); var previous = item.SeriesInventory;
        var store = new WishlistStore(() => null, _ => { }); store.Add(item); var requests = 0;
        var refresher = new SeriesInventoryRefresher(store, (_, _) =>
        {
            requests++;
            if (response == "exception") throw new InvalidOperationException("test metadata failure");
            return Task.FromResult<TvMazeScheduleEpisode[]?>(response switch
            {
                "empty" => [], "duplicate" => [Episodes()[0], Episodes()[0]], _ => null
            });
        }, () => SeriesMonitorPolicyTests.Now);
        Assert.Equal(0, await refresher.RefreshDueAsync());
        Assert.Equal(0, await refresher.RefreshDueAsync());
        Assert.Same(previous, item.SeriesInventory); Assert.Equal(1, requests);
    }

    [Fact]
    public async Task OverlapIsSuppressedAndClaimsAddedDuringHttpAreNeverOverwritten()
    {
        var item = SeriesMonitorPolicyTests.Series(); var store = new WishlistStore(() => null, _ => { }); store.Add(item);
        var response = new TaskCompletionSource<TvMazeScheduleEpisode[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new SeriesInventoryRefresher(store, (_, _) => response.Task, () => SeriesMonitorPolicyTests.Now);
        var active = refresher.RefreshDueAsync();
        Assert.Equal(0, await refresher.RefreshDueAsync());
        item.PendingGrabs.Add(new() { ReleaseName = SeriesMonitorPolicyTests.Release("S01E01"), DownloadId = "pending" });
        item.BestOwned.Add(new() { ReleaseName = SeriesMonitorPolicyTests.Release("S01E02"), DownloadId = "owned" });
        response.SetResult(Episodes());
        Assert.Equal(1, await active);
        Assert.Equal("pending", Assert.Single(store.GetById(item.Id)!.PendingGrabs).DownloadId);
        Assert.Equal("owned", Assert.Single(store.GetById(item.Id)!.BestOwned).DownloadId);
    }

    [Fact]
    public async Task ChangedShowIdentityCannotReceiveStaleHttpInventory()
    {
        var item = SeriesMonitorPolicyTests.Series(); var old = item.SeriesInventory;
        var store = new WishlistStore(() => null, _ => { }); store.Add(item);
        var refresher = new SeriesInventoryRefresher(store, (_, _) =>
        {
            item.TvMazeId = 456;
            return Task.FromResult<TvMazeScheduleEpisode[]?>(Episodes());
        }, () => SeriesMonitorPolicyTests.Now);
        Assert.Equal(0, await refresher.RefreshDueAsync()); Assert.Same(old, item.SeriesInventory);
    }

    [Fact]
    public async Task LegacyAndPausedItemsNeverRequestMetadata()
    {
        var item = SeriesMonitorPolicyTests.Series(SeriesMonitorMode.Legacy);
        var store = new WishlistStore(() => null, _ => { }); store.Add(item);
        var calls = 0;
        var refresh = new SeriesInventoryRefresher(store, (_, _) => { calls++; return Task.FromResult<TvMazeScheduleEpisode[]?>([]); });
        await refresh.RefreshDueAsync();
        item.MonitorMode = SeriesMonitorMode.All; item.Status = WishlistStatus.Paused;
        await refresh.RefreshDueAsync(); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesOverlapGuard()
    {
        var store = new WishlistStore(() => null, _ => { }); store.Add(SeriesMonitorPolicyTests.Series());
        var refresh = new SeriesInventoryRefresher(store, (_, _) => Task.FromResult<TvMazeScheduleEpisode[]?>(Episodes()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.RefreshDueAsync(cancelled.Token));
        Assert.Equal(1, await refresh.RefreshDueAsync());
    }

    [Fact]
    public async Task FailedAttemptPersistenceCannotIssueHttpAndRollsBackTimestamp()
    {
        var item = SeriesMonitorPolicyTests.Series(); var failSave = false;
        var store = new WishlistStore(() => null, _ => { if (failSave) throw new InvalidOperationException("save failed"); }); store.Add(item);
        failSave = true; var calls = 0;
        var refresh = new SeriesInventoryRefresher(store, (_, _) => { calls++; return Task.FromResult<TvMazeScheduleEpisode[]?>(Episodes()); });
        Assert.Equal(0, await refresh.RefreshDueAsync()); Assert.Equal(0, calls);
        Assert.Null(item.SeriesInventoryLastAttemptAtUtc);
    }

    [Fact]
    public void ConversionRejectsDuplicateNumberingAndSkipsUnnumberedSpecials()
    {
        Assert.Null(SeriesInventoryRefresher.ConvertEpisodes([Episodes()[0], Episodes()[0]]));
        var result = SeriesInventoryRefresher.ConvertEpisodes([Episodes()[0], new() { Season = 1, Number = null }]);
        Assert.Equal(1, Assert.Single(result!).Number);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero), result![0].AirAtUtc);
    }
}
