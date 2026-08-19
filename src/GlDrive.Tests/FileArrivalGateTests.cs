using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the first link of the 2026-08-18 extractor loop: a file that had not
/// arrived yet was treated as a terminal failure, and reported as a timeout.
///
/// The evidence that localised it was a pair of invariants:
///   * across three days of logs, "archive was not ready before timeout" appeared 69 times and
///     the genuine timeout message appeared ZERO times;
///   * every detect→warn gap was 2.008 s — one poll interval of a 300 s budget. Contention
///     varies; a structural exit repeats exactly.
///
/// The gate now cannot express "give up" at all. That is the point: the only remaining route
/// to a false return is budget exhaustion, which the caller owns, so the timeout message is
/// true whenever it is printed.
/// </summary>
public sealed class FileArrivalGateTests
{
    [Fact]
    public void A_file_that_has_not_arrived_yet_keeps_the_wait_alive()
    {
        long lastSize = -1;
        var stable = 0;

        // The .rar of an old-style set sorts after every .rNN, so it lands last. This is the
        // ordinary state for most of an arrival, not an error.
        for (var tick = 0; tick < 150; tick++)
            Assert.Equal(
                FileArrivalGate.Decision.KeepWaiting,
                FileArrivalGate.Observe(exists: false, currentSize: 0, ref lastSize, ref stable));
    }

    [Fact]
    public void An_absent_file_voids_any_stability_run_measured_earlier()
    {
        long lastSize = -1;
        var stable = 0;

        FileArrivalGate.Observe(true, 500, ref lastSize, ref stable);
        FileArrivalGate.Observe(true, 500, ref lastSize, ref stable);
        Assert.Equal(1, stable);

        // A file that disappears (replaced, renamed mid-set) must not carry its old stability
        // forward, or the next matching size would confirm readiness a tick too early.
        FileArrivalGate.Observe(false, 0, ref lastSize, ref stable);
        Assert.Equal(0, stable);
        Assert.Equal(-1, lastSize);
    }

    [Fact]
    public void A_settled_file_is_confirmed_after_two_stable_observations()
    {
        long lastSize = -1;
        var stable = 0;

        Assert.Equal(FileArrivalGate.Decision.KeepWaiting,
            FileArrivalGate.Observe(true, 1000, ref lastSize, ref stable));
        Assert.Equal(FileArrivalGate.Decision.KeepWaiting,
            FileArrivalGate.Observe(true, 1000, ref lastSize, ref stable));
        Assert.Equal(FileArrivalGate.Decision.ConfirmWithExclusiveOpen,
            FileArrivalGate.Observe(true, 1000, ref lastSize, ref stable));
    }

    [Fact]
    public void A_growing_file_never_confirms()
    {
        long lastSize = -1;
        var stable = 0;

        for (long size = 1; size <= 200; size++)
            Assert.Equal(
                FileArrivalGate.Decision.KeepWaiting,
                FileArrivalGate.Observe(true, size, ref lastSize, ref stable));
    }

    [Fact]
    public void A_zero_byte_placeholder_never_confirms_however_long_it_sits()
    {
        // An external copy is created empty and filled in place. Stable at zero bytes is the
        // start of an arrival, not the end of one.
        long lastSize = -1;
        var stable = 0;

        for (var tick = 0; tick < 100; tick++)
            Assert.Equal(
                FileArrivalGate.Decision.KeepWaiting,
                FileArrivalGate.Observe(true, 0, ref lastSize, ref stable));
    }

    [Fact]
    public void A_file_that_resumes_growing_after_a_pause_loses_its_stability()
    {
        long lastSize = -1;
        var stable = 0;

        FileArrivalGate.Observe(true, 1000, ref lastSize, ref stable);
        FileArrivalGate.Observe(true, 1000, ref lastSize, ref stable);
        Assert.Equal(1, stable);

        // A stalled-then-resumed transfer must restart the run, not confirm on the next tick.
        Assert.Equal(FileArrivalGate.Decision.KeepWaiting,
            FileArrivalGate.Observe(true, 2000, ref lastSize, ref stable));
        Assert.Equal(0, stable);
    }

    [Fact]
    public void An_arrival_that_appears_late_still_confirms()
    {
        // End-to-end shape of the fixed behaviour: the .rar shows up after the continuation
        // parts, grows, settles, and is confirmed — all inside one wait.
        long lastSize = -1;
        var stable = 0;

        for (var tick = 0; tick < 60; tick++)
            FileArrivalGate.Observe(false, 0, ref lastSize, ref stable);

        for (long size = 1_000; size <= 5_000; size += 1_000)
            Assert.Equal(FileArrivalGate.Decision.KeepWaiting,
                FileArrivalGate.Observe(true, size, ref lastSize, ref stable));

        FileArrivalGate.Observe(true, 5_000, ref lastSize, ref stable);
        Assert.Equal(FileArrivalGate.Decision.ConfirmWithExclusiveOpen,
            FileArrivalGate.Observe(true, 5_000, ref lastSize, ref stable));
    }
}
