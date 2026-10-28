using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using FluentFTP.Exceptions;
using GlDrive.Config;
using GlDrive.Ftp;
using Xunit;
using Failure = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;

namespace GlDrive.Tests;

public sealed class LoginAttemptGovernorTests : IDisposable
{
    private static readonly FtpLoginAccount Account = new("ftp.example.invalid", 21, "member");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-login-budget-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan duration) { _utc += duration; _stamp += duration.Ticks; }
        internal void ShiftUtc(TimeSpan duration) => _utc += duration;
    }
    private static PoolConfig Policy(int limit = 8, bool enabled = true) => new()
    { LoginRateLimitEnabled = enabled, MaxLoginAttemptsPerFiveMinutes = limit };
    private static GovernedLoginAttempt Begin(LoginAttemptGovernor governor, bool ghost = false, bool manual = false,
        PoolConfig? policy = null, FtpLoginAccount? account = null) => governor.Begin(account ?? Account, policy ?? Policy(), manual, ghost);

    [Fact]
    public void SuccessesCountAndOneSlotIsReservedForGhostRecovery()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        for (var i = 0; i < 7; i++) governor.Complete(Begin(governor), null, false, false);
        var denied = Assert.Throws<FtpLoginHeldException>(() => Begin(governor));
        Assert.True(denied.LoginRateHold);
        Assert.False(denied.AuthenticationHold);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(5), denied.RetryAtUtc);
        Begin(governor, ghost: true);
        Assert.Equal(8, governor.Snapshot(Account).AttemptsInWindow);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor, ghost: true));
        clock.Advance(TimeSpan.FromMinutes(5));
        Begin(governor);
        Assert.Equal(1, governor.Snapshot(Account).AttemptsInWindow);
    }

    [Fact]
    public void ParallelCallersShareOneAtomicAccountBudget()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        var admitted = new ConcurrentBag<GovernedLoginAttempt>();
        Parallel.For(0, 50, _ => { try { admitted.Add(Begin(governor)); } catch (FtpLoginHeldException) { } });
        Assert.Equal(7, admitted.Count);
        Assert.Equal(7, governor.Snapshot(Account).AttemptsInWindow);
        Begin(governor, account: Account with { Username = "other" });
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor,
            account: new(" FTP.EXAMPLE.INVALID ", 21, "MEMBER")));
    }

    [Theory]
    [InlineData(Failure.ServerRefused, false, false, 90, LoginHoldReason.Refused)]
    [InlineData(Failure.BncLoginLimit, false, false, 90, LoginHoldReason.LoginLimit)]
    [InlineData(Failure.HostUnreachable, false, false, 60, LoginHoldReason.Unreachable)]
    [InlineData(Failure.ConnectFault, true, false, 60, LoginHoldReason.ConnectTimeout)]
    [InlineData(Failure.ConnectFault, false, true, 7200, LoginHoldReason.BncCooldown)]
    public void ActualFailuresArmAccountWideHolds(Failure failure, bool timedOut, bool cooldown,
        int seconds, LoginHoldReason reason)
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        governor.Complete(Begin(governor), failure, timedOut, cooldown);
        var retry = clock.GetUtcNow().UtcDateTime.AddSeconds(seconds);
        Assert.Equal(reason, governor.Snapshot(Account).HoldReason);
        Assert.Equal(retry, Assert.Throws<FtpLoginHeldException>(() => Begin(governor)).RetryAtUtc);
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Equal(retry, Assert.Throws<FtpLoginHeldException>(() => Begin(governor)).RetryAtUtc);
        clock.Advance(TimeSpan.FromSeconds(1));
        Begin(governor);
    }

    [Fact]
    public void GhostCanRecoverLoginLimitButCannotBypassBncCooldownOrGlobalMinuteGap()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        governor.Complete(Begin(governor), Failure.BncLoginLimit, false, false);
        var ghost = Begin(governor, ghost: true);
        governor.Complete(ghost, null, false, false);
        Assert.Equal(LoginHoldReason.None, governor.Snapshot(Account).HoldReason);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor, ghost: true));
        clock.Advance(TimeSpan.FromMinutes(1));
        governor.Complete(Begin(governor), Failure.ConnectFault, false, true);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor, ghost: true));
    }

    [Fact]
    public void EarlierParallelSuccessCannotClearNewFailureHold()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        var earlier = Begin(governor);
        governor.Complete(Begin(governor), Failure.ServerRefused, false, false);
        governor.Complete(earlier, null, false, false);
        Assert.Equal(LoginHoldReason.Refused, governor.Snapshot(Account).HoldReason);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor));
    }

    [Fact]
    public void CallerCancellationDoesNotArmAHostHoldAndPreconnectCancellationRefundsAttempt()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        var cancelled = Begin(governor, ghost: true);
        governor.CancelBeforeConnect(cancelled);
        Assert.Equal(0, governor.Snapshot(Account).AttemptsInWindow);
        var ghost = Begin(governor, ghost: true);
        governor.Complete(ghost, Failure.CallerAbandoned, true, true);
        Assert.Equal(LoginHoldReason.None, governor.Snapshot(Account).HoldReason);
        Assert.Equal(1, governor.Snapshot(Account).AttemptsInWindow);
    }

    [Fact]
    public void ManualRetryIsOneBypassAndSuccessfulRetryClearsItsHold()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        governor.Complete(Begin(governor), Failure.ConnectFault, false, true);
        governor.RequestManualRetry(Account);
        var retry = Begin(governor);
        Assert.True(retry.Manual);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor));
        governor.Complete(retry, null, false, false);
        Assert.Equal(LoginHoldReason.None, governor.Snapshot(Account).HoldReason);
    }

    [Fact]
    public void DisabledGovernorDoesNotAlterAuthenticationCircuitOrLimitNewLogins()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        for (var i = 0; i < 20; i++) Begin(governor, policy: Policy(enabled: false));
        Assert.False(governor.Snapshot(Account).Enabled);
        Assert.Null(governor.Snapshot(Account).RetryAtUtc);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor));
    }

    [Fact]
    public void MonotonicRateWindowSurvivesWallClockJump()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        for (var i = 0; i < 7; i++) Begin(governor);
        clock.ShiftUtc(TimeSpan.FromDays(1));
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor));
        clock.Advance(TimeSpan.FromMinutes(5));
        Begin(governor);
    }

    [Fact]
    public void RateWindowAndHoldSurviveRestartWithoutPlainAccountIdentity()
    {
        var clock = new Clock();
        var path = Path.Combine(_root, "governor.json");
        var governor = new LoginAttemptGovernor(clock, path);
        governor.Complete(Begin(governor), Failure.ConnectFault, false, true);
        clock.Advance(TimeSpan.FromMinutes(10));
        governor = new LoginAttemptGovernor(clock, path);
        Assert.Equal(LoginHoldReason.BncCooldown, governor.Snapshot(Account).HoldReason);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(110),
            Assert.Throws<FtpLoginHeldException>(() => Begin(governor)).RetryAtUtc);
        Assert.DoesNotContain(Account.Host, File.ReadAllText(path));
        Assert.DoesNotContain(Account.Username, File.ReadAllText(path));
    }

    // A local bookkeeping file must never stop every FTP login on every account.
    [Fact]
    public void CorruptBudgetIsQuarantinedAndStartsEmpty()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "governor.json");
        File.WriteAllText(path, "not-json");
        var governor = new LoginAttemptGovernor(new Clock(), path);
        Assert.False(Begin(governor).Manual);
        Assert.True(governor.Snapshot(Account).PersistenceHealthy);
        Assert.Equal("not-json", File.ReadAllText(path + ".corrupt"));
        Assert.NotEqual("not-json", File.ReadAllText(path));
    }

    [Fact]
    public void UnwritableBudgetStillLimitsInMemoryWithoutDenyingLogins()
    {
        var path = Path.Combine(_root, "bad\0name"); // every save fails fast (no SecureFile retry)
        var governor = new LoginAttemptGovernor(new Clock(), path);
        for (var i = 0; i < 7; i++) governor.Complete(Begin(governor), null, false, false);
        Assert.False(governor.Snapshot(Account).PersistenceHealthy);
        Assert.Throws<FtpLoginHeldException>(() => Begin(governor)); // the rate budget itself still applies
    }

    [Theory]
    [InlineData("Timed out trying to connect!", true)]
    [InlineData("connection timed out", true)]
    [InlineData("TLS handshake timed out", false)]
    [InlineData("Timed out trying to read data from socket stream", false)]
    [InlineData("operation timed out", false)]
    public void OnlyConnectTimeoutEvidenceCanEnableTcpProbeClassification(string message, bool expected) =>
        Assert.Equal(expected, LoginAttemptGovernor.IsTimeout(new IOException(message)));

    [Fact]
    public void TypedSocketTimeoutAndExplicitFtpCooldownRepliesAreRecognized()
    {
        Assert.True(LoginAttemptGovernor.IsTimeout(new IOException("wrapper", new SocketException((int)SocketError.TimedOut))));
        Assert.False(LoginAttemptGovernor.IsTimeout(new IOException("TLS handshake failed", new SocketException((int)SocketError.TimedOut))));
        Assert.False(LoginAttemptGovernor.IsTimeout(new IOException("Read timed out", new SocketException((int)SocketError.TimedOut))));
        Assert.False(LoginAttemptGovernor.IsBncCooldown(new FtpAuthenticationException("530", "Login incorrect")));
    }

    // A bare 421/450 is how most daemons say "too many users"; a two-hour account hold on it
    // would stop racing from the source site for two hours on a login-limit reply.
    [Theory]
    [InlineData("421", "Service unavailable", false)]
    [InlineData("450", "Too many connections", false)]
    [InlineData("421", "Too many users - try again later", false)]
    [InlineData("530", "Sorry, your account is restricted to 3 simultaneous logins.", false)]
    [InlineData("421", "Reconnecting too fast, cooldown active", true)]
    [InlineData("421", "Rate limit exceeded, come back later", true)]
    public void OnlyExplicitServerThrottleRepliesEarnTheLongHold(string code, string reply, bool expected) =>
        Assert.Equal(expected, LoginAttemptGovernor.IsBncCooldown(new FtpCommandException(code, reply)));

    [Fact]
    public void LocalPoolCooldownTextIsNeverReadAsServerEvidence() =>
        Assert.False(LoginAttemptGovernor.IsBncCooldown(
            new InvalidOperationException("Server in BNC cooldown — not attempting new connection")));

    [Fact]
    public async Task GuardLocalDenialsNeverCallConnectOrPublishServerFailure()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var circuit = new AuthenticationCircuitBreaker(clock);
        var observations = new List<FtpLoginObservation>();
        var calls = 0;
        Task Connect(CancellationToken _) { calls++; return Task.CompletedTask; }
        for (var i = 0; i < 7; i++)
            await GuardedFtpLogin.RunAsync(circuit, Account, "password", Connect, governor: governor, observe: observations.Add);
        await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "password", Connect,
            governor: governor, observe: observations.Add));
        Assert.Equal(7, calls);
        Assert.Equal(7, observations.Count);
        Assert.All(observations, observation => Assert.True(observation.Succeeded));
        Assert.False(circuit.IsHeld(Account));
    }

    [Fact]
    public async Task ExistingChangedPasswordSingleProbeRemainsAvailableAfterRateLimit()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var circuit = new AuthenticationCircuitBreaker(clock);
        for (var i = 0; i < 6; i++)
            await GuardedFtpLogin.RunAsync(circuit, Account, "wrong", _ => Task.CompletedTask, governor: governor);
        await Assert.ThrowsAsync<FtpAuthenticationException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "wrong",
            _ => throw new FtpAuthenticationException("530", "Login incorrect"), governor: governor));
        Assert.True(circuit.IsHeld(Account));
        var connected = false;
        await GuardedFtpLogin.RunAsync(circuit, Account, "fixed", _ => { connected = true; return Task.CompletedTask; }, governor: governor);
        Assert.True(connected);
        Assert.False(circuit.IsHeld(Account));
        Assert.Equal(8, governor.Snapshot(Account).AttemptsInWindow);
    }

    [Fact]
    public void ChangedHeldCredentialQueryNeverReservesOrClearsARecoveryProbe()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        Assert.False(circuit.HasChangedHeldCredentials(Account, "fixed"));
        circuit.Failed(circuit.Begin(Account, "wrong", false, true), Failure.AuthRejected);
        var nextProbe = circuit.RetryAt(Account);
        Assert.False(circuit.HasChangedHeldCredentials(Account, "wrong"));
        Assert.True(circuit.HasChangedHeldCredentials(Account, "fixed"));
        Assert.True(circuit.HasChangedHeldCredentials(Account, "fixed"));
        Assert.Equal(nextProbe, circuit.RetryAt(Account));
        Assert.True(circuit.IsHeld(Account));
        var probe = circuit.Begin(Account, "fixed", false, true);
        Assert.True(probe.CredentialChanged);
        Assert.False(circuit.HasChangedHeldCredentials(Account, "fixed"));
        Assert.Throws<FtpLoginHeldException>(() => circuit.Begin(Account, "fixed", false, true));
        circuit.Succeeded(probe);
        Assert.False(circuit.HasChangedHeldCredentials(Account, "different"));
    }

    [Fact]
    public async Task ScheduledAuthenticationProbeHonorsLongSiteDeadlineButChangedPasswordCanRetry()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var circuit = new AuthenticationCircuitBreaker(clock);
        await Assert.ThrowsAsync<FtpAuthenticationException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "wrong",
            _ => throw new FtpAuthenticationException("530", "Login incorrect"), governor: governor));
        clock.Advance(TimeSpan.FromMinutes(30));
        var deadline = clock.GetUtcNow().UtcDateTime.AddHours(4);
        var calls = 0;
        var observations = new List<FtpLoginObservation>();
        Task Connect(CancellationToken _) { calls++; return Task.CompletedTask; }
        var denied = await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit,
            Account, "wrong", Connect, governor: governor, observe: observations.Add, admissionDeadline: _ => deadline));
        Assert.Equal(deadline, denied.RetryAtUtc);
        Assert.Equal(0, calls);
        Assert.Empty(observations);
        Assert.Equal(0, governor.Snapshot(Account).AttemptsInWindow);
        Assert.True(circuit.IsHeld(Account));

        await GuardedFtpLogin.RunAsync(circuit, Account, "fixed", Connect, governor: governor,
            observe: observations.Add, admissionDeadline: _ => deadline);
        Assert.Equal(1, calls);
        Assert.Single(observations);
        Assert.False(circuit.IsHeld(Account));
    }

    [Fact]
    public async Task ObservationRunsOutsideGovernorLockAndCannotTurnSuccessIntoFailure()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var circuit = new AuthenticationCircuitBreaker(clock);
        var observed = false;
        await GuardedFtpLogin.RunAsync(circuit, Account, "password", _ => Task.CompletedTask, governor: governor,
            observe: observation =>
            {
                Assert.Equal(1, Task.Run(() => governor.Snapshot(Account)).WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult().AttemptsInWindow);
                observed = observation.Succeeded;
                throw new InvalidOperationException("observer failure");
            });
        Assert.True(observed);
        Assert.Equal(LoginHoldReason.None, governor.Snapshot(Account).HoldReason);
    }

    [Fact]
    public async Task SiteLadderGatesEveryAutomaticAttemptWithoutCountingOrPublishingFailure()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var circuit = new AuthenticationCircuitBreaker(clock);
        var deadline = clock.GetUtcNow().UtcDateTime.AddHours(4);
        var calls = 0;
        var observations = new List<FtpLoginObservation>();
        Task Connect(CancellationToken _) { calls++; return Task.CompletedTask; }
        for (var i = 0; i < 3; i++)
        {
            var denied = await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit,
                Account, "password", Connect, governor: governor, observe: observations.Add, admissionDeadline: _ => deadline));
            Assert.Equal(deadline, denied.RetryAtUtc);
        }
        Assert.Equal(0, calls);
        Assert.Empty(observations);
        Assert.Equal(0, governor.Snapshot(Account).AttemptsInWindow);
        governor.RequestManualRetry(Account);
        await GuardedFtpLogin.RunAsync(circuit, Account, "password", Connect, governor: governor,
            observe: observations.Add, admissionDeadline: _ => deadline);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit,
            Account, "password", Connect, governor: governor, admissionDeadline: _ => deadline));
        clock.Advance(TimeSpan.FromHours(4));
        await GuardedFtpLogin.RunAsync(circuit, Account, "password", Connect, governor: governor,
            observe: observations.Add, admissionDeadline: _ => deadline);
        Assert.Equal(2, calls);
        Assert.Equal(2, observations.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
