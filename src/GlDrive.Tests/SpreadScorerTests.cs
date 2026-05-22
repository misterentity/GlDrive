using System;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class SpreadScorerTests
{
    private static SpreadScorer NewScorer() => new(new SpeedTracker());

    private static SpreadFileInfo File(string name, long size = 1_000_000) =>
        new() { Name = name, FullPath = "/x/" + name, Size = size };

    [Fact]
    public void Sfv_scores_max_priority()
    {
        var s = NewScorer();
        var score = s.Score(File("rls.sfv"), "a", "b", SitePriority.Normal,
            ownedPercent: 0, maxFileSize: 1_000_000, maxSpeedBps: 1, elapsed: TimeSpan.Zero, SpreadMode.Race);
        Assert.True(score >= 50000, $"SFV should be top priority, got {score}");
    }

    [Fact]
    public void Nfo_becomes_priority_after_15s()
    {
        var s = NewScorer();
        var early = s.Score(File("rls.nfo"), "a", "b", SitePriority.Normal, 0, 1_000_000, 1, TimeSpan.FromSeconds(5), SpreadMode.Race);
        var late = s.Score(File("rls.nfo"), "a", "b", SitePriority.Normal, 0, 1_000_000, 1, TimeSpan.FromSeconds(20), SpreadMode.Race);
        Assert.True(late > early, $"NFO should gain priority after 15s (early={early}, late={late})");
        Assert.True(late >= 50000);
    }

    [Fact]
    public void Failure_penalty_demotes_a_failed_pair_below_a_fresh_one()
    {
        var s = NewScorer();
        var fresh = s.Score(File("rls.r01"), "a", "b", SitePriority.Normal, 0, 1_000_000, 1, TimeSpan.Zero, SpreadMode.Race, priorFailures: 0);
        var failed = s.Score(File("rls.r01"), "a", "c", SitePriority.Normal, 0, 1_000_000, 1, TimeSpan.Zero, SpreadMode.Race, priorFailures: 3);
        Assert.True(failed < fresh, $"A 3x-failed pair should rank below a fresh pair (fresh={fresh}, failed={failed})");
    }

    [Fact]
    public void Score_never_negative_even_with_heavy_failures()
    {
        var s = NewScorer();
        var score = s.Score(File("rls.r01", 1), "a", "b", SitePriority.VeryLow, 1.0, 1_000_000, 1, TimeSpan.Zero, SpreadMode.Race, priorFailures: 99);
        Assert.True(score >= 0);
    }
}
