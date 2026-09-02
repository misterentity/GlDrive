using System;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Covers the escalation that bounds the invite-only retry loop.
///
/// Regression target (2026-08-31 23:46 → 2026-09-01 09:22): the standing retry
/// re-issued an accepted SITE INVITE 25 times over 9h50m on two independent IRC
/// networks and never escalated. A process restart fixed it in 746 ms.
/// </summary>
public class InviteRecoveryPolicyTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]  // first standing-retry warning
    [InlineData(7)]  // ~35 min in — still inside the "bot may come back" window
    public void DoesNotEscalateWhileTheStandingRetryIsStillReasonable(int attempts)
    {
        Assert.False(InviteRecoveryPolicy.ShouldForceReconnect(attempts, T0, null));
    }

    [Fact]
    public void EscalatesOnceTheRetryHasClearlyStoppedWorking()
    {
        Assert.True(InviteRecoveryPolicy.ShouldForceReconnect(
            InviteRecoveryPolicy.EscalateAfterAttempts, T0, null));
    }

    [Fact]
    public void DoesNotReconnectAgainInsideTheCooldown()
    {
        var last = T0;
        // Attempts keep climbing (the retry keeps failing), but the session was just
        // rebuilt — rebuilding it again immediately is the loop we are preventing.
        Assert.False(InviteRecoveryPolicy.ShouldForceReconnect(
            40, last + TimeSpan.FromMinutes(119), last));
    }

    [Fact]
    public void ReconnectsAgainOnceTheCooldownExpires()
    {
        var last = T0;
        Assert.True(InviteRecoveryPolicy.ShouldForceReconnect(
            40, last + InviteRecoveryPolicy.MinReconnectInterval, last));
    }

    /// <summary>
    /// The whole point of the change: replay the real 9h50m outage and assert it now
    /// produces forced reconnects instead of nothing. Attempt 4 is the first standing
    /// retry (23:46), and the observed schedule was 5m, 15m, 15m, then 30m forever.
    /// </summary>
    [Fact]
    public void TheProductionOutageWouldHaveEscalatedRepeatedly()
    {
        var now = new DateTime(2026, 8, 31, 23, 46, 0, DateTimeKind.Utc);
        var outageEnd = new DateTime(2026, 9, 1, 9, 36, 0, DateTimeKind.Utc);
        DateTime? lastForced = null;
        var forced = 0;

        for (var attempts = 4; now < outageEnd; attempts++)
        {
            if (InviteRecoveryPolicy.ShouldForceReconnect(attempts, now, lastForced))
            {
                lastForced = now;
                forced++;
            }

            now += attempts switch
            {
                <= 4 => TimeSpan.FromMinutes(5),
                <= 6 => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromMinutes(30)
            };
        }

        // Was 0 in v3.10.96. Bounded by the 2h cooldown across a 9h50m window.
        Assert.InRange(forced, 4, 6);
    }

    [Fact]
    public void FirstEscalationLandsWithinAboutAnHourOfTheFirstWarning()
    {
        // Guards the constant against drifting into "so late it may as well be never".
        var now = new DateTime(2026, 8, 31, 23, 46, 0, DateTimeKind.Utc);
        var start = now;
        var attempts = 4;
        while (!InviteRecoveryPolicy.ShouldForceReconnect(attempts, now, null))
        {
            now += attempts switch
            {
                <= 4 => TimeSpan.FromMinutes(5),
                <= 6 => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromMinutes(30)
            };
            attempts++;
        }

        Assert.InRange(now - start, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(90));
    }
}
