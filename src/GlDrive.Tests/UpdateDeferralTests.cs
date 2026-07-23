using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-07-23 update starvation: auto-install is gated on
/// "no spread races running", sampled at one instant every 3h. On a machine that races
/// around the clock that sample is essentially never idle, so v3.10.37 was deferred on
/// three consecutive polls across 10.5h with no path to ever installing — and the three
/// identical "deferred (busy)" log lines looked like normal operation.
/// </summary>
public sealed class UpdateDeferralTests
{
    [Fact]
    public void ShortHold_KeepsWaitingForAnIdleMoment()
    {
        // The courtesy gate must still work normally — the common case is that a race
        // finishes and the next poll installs cleanly.
        Assert.False(UpdateChecker.ShouldForceDeferredInstall(TimeSpan.Zero));
        Assert.False(UpdateChecker.ShouldForceDeferredInstall(TimeSpan.FromHours(3)));
        Assert.False(UpdateChecker.ShouldForceDeferredInstall(TimeSpan.FromHours(10.5)));
    }

    [Fact]
    public void HoldAtOrPastTheDeadline_ForcesTheInstall()
    {
        Assert.True(UpdateChecker.ShouldForceDeferredInstall(UpdateChecker.MaxInstallDeferral));
        Assert.True(UpdateChecker.ShouldForceDeferredInstall(TimeSpan.FromHours(13)));
        Assert.True(UpdateChecker.ShouldForceDeferredInstall(TimeSpan.FromDays(2)));
    }

    [Fact]
    public void DeadlineIsReachableWithinADay()
    {
        // A deadline longer than a day would let a fix sit unshipped indefinitely on a
        // busy box, which is the failure this exists to prevent.
        Assert.True(UpdateChecker.MaxInstallDeferral <= TimeSpan.FromHours(24));
        // ...but long enough that a normal race (30 min hard ceiling) never triggers it.
        Assert.True(UpdateChecker.MaxInstallDeferral >= TimeSpan.FromHours(6));
    }
}
