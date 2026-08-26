using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A second announce may be processed after a completed race has already populated every
/// site. The race must still enter its terminal state and feed the dead-race cache, but the
/// resulting idempotent no-op must not be reported as an application warning.
/// </summary>
public sealed class SpreadNoWorkLogPolicyTests
{
    [Theory]
    [InlineData("No viable destinations for [tv-hd] Show.S01E01 — release already present on all 2 candidate site(s) — no new destination to spread to")]
    [InlineData("RELEASE ALREADY PRESENT ON ALL 3 CANDIDATE SITE(S)")]
    public void Already_present_everywhere_is_expected_no_work(string message)
        => Assert.True(SpreadJob.IsExpectedNoWorkOutcome(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No viable destinations — affil-blocked (zephyr)")]
    [InlineData("Source scan never succeeded")]
    [InlineData("STOR failed: 425 Can't build data connection")]
    public void Real_or_actionable_failures_remain_warnings(string? message)
        => Assert.False(SpreadJob.IsExpectedNoWorkOutcome(message));
}
