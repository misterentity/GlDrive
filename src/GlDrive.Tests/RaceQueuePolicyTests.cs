using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceQueuePolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static PersistedRace Request(string name, double wait, double? seconds) =>
        new("tv", name, ["source", "destination"], SpreadMode.Race, "source", null, Now.AddSeconds(-wait))
        { PredictedSeconds = seconds };

    [Fact]
    public void Response_ratio_uses_wait_against_predicted_work()
    {
        var queue = new[] { Request("large", 100, 1000), Request("small", 50, 10) };
        Assert.Equal([1, 0], RaceQueuePolicy.OrderIndices(queue, [0, 1], Now, true, 120));
        Assert.Equal(6, RaceQueuePolicy.ResponseRatio(Now.AddSeconds(-50), Now, 10));
        Assert.Equal([0, 1], RaceQueuePolicy.OrderIndices(queue, [0, 1], Now, false, 120));
    }

    [Fact]
    public void Hard_age_override_prevents_large_request_starvation()
    {
        var queue = new[] { Request("large", 7200, 5400), Request("small", 30, 5) };
        Assert.Equal([1, 0], RaceQueuePolicy.OrderIndices(queue, [0, 1], Now, true, 1440));
        Assert.Equal([0, 1], RaceQueuePolicy.OrderIndices(queue, [0, 1], Now, true, 120));
    }

    [Fact]
    public void Aged_requests_use_oldest_then_fifo_even_with_different_predictions()
    {
        var queue = new[] { Request("first", 8000, 5400), Request("second", 8000, 5), Request("oldest", 9000, 300) };
        Assert.Equal([2, 0, 1], RaceQueuePolicy.OrderIndices(queue, [0, 1, 2], Now, true, 120));
    }

    [Fact]
    public void A5_cleanup_fresh_resume_priorities_remain_authoritative()
    {
        var queue = new[]
        {
            Request("resume", 40000, 5) with { ResumeCount = 1 },
            Request("fresh", 1, 5400),
            Request("cleanup", 0, 5400) with { CleanupOnly = true },
            Request("owned", 40000, 5) with { Partial = new() { Directories = [new() { ServerId = "destination" }] } }
        };
        Assert.Equal([2, 1, 0, 3], RaceQueuePolicy.OrderIndices(queue, [0, 1, 2, 3], Now, true, 120));
        Assert.Equal([2, 1, 0, 3], RaceQueuePolicy.OrderIndices(queue, [0, 1, 2, 3], Now, false, 120));
    }

    [Fact]
    public void Normalized_deadline_cleanup_is_used_without_mutating_the_stored_request()
    {
        var old = Request("resume", 100, 100) with { ResumeCount = 1 };
        var fresh = Request("fresh", 1000, 5);
        Assert.Equal([4, 2], RaceQueuePolicy.Order([(old with { CleanupOnly = true }, 4), (fresh, 2)], Now, true, 120));
        Assert.False(old.CleanupOnly);
    }

    [Fact]
    public void Equal_ratio_and_future_timestamps_keep_fifo_and_eligibility_is_not_expanded()
    {
        var queue = new[] { Request("one", 100, 100), Request("two", 10, 10), Request("future", -50, 5) };
        Assert.Equal([0, 1, 2], RaceQueuePolicy.OrderIndices(queue, [2, 1, 0, 0, -1, 10], Now, true, 120));
        Assert.Equal([1], RaceQueuePolicy.OrderIndices(queue, [1], Now, true, 120));
        Assert.Equal(1, RaceQueuePolicy.ResponseRatio(Now.AddHours(1), Now, 5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_persisted_predictions_use_bounded_default(double? seconds)
    {
        Assert.Equal(RaceWorkEstimator.DefaultSeconds, RaceWorkEstimator.ClampSeconds(seconds));
        Assert.True(double.IsFinite(RaceQueuePolicy.ResponseRatio(DateTime.MinValue, DateTime.MaxValue, seconds)));
    }

    [Fact]
    public void Synthetic_single_slot_arrival_replay_reduces_median_wait_against_fifo()
    {
        // A transfer already occupies the sole slot when two fills and a burst of small releases arrive.
        var arrivals = new List<(double At, double Work)> { (0, 600), (1, 900), (2, 900) };
        arrivals.AddRange(Enumerable.Range(3, 20).Select(i => ((double)i, 10d)));
        var fifo = Replay(arrivals, enabled: false);
        var hrrn = Replay(arrivals, enabled: true);
        Assert.Equal(arrivals.Count, hrrn.Count);
        Assert.Equal(2469, Median(fifo));
        Assert.Equal(687, Median(hrrn));
        Assert.True(Median(hrrn) < Median(fifo), $"HRRN median {Median(hrrn)} must improve FIFO {Median(fifo)}");
        Assert.True(Median(hrrn) < Median(fifo) / 2);
        Assert.All(hrrn, wait => Assert.True(wait >= 0));
    }

    private static List<double> Replay(IReadOnlyList<(double At, double Work)> trace, bool enabled)
    {
        var queue = trace.Select((t, i) => new PersistedRace("tv", "Release." + i, ["source", "destination"],
            SpreadMode.Race, "source", null, Now.AddSeconds(t.At)) { PredictedSeconds = t.Work }).ToArray();
        var remaining = Enumerable.Range(0, trace.Count).ToHashSet();
        var waits = new List<double>();
        double now = 0;
        while (remaining.Count > 0)
        {
            var due = remaining.Where(i => trace[i].At <= now).ToArray();
            if (due.Length == 0) { now = remaining.Min(i => trace[i].At); continue; }
            var next = RaceQueuePolicy.OrderIndices(queue, due, Now.AddSeconds(now), enabled, 120).First();
            waits.Add(now - trace[next].At);
            remaining.Remove(next);
            // Exactly one work interval is advanced per choice: this replay has no preemption or second slot.
            now += trace[next].Work;
        }
        return waits;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }
}
