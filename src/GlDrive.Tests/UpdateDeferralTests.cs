using System;
using System.IO;
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

    // --- Visible escalation (2026-07-25) --------------------------------------------------
    // With the relaxed gate (block only on in-flight transfers, not any active job), a hold
    // this long means a genuinely continuous transfer stream. Surface it before the forced
    // install interrupts a transfer, so the user can pause and apply cleanly.

    [Fact]
    public void ShortHold_DoesNotEscalate()
    {
        Assert.False(UpdateChecker.ShouldEscalateDeferral(TimeSpan.Zero));
        Assert.False(UpdateChecker.ShouldEscalateDeferral(TimeSpan.FromHours(3)));
        Assert.False(UpdateChecker.ShouldEscalateDeferral(UpdateChecker.EscalateDeferralAfter - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void HoldPastTheThreshold_Escalates()
    {
        Assert.True(UpdateChecker.ShouldEscalateDeferral(UpdateChecker.EscalateDeferralAfter));
        Assert.True(UpdateChecker.ShouldEscalateDeferral(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void EscalationFiresBeforeTheForcedInstall()
    {
        // The heads-up must land while the update is still being deferred, i.e. strictly
        // before the forced install — otherwise it is pointless noise.
        Assert.True(UpdateChecker.EscalateDeferralAfter < UpdateChecker.MaxInstallDeferral);

        // At the escalation threshold we warn but do NOT yet force.
        Assert.True(UpdateChecker.ShouldEscalateDeferral(UpdateChecker.EscalateDeferralAfter));
        Assert.False(UpdateChecker.ShouldForceDeferredInstall(UpdateChecker.EscalateDeferralAfter));
    }
}

/// <summary>
/// Cover for the follow-up defect (2026-07-24): the 12h deadline lived in a field, so every
/// restart reset it to zero. With a 3h poll interval a box that restarts even once per 12h
/// could never accumulate the hold, and the forced install never fired — leaving the exact
/// indefinite starvation the deadline was added to end. Observed live: 9 consecutive
/// "Update available: 3.10.36 → 3.10.38" polls across 27h with no install.
/// </summary>
public sealed class UpdateDeferralPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-deferral-" + Guid.NewGuid().ToString("N"));
    private string Marker => Path.Combine(_dir, ".update-deferred");

    public UpdateDeferralPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void NoMarker_StartsAFreshHold()
    {
        Assert.Null(UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow));
    }

    [Fact]
    public void HoldSurvivesARestart()
    {
        // Process 1 defers the update, then dies (watchdog restart, crash, manual restart).
        var now = DateTime.UtcNow;
        var firstDeferral = now.AddHours(-11);
        UpdateChecker.WriteDeferralStartAt(Marker, "v3.10.39", firstDeferral);

        // Process 2 must inherit the 11h hold rather than restarting the clock at zero.
        var resumed = UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", now);
        Assert.NotNull(resumed);
        Assert.Equal(firstDeferral, resumed!.Value, TimeSpan.FromSeconds(1));
        Assert.False(UpdateChecker.ShouldForceDeferredInstall(now - resumed.Value));

        // One more 3h poll after the restart now crosses the deadline — pre-fix it never would.
        Assert.True(UpdateChecker.ShouldForceDeferredInstall(now.AddHours(3) - resumed.Value));
    }

    [Fact]
    public void ANewerReleaseDoesNotInheritTheOldHold()
    {
        // Otherwise a fresh release would install immediately on its first poll, defeating
        // the courtesy gate entirely.
        UpdateChecker.WriteDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow.AddHours(-20));
        Assert.Null(UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.40", DateTime.UtcNow));
    }

    [Fact]
    public void FutureStartIsRejectedRatherThanTrusted()
    {
        // Clock skew must not push the deadline out forever — that is the starvation bug again.
        UpdateChecker.WriteDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow.AddDays(3));
        Assert.Null(UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow));
    }

    [Fact]
    public void CorruptMarkerFallsBackToAFreshHold()
    {
        File.WriteAllText(Marker, "this is not a deferral record");
        Assert.Null(UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow));
    }

    [Fact]
    public void ClearingRemovesTheHold()
    {
        UpdateChecker.WriteDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow.AddHours(-5));
        UpdateChecker.ClearDeferralAt(Marker);
        Assert.False(File.Exists(Marker));
        Assert.Null(UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow));
        UpdateChecker.ClearDeferralAt(Marker); // idempotent
    }

    [Fact]
    public void RoundTripSurvivesLocalTimeInput()
    {
        // The marker is written from a UTC field today, but a local-kind DateTime must not
        // silently shift the deadline by the UTC offset.
        var localStart = DateTime.Now.AddHours(-13);
        UpdateChecker.WriteDeferralStartAt(Marker, "v3.10.39", localStart);
        var read = UpdateChecker.ReadDeferralStartAt(Marker, "v3.10.39", DateTime.UtcNow);
        Assert.NotNull(read);
        Assert.Equal(localStart.ToUniversalTime(), read!.Value, TimeSpan.FromSeconds(1));
        Assert.True(UpdateChecker.ShouldForceDeferredInstall(DateTime.UtcNow - read.Value));
    }
}
