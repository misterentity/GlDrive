using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-04..06: 13 of 13 "FXP transfer timed out" warnings were superbnc -> zephyr Relay
/// transfers still streaming at ~2.4 MB/s when the 180s per-transfer ceiling fired — one at
/// 496,910,336 of 500,000,000 bytes, nine Ted.Lasso 650 MB volumes in 50 minutes. The ceiling
/// was a wall clock; a volume that needs ~270s could never finish on a slow-but-healthy route
/// and was re-sent from zero. For Relay (the only mode that observes progress) the ceiling now
/// measures inactivity: every progress report re-arms it.
/// </summary>
public sealed class RelayProgressCeilingTests
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

    private static string ProgressHandler()
    {
        var src = Source();
        var exec = src.IndexOf("private async Task ExecuteTransfer(", StringComparison.Ordinal);
        var start = src.IndexOf("transfer.BytesTransferred += totalBytes =>", exec, StringComparison.Ordinal);
        var end = src.IndexOf("transferProtocolStarted = true", start, StringComparison.Ordinal);
        Assert.True(exec >= 0 && start > exec && end > start, "fixture drift: progress handler anchors moved");
        return src[start..end];
    }

    [Fact]
    public void Rearm_is_throttled_to_the_interval()
    {
        var t0 = new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);
        Assert.False(FxpFailurePolicy.ShouldRearmOnProgress(t0, t0.AddMilliseconds(999)));
        Assert.True(FxpFailurePolicy.ShouldRearmOnProgress(t0, t0 + FxpFailurePolicy.ProgressRearmInterval));
    }

    [Theory]
    [InlineData(FxpMode.Relay, 0, true)]
    [InlineData(FxpMode.Relay, 425025536, true)]
    [InlineData(FxpMode.CpsvPasv, 1, true)]   // fell back to Relay mid-call
    [InlineData(FxpMode.CpsvPasv, 0, false)]
    [InlineData(FxpMode.PasvPasv, 0, false)]
    public void Ceiling_kind_follows_observed_progress(FxpMode mode, long moved, bool tracks)
        => Assert.Equal(tracks, FxpFailurePolicy.CeilingTracksProgress(mode, moved));

    [Fact]
    public void Progress_handler_rearms_the_ceiling_through_the_throttle()
    {
        var handler = ProgressHandler();
        var check = handler.IndexOf("FxpFailurePolicy.ShouldRearmOnProgress(lastCeilingRearm, now)", StringComparison.Ordinal);
        var arm = handler.IndexOf("armTransferCeiling?.Invoke();", StringComparison.Ordinal);
        var stamp = handler.IndexOf("lastCeilingRearm = now;", StringComparison.Ordinal);
        Assert.True(check >= 0, "progress must consult the re-arm throttle");
        Assert.True(arm > check && stamp > check, "re-arm and its timestamp must sit behind the throttle");
    }

    [Fact]
    public void Rearm_is_not_gated_on_the_detected_mode()
    {
        // CPSV-PASV falls back to Relay inside FxpTransfer; gating on the DETECTED mode would
        // leave that relay on the wall-clock ceiling. Progress events are the evidence.
        var handler = ProgressHandler();
        Assert.DoesNotContain("mode", handler.Replace("lastCeilingRearm", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Steady_progress_outlives_the_window_and_a_stall_does_not()
    {
        // The production wiring in miniature: ceiling armed once, re-armed on each progress tick.
        var window = TimeSpan.FromMilliseconds(1000);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(window);
        var last = DateTime.UtcNow - FxpFailurePolicy.ProgressRearmInterval;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            var now = DateTime.UtcNow;
            // Throttle bypassed (interval is 1s) by stamping last far enough back each tick.
            if (FxpFailurePolicy.ShouldRearmOnProgress(last, now))
            {
                cts.CancelAfter(window);
                last = now - FxpFailurePolicy.ProgressRearmInterval;
            }
        }
        Assert.False(cts.IsCancellationRequested, "2s of steady progress must not hit a 1s no-progress window");
        await Task.Delay(2500);
        Assert.True(cts.IsCancellationRequested, "a stall must still fire the window");
    }
}
