using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.28 — 2026-10-22 16:47:32: a lane that lost its connection re-borrowed from the main
/// pool, but the next queued download had just taken that pool's only login. The re-borrow
/// waited until the 90 s stall watcher cancelled it, twice, and the lane was "retired after 3
/// consecutive failures: The operation was canceled." Waiting for a busy pool is queueing,
/// not a lane fault: it must back off (not counted) and try again.
/// </summary>
public class FtpLaneReborrowTests
{
    [Fact]
    public async Task Busy_pool_becomes_a_backoff_not_a_failure()
    {
        var ex = await Assert.ThrowsAsync<LaneBackoffException>(() =>
            FtpLane.BorrowWithin(ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<int>(_ => throw new OperationCanceledException(ct)),
                TimeSpan.FromMilliseconds(100), "Ethernet", CancellationToken.None));

        Assert.True(ex.Delay > TimeSpan.Zero);
        Assert.Contains("Ethernet", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FtpLane.BorrowWithin(ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<int>(_ => throw new OperationCanceledException(ct)),
                TimeSpan.FromSeconds(30), "Ethernet", cts.Token));
    }

    [Fact]
    public async Task Real_borrow_failures_are_not_softened()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FtpLane.BorrowWithin<int>(_ => throw new InvalidOperationException("Server in BNC cooldown"),
                TimeSpan.FromSeconds(30), "Ethernet", CancellationToken.None));
    }

    [Fact]
    public async Task Successful_borrow_is_returned()
    {
        Assert.Equal(7, await FtpLane.BorrowWithin(_ => Task.FromResult(7), TimeSpan.FromSeconds(30), "Ethernet", CancellationToken.None));
    }

    [Fact]
    public void Reborrow_wait_stays_inside_the_stall_watcher_window()
    {
        // The engine's stall watcher is armed before OpenAsync; a re-borrow deadline at or
        // beyond it turns queueing back into a "stall" failure.
        Assert.True(FtpLane.ReborrowWait < new SegmentedTransferOptions().StallTimeout);
    }

    [Fact]
    public void Lane_reborrow_goes_through_the_bounded_wait()
    {
        var src = File.ReadAllText(FindSource("src", "GlDrive", "Downloads", "Segmented", "FtpLane.cs"));
        var start = src.IndexOf("private async Task<AsyncFtpClient> ClientAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = src[start..src.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("BorrowWithin(_reborrowPool.Borrow, ReborrowWait", body);
        Assert.DoesNotContain("await _reborrowPool.Borrow(ct)", body);
    }

    private static string FindSource(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine([dir, .. parts]);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
