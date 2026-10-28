using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class SiteHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 0, 30)]
    [InlineData(2, 1, 60)]
    [InlineData(3, 2, 120)]
    [InlineData(4, 4, 240)]
    [InlineData(5, 9, 300)]
    [InlineData(100, 29, 300)]
    public void InitialThirtyMinutesPreserveExistingLadder(int attempt, int minutes, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SiteHealthPolicy.RetryDelay(attempt, TimeSpan.FromMinutes(minutes), 0, SiteFailureKind.Unreachable));

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 60)]
    [InlineData(2, 240)]
    [InlineData(3, 240)]
    public void ProlongedOutageEscalates(int step, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SiteHealthPolicy.RetryDelay(10, TimeSpan.FromMinutes(30), step, SiteFailureKind.Timeout));

    [Theory]
    [InlineData(SiteFailureKind.Authentication, 0, 30)]
    [InlineData(SiteFailureKind.BncCooldown, 0, 120)]
    [InlineData(SiteFailureKind.Authentication, 2, 240)]
    [InlineData(SiteFailureKind.BncCooldown, 2, 240)]
    public void ExistingSafetyFloorsNeverShortenLongOutageLadder(SiteFailureKind kind, int step, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SiteHealthPolicy.RetryDelay(10, TimeSpan.FromHours(2), step, kind));

    [Theory]
    [InlineData(SiteFailureKind.Unreachable, SiteHealthState.Down)]
    [InlineData(SiteFailureKind.Timeout, SiteHealthState.Down)]
    [InlineData(SiteFailureKind.Authentication, SiteHealthState.AuthRejected)]
    [InlineData(SiteFailureKind.BncCooldown, SiteHealthState.Quarantined)]
    [InlineData(SiteFailureKind.Refused, SiteHealthState.Degraded)]
    [InlineData(SiteFailureKind.LoginLimit, SiteHealthState.Degraded)]
    [InlineData(SiteFailureKind.LocalCapacity, SiteHealthState.Degraded)]
    [InlineData(SiteFailureKind.Unknown, SiteHealthState.Degraded)]
    public void StateUsesTypedEvidence(SiteFailureKind kind, SiteHealthState state) =>
        Assert.Equal(state, SiteHealthPolicy.StateFor(kind, TimeSpan.Zero));

    [Fact]
    public void OldAuthenticatedConnectionCannotProveCredentialsRecovered()
    {
        Assert.False(SiteHealthPolicy.ExistingConnectionCanRecover(SiteFailureKind.Authentication));
        Assert.True(SiteHealthPolicy.ExistingConnectionCanRecover(SiteFailureKind.Unreachable));
    }

    [Fact]
    public void LocalPoolCapacityNeverCreatesOrExtendsRemoteOutage()
    {
        var writes = 0;
        var tracker = new SiteHealthTracker(() => null, _ => writes++);
        Assert.Null(tracker.Failed("site", "route", SiteFailureKind.LocalCapacity, Start));
        Assert.True(tracker.CanAttempt("site", "route", Start));
        Assert.Equal(0, writes);
        tracker.Failed("site", "route", SiteFailureKind.Unreachable, Start);
        var original = tracker.Get("site", "route");
        Assert.Null(tracker.Failed("site", "route", SiteFailureKind.LocalCapacity, Start.AddHours(1)));
        Assert.Equal(original, tracker.Get("site", "route"));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void RestartPreservesOutageDueAttemptsAndEscalation()
    {
        string? disk = null;
        SiteHealthTracker Open() => new(() => disk, text => disk = text);
        var tracker = Open();
        Assert.NotNull(tracker.Failed("site", "route", SiteFailureKind.Unreachable, Start));
        Assert.False(tracker.CanAttempt("site", "route", Start.AddSeconds(29)));
        Assert.True(tracker.CanAttempt("site", "route", Start.AddSeconds(30)));
        tracker = Open();
        Assert.Null(tracker.Failed("site", "route", SiteFailureKind.Unreachable, Start.AddMinutes(30)));
        var firstEscalation = Open().Get("site", "route");
        Assert.Equal(Start, firstEscalation.FirstFailureUtc);
        Assert.Equal(2, firstEscalation.Attempts);
        Assert.Equal(1, firstEscalation.EscalationStep);
        Assert.Equal(Start.AddMinutes(45), firstEscalation.NextRetryUtc);
        tracker = Open();
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(45));
        Assert.Equal(Start.AddMinutes(105), Open().Get("site", "route").NextRetryUtc);
        tracker = Open();
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(105));
        Assert.Equal(Start.AddMinutes(345), Open().Get("site", "route").NextRetryUtc);
        Assert.Equal(SiteHealthState.Quarantined, Open().Get("site", "route").State);
    }

    [Fact]
    public void OutageAndRecoveryAreNotRepeatedAcrossRestarts()
    {
        string? disk = null;
        SiteHealthTracker Open() => new(() => disk, text => disk = text);
        Assert.Null(Open().Recovered("site", "route", Start));
        Assert.False(Open().Failed("site", "route", SiteFailureKind.Unreachable, Start.AddMinutes(1))!.Recovered);
        Assert.Null(Open().Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(2)));
        Assert.True(Open().Recovered("site", "route", Start.AddMinutes(3))!.Recovered);
        Assert.Null(Open().Recovered("site", "route", Start.AddMinutes(4)));
        var healthy = Open().Get("site", "route");
        Assert.Equal(SiteHealthState.Healthy, healthy.State);
        Assert.Null(healthy.FirstFailureUtc);
        Assert.Null(healthy.NextRetryUtc);
        Assert.Equal(0, healthy.Attempts);
        Assert.NotNull(Open().Failed("site", "route", SiteFailureKind.Unreachable, Start.AddMinutes(5)));
    }

    [Theory]
    [InlineData(SiteFailureKind.Unreachable, true)]
    [InlineData(SiteFailureKind.Timeout, true)]
    [InlineData(SiteFailureKind.Authentication, false)]
    [InlineData(SiteFailureKind.BncCooldown, false)]
    [InlineData(SiteFailureKind.Refused, false)]
    [InlineData(SiteFailureKind.LoginLimit, false)]
    [InlineData(SiteFailureKind.LocalCapacity, false)]
    [InlineData(SiteFailureKind.Unknown, false)]
    public void OnlyUnreachableAndTransportTimeoutPermitTcpProbe(SiteFailureKind kind, bool expected) =>
        Assert.Equal(expected, SiteHealthPolicy.ProbeAllowed(kind, Start, null, Start.AddMinutes(30)));

    [Fact]
    public void ProbeBudgetPersistsAndDoesNotResetOnRecoveryOrManualAttempts()
    {
        string? disk = null;
        SiteHealthTracker Open() => new(() => disk, text => disk = text);
        var tracker = Open();
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start);
        Assert.False(tracker.TryReserveProbe("site", "route", Start.AddMinutes(29)));
        Assert.True(tracker.TryReserveProbe("site", "route", Start.AddMinutes(30)));
        Assert.False(Open().TryReserveProbe("site", "route", Start.AddMinutes(44).AddSeconds(59)));
        Assert.True(Open().TryReserveProbe("site", "route", Start.AddMinutes(45)));
        tracker = Open();
        tracker.Recovered("site", "route", Start.AddMinutes(46));
        Assert.Equal(Start.AddMinutes(45), Open().Get("site", "route").LastProbeUtc);
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(46));
        Assert.False(tracker.TryReserveProbe("site", "route", Start.AddMinutes(47)));
    }

    // Health state is advisory backoff: losing the file must never hold every automatic
    // login (the v3.14 review found it returned DateTime.MaxValue for every account).
    [Fact]
    public void PersistenceFailureKeepsTheLadderInMemoryInsteadOfHoldingEveryLogin()
    {
        var fail = false;
        var tracker = new SiteHealthTracker(() => null, _ => { if (fail) throw new IOException("unavailable"); });
        tracker.Failed("site", "route", SiteFailureKind.Unreachable, Start);
        fail = true;
        Assert.NotNull(tracker.Failed("other", "route", SiteFailureKind.Unreachable, Start));
        Assert.False(tracker.Get("site", "route").PersistenceHealthy);
        Assert.False(tracker.CanAttempt("other", "route", Start.AddSeconds(29)));
        Assert.True(tracker.CanAttempt("other", "route", Start.AddSeconds(30)));
        Assert.True(tracker.CanAttempt("site", "route", Start.AddDays(1)));
        Assert.True(tracker.TryReserveProbe("site", "route", Start.AddMinutes(30)));
        Assert.Equal(SiteHealthState.Down, tracker.Get("site", "route").State);
        fail = false;
        Assert.NotNull(tracker.Recovered("other", "route", Start.AddMinutes(1)));
        Assert.True(tracker.Get("site", "route").PersistenceHealthy);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("null")]
    [InlineData("[{\"serverId\":\"site\",\"endpointIdentity\":\"route\",\"attempts\":-1}]")]
    [InlineData("[{\"serverId\":\"site\",\"endpointIdentity\":\"route\",\"state\":999}]")]
    public void CorruptStateIsQuarantinedAndTrackingRestartsEmpty(string disk)
    {
        var quarantined = false;
        string? written = null;
        var tracker = new SiteHealthTracker(() => disk, text => written = text, () => quarantined = true);
        Assert.True(quarantined);
        Assert.False(tracker.Get("site", "route").PersistenceHealthy);
        Assert.True(tracker.CanAttempt("site", "route", Start));
        Assert.NotNull(tracker.Failed("site", "route", SiteFailureKind.Unknown, Start));
        Assert.NotNull(written);
        Assert.True(tracker.Get("site", "route").PersistenceHealthy);
        Assert.False(tracker.CanAttempt("site", "route", Start.AddSeconds(29)));
    }

    // A login-limit reply proves the site is up; arming the ladder also blocked the !user ghost kill.
    [Fact]
    public void LoginLimitIsNotAnOutage()
    {
        var tracker = new SiteHealthTracker(() => null, _ => { });
        Assert.Null(tracker.Failed("site", "route", SiteFailureKind.LoginLimit, Start));
        Assert.True(tracker.CanAttempt("site", "route", Start));
        Assert.Null(tracker.Get("site", "route").FirstFailureUtc);
    }

    [Fact]
    public void EndpointChangeDoesNotInheritOldRoutesOutage()
    {
        var tracker = new SiteHealthTracker(() => null, _ => { });
        tracker.Failed("site", "route-a", SiteFailureKind.Unreachable, Start);
        Assert.False(tracker.CanAttempt("site", "route-a", Start));
        Assert.True(tracker.CanAttempt("site", "route-b", Start));
        Assert.Null(tracker.Get("site", "route-b").FirstFailureUtc);
    }

    [Fact]
    public void ExistingAuthenticationHoldImportsOnceWithoutExtendingOrCountingAttempts()
    {
        string? disk = null;
        SiteHealthTracker Open() => new(() => disk, text => disk = text);
        Assert.NotNull(Open().AuthenticationHeld("site", "route", Start, Start.AddMinutes(5)));
        Assert.Null(Open().AuthenticationHeld("site", "route", Start.AddMinutes(1), Start.AddMinutes(5)));
        var held = Open().Get("site", "route");
        Assert.Equal(SiteHealthState.AuthRejected, held.State);
        Assert.Equal(0, held.Attempts);
        Assert.Equal(Start, held.FirstFailureUtc);
        Assert.Equal(Start.AddMinutes(5), held.NextRetryUtc);
        Assert.True(Open().Recovered("site", "route", Start.AddMinutes(2))!.Recovered);
    }

    [Fact]
    public void ImportedAuthenticationHoldCannotShortenExistingLongOutage()
    {
        var tracker = new SiteHealthTracker(() => null, _ => { });
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start);
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(30));
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(45));
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(105));
        tracker.AuthenticationHeld("site", "route", Start.AddMinutes(106), Start.AddMinutes(136));
        Assert.Equal(Start.AddMinutes(345), tracker.Get("site", "route").NextRetryUtc);
        Assert.Equal(4, tracker.Get("site", "route").Attempts);
    }

    [Fact]
    public void ClockMovingBackwardsDoesNotPermitEarlyProbe()
    {
        Assert.False(SiteHealthPolicy.ProbeAllowed(SiteFailureKind.Timeout, Start, Start.AddMinutes(30), Start.AddMinutes(29)));
        Assert.False(SiteHealthPolicy.ProbeAllowed(SiteFailureKind.Timeout, Start, null, Start.AddHours(-1)));
    }

    [Fact]
    public void StaleOrDuplicateObservationCannotClearOutageOrAdvanceLadderAcrossRestart()
    {
        string? disk = null;
        SiteHealthTracker Open() => new(() => disk, text => disk = text);
        Open().Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(2));
        Assert.Null(Open().Recovered("site", "route", Start.AddMinutes(1)));
        Assert.Null(Open().Failed("site", "route", SiteFailureKind.Timeout, Start.AddMinutes(2)));
        Assert.Null(Open().Failed("site", "route", SiteFailureKind.Unreachable, Start));
        Assert.Equal(1, Open().Get("site", "route").Attempts);
        Assert.Equal(SiteHealthState.Down, Open().Get("site", "route").State);
        Assert.NotNull(Open().Recovered("site", "route", Start.AddMinutes(3)));
        Assert.Null(Open().Failed("site", "route", SiteFailureKind.Unreachable, Start.AddMinutes(2)));
        Assert.Equal(SiteHealthState.Healthy, Open().Get("site", "route").State);
    }
}
