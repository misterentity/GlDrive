using System;
using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-15 torrent-search sweep: every source's availability
/// probe was a one-shot latch. <c>_apibayChecked = true</c> was set BEFORE the probe loop, so
/// a probe that failed left the host empty and the source disabled for the whole process
/// lifetime. The only reset path was a 403/503 on a real search — which could never fire,
/// because no search is issued when the host is empty. <c>_csvChecked</c> had no reset at all.
///
/// One transient blip at startup silently removed a source until the app restarted.
///
/// Fourth instance of "a decision that never expires is a permanent exemption" in this
/// codebase (v3.10.41 UAC decline, v3.10.42 _destDirConfirmed, v3.10.65 _watchAbandoned).
/// Time is the right expiry here: unlike a volume set there is no fingerprint to watch, and a
/// dead indexer is exactly the kind of thing that comes back on its own.
/// </summary>
public sealed class SourceAvailabilityPolicyTests
{
    private static readonly DateTime T0 = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unprobed_source_should_be_probed()
    {
        var policy = new SourceAvailabilityPolicy();
        Assert.True(policy.ShouldProbe("knaben", T0));
    }

    [Fact]
    public void Available_source_is_not_reprobed()
    {
        var policy = new SourceAvailabilityPolicy();
        policy.MarkAvailable("knaben", T0);

        Assert.False(policy.ShouldProbe("knaben", T0.AddMinutes(1)));
        Assert.True(policy.IsUsable("knaben", T0.AddMinutes(1)));
    }

    /// <summary>
    /// THE test. A source that failed its probe must come back on its own. Under the old
    /// latch this was false forever.
    /// </summary>
    [Fact]
    public void FailedSource_is_reprobed_after_the_cooldown()
    {
        var policy = new SourceAvailabilityPolicy();
        policy.MarkUnavailable("apibay", T0);

        Assert.False(policy.ShouldProbe("apibay", T0.AddMinutes(1)));
        Assert.False(policy.IsUsable("apibay", T0.AddMinutes(1)));

        var afterCooldown = T0 + SourceAvailabilityPolicy.RetryAfter + TimeSpan.FromSeconds(1);
        Assert.True(policy.ShouldProbe("apibay", afterCooldown));
    }

    [Fact]
    public void FailedSource_that_recovers_becomes_usable_again()
    {
        var policy = new SourceAvailabilityPolicy();
        policy.MarkUnavailable("apibay", T0);

        var later = T0 + SourceAvailabilityPolicy.RetryAfter + TimeSpan.FromSeconds(1);
        Assert.True(policy.ShouldProbe("apibay", later));

        policy.MarkAvailable("apibay", later);
        Assert.True(policy.IsUsable("apibay", later));
    }

    /// <summary>
    /// Repeated failures must not compound into a longer and longer exile — each failure
    /// restarts the same fixed cooldown, so a source is never more than RetryAfter away from
    /// another chance.
    /// </summary>
    [Fact]
    public void RepeatedFailures_keep_a_fixed_cooldown()
    {
        var policy = new SourceAvailabilityPolicy();
        var t = T0;

        for (var i = 0; i < 5; i++)
        {
            policy.MarkUnavailable("apibay", t);
            t = t + SourceAvailabilityPolicy.RetryAfter + TimeSpan.FromSeconds(1);
            Assert.True(policy.ShouldProbe("apibay", t));
        }
    }

    /// <summary>
    /// A source knocked out mid-search (403/503) must also serve its cooldown and then return,
    /// rather than being disabled until restart.
    /// </summary>
    [Fact]
    public void SourceKnockedOutMidSearch_returns_after_cooldown()
    {
        var policy = new SourceAvailabilityPolicy();
        policy.MarkAvailable("solid", T0);
        Assert.True(policy.IsUsable("solid", T0));

        policy.MarkUnavailable("solid", T0.AddMinutes(2));
        Assert.False(policy.IsUsable("solid", T0.AddMinutes(3)));

        Assert.True(policy.ShouldProbe("solid",
            T0.AddMinutes(2) + SourceAvailabilityPolicy.RetryAfter + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Sources_are_tracked_independently()
    {
        var policy = new SourceAvailabilityPolicy();
        policy.MarkAvailable("knaben", T0);
        policy.MarkUnavailable("apibay", T0);

        Assert.True(policy.IsUsable("knaben", T0));
        Assert.False(policy.IsUsable("apibay", T0));
    }

    /// <summary>
    /// The cooldown has to be short enough that a user who retries a search a few minutes
    /// later sees a recovered source, and long enough not to hammer a dead host on every
    /// keystroke-driven search. Pinned so a future edit is a deliberate decision.
    /// </summary>
    [Fact]
    public void RetryAfter_is_a_few_minutes_not_hours()
    {
        Assert.InRange(SourceAvailabilityPolicy.RetryAfter,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
    }
}
