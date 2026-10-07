using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-01 15:09–15:18: five superbnc -> zephyr relays logged "timed out after 133–149s ...
/// ceiling 180s". The ceiling was armed when the transfer task was DISPATCHED, while the file
/// still queued behind the running transfer for the server gates (up to 2x45s) and the pool
/// borrow (30s) — each of those already bounded on its own. The queued file was charged for
/// its predecessor's runtime, and the warning's elapsed time (measured from protocol start)
/// contradicted the ceiling it printed. The ceiling must be re-armed when protocol begins.
/// </summary>
public sealed class TransferCeilingClockTests
{
    private static string Source()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate SpreadJob.cs");
    }

    [Fact]
    public async Task CancelAfter_rearm_replaces_the_pending_deadline()
    {
        // The fix relies on CancelAfter rescheduling an already-armed timer.
        using var cts = new CancellationTokenSource();
        // Margins are wide: under release-build test load a 100ms delay overran a 150ms timer.
        cts.CancelAfter(TimeSpan.FromMilliseconds(1000));
        await Task.Delay(100);
        cts.CancelAfter(TimeSpan.FromMilliseconds(3000));
        await Task.Delay(1500);
        Assert.False(cts.IsCancellationRequested, "re-arm must push the deadline out");
        await Task.Delay(2500);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Ceiling_is_rearmed_where_the_protocol_clock_starts_and_before_protocol()
    {
        var src = Source();
        var exec = src.IndexOf("private async Task ExecuteTransfer(", StringComparison.Ordinal);
        Assert.True(exec >= 0);
        var borrow = src.IndexOf("dstPool.Borrow(", exec, StringComparison.Ordinal);
        var clock = src.IndexOf("startTime = DateTime.UtcNow;", borrow, StringComparison.Ordinal);
        var arm = src.IndexOf("armTransferCeiling?.Invoke();", exec, StringComparison.Ordinal);
        var protocol = src.IndexOf("transferProtocolStarted = true", exec, StringComparison.Ordinal);
        Assert.True(borrow > exec && clock > borrow, "fixture drift: borrow/clock anchors moved");
        Assert.True(arm > borrow && arm < protocol, "ceiling must be re-armed after gates+borrow, before protocol");
        Assert.True(Math.Abs(arm - clock) < 200, "re-arm and the elapsed clock must start together");
    }

    [Fact]
    public void Dispatcher_passes_a_rearm_that_resets_the_same_token_source()
    {
        var src = Source();
        var run = src.IndexOf("xferTimeout.CancelAfter(ceiling);", StringComparison.Ordinal);
        var call = src.IndexOf("await ExecuteTransfer(", run, StringComparison.Ordinal);
        Assert.True(run >= 0 && call > run);
        var callLine = src[call..src.IndexOf(';', call)];
        Assert.Contains("xferTimeout.Token", callLine);
        Assert.Contains("() => xferTimeout.CancelAfter(", callLine);
    }
}
