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
    public void Steady_progress_outlives_the_window_and_a_stall_does_not()
    {
        // The production wiring in miniature: ceiling armed once, re-armed on each progress tick.
        // Manual time: the wall-clock version flaked twice under CI load (one 100 ms delay
        // stalling past the 1 s window cancels the token), even after its margins were widened.
        var clock = new ManualClock();
        var window = TimeSpan.FromMilliseconds(1000);
        using var cts = new CancellationTokenSource(window, clock);
        var last = clock.GetUtcNow().UtcDateTime - FxpFailurePolicy.ProgressRearmInterval;
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var now = clock.GetUtcNow().UtcDateTime;
            // Throttle bypassed (interval is 1s) by stamping last far enough back each tick.
            if (FxpFailurePolicy.ShouldRearmOnProgress(last, now))
            {
                cts.CancelAfter(window);
                last = now - FxpFailurePolicy.ProgressRearmInterval;
            }
        }
        Assert.False(cts.IsCancellationRequested, "2s of steady progress must not hit a 1s no-progress window");
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(cts.IsCancellationRequested, "the window must not fire early");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(cts.IsCancellationRequested, "a stall must still fire the window");
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _utc = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan interval)
        {
            _utc += interval;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_utc);
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._utc + dueTime;
                return true;
            }
            internal void FireIfDue(DateTimeOffset now)
            {
                if (_due is not { } due || due > now) return;
                _due = null;
                callback(state);
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
