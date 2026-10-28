using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Downloads.Segmented;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-27 13:12–13:40: a download's idle-race-login lane asked for the source site's race
/// login every 5 s with a 2 s wait. The spread pool was empty, so each ask dialled a fresh
/// login that took ~2 s; the wait cancelled it as it finished and the login was thrown away —
/// 253 logins in one hour, 58 in five minutes. With the v3.14 login governor (7 automatic
/// logins per 5 minutes) that churn would also have pinned the account's budget and starved
/// race logins. A late login must land in the pool, and failed asks must back off.
/// </summary>
public class RaceLoginLendTests
{
    private sealed class Lease
    {
        public int Released;
    }

    [Fact]
    public async Task Borrow_that_finishes_within_the_wait_is_returned_to_the_caller()
    {
        var lease = new Lease();
        var result = await BoundedLend.WaitOrReleaseAsync(
            _ => Task.FromResult(lease), l => { l.Released++; return ValueTask.CompletedTask; },
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Same(lease, result);
        Assert.Equal(0, lease.Released);
    }

    [Fact]
    public async Task Login_still_dialling_at_the_wait_finishes_into_the_pool_instead_of_being_cancelled()
    {
        var dial = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken borrowToken = default;
        var released = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await BoundedLend.WaitOrReleaseAsync(
            token => { borrowToken = token; return dial.Task; },
            l => { released.TrySetResult(l); return ValueTask.CompletedTask; },
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(result);
        Assert.False(borrowToken.IsCancellationRequested); // the 50 ms wait did not abort the login
        var lease = new Lease();
        dial.SetResult(lease);
        Assert.Same(lease, await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Late_borrow_is_still_bounded_by_its_own_budget()
    {
        CancellationToken borrowToken = default;
        var result = await BoundedLend.WaitOrReleaseAsync<Lease>(
            token => { borrowToken = token; return Task.Delay(Timeout.Infinite, token).ContinueWith<Lease>(_ => throw new OperationCanceledException(token)); },
            _ => ValueTask.CompletedTask,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Null(result);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(borrowToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates_and_cancels_the_borrow()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken borrowToken = default;
        var pending = BoundedLend.WaitOrReleaseAsync<Lease>(
            token => { borrowToken = token; return Task.Delay(Timeout.Infinite, token).ContinueWith<Lease>(_ => throw new OperationCanceledException(token)); },
            _ => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(borrowToken.IsCancellationRequested);
    }

    [Fact]
    public void Failed_lends_back_off_exponentially_to_five_minutes_and_reset_on_success()
    {
        var backoff = new LendBackoff();
        Assert.True(backoff.Ready(0));

        var delays = new List<TimeSpan>();
        long now = 0;
        for (var i = 0; i < 9; i++)
        {
            var delay = backoff.Failed(now);
            delays.Add(delay);
            Assert.False(backoff.Ready(now + (long)delay.TotalMilliseconds - 1));
            now += (long)delay.TotalMilliseconds;
            Assert.True(backoff.Ready(now));
        }

        Assert.Equal(new[] { 5, 10, 20, 40, 80, 160, 300, 300, 300 }, delays.ConvertAll(d => (int)d.TotalSeconds));
        backoff.Succeeded();
        Assert.True(backoff.Ready(now));
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.Failed(now));
    }

    private sealed class RefusingLender : IRaceLoginLender
    {
        public int Borrows;
        public bool IsRaceIdle(string serverId) => true;
        public Task<PooledConnection?> TryBorrowAsync(string serverId, TimeSpan wait, CancellationToken ct)
        {
            Borrows++;
            return Task.FromResult<PooledConnection?>(null);
        }
    }

    [Fact]
    public async Task Lane_source_does_not_redial_a_site_every_poll_after_failed_lends()
    {
        var lender = new RefusingLender();
        long now = 0;
        var source = new RaceLoginLaneSource(lender, "src", NetworkPath.Default, null, () => now);

        // The supervisor polls every 5 s; over ten minutes that was 120 dials.
        for (now = 0; now <= 600_000; now += 5_000)
            Assert.Null(await source.TryAcquireAsync(CancellationToken.None));

        Assert.InRange(lender.Borrows, 1, 9);
    }
}
