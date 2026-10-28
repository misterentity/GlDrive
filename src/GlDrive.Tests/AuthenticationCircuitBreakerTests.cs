using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentFTP.Exceptions;
using GlDrive.Ftp;
using Xunit;
using Failure = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;

namespace GlDrive.Tests;

public class AuthenticationCircuitBreakerTests
{
    private static readonly FtpLoginAccount Account = new("ftp.example.invalid", 21, "test-user");
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan interval) { _utc += interval; _stamp += interval.Ticks; }
        internal void ShiftUtc(TimeSpan interval) => _utc += interval;
    }

    private static FtpLoginAttempt Begin(AuthenticationCircuitBreaker circuit, string password = "wrong",
        bool manual = false, bool probe = true, FtpLoginAccount? account = null) =>
        circuit.Begin(account ?? Account, password, manual, probe);

    private static void Reject(AuthenticationCircuitBreaker circuit) => circuit.Failed(Begin(circuit), Failure.AuthRejected);

    [Theory]
    [InlineData("530 Login incorrect", Failure.AuthRejected)]
    [InlineData("530 Authentication failed", Failure.AuthRejected)]
    [InlineData("530 Invalid password", Failure.AuthRejected)]
    [InlineData("530 Sorry, restricted to 4 simultaneous logins", Failure.BncLoginLimit)]
    [InlineData("530 Too many connections", Failure.BncLoginLimit)]
    [InlineData("530 Already logged in", Failure.BncLoginLimit)]
    [InlineData("530 Upstream unavailable", Failure.ConnectFault)]
    [InlineData("530 Unable to connect, login failed", Failure.ConnectFault)]
    [InlineData("SOCKS5 authentication failed", Failure.ConnectFault)]
    [InlineData("SOCKS5 proxy at localhost:5300 authentication failed", Failure.ConnectFault)]
    public void ClassifierSeparatesCredentialsFromCapacityAndTransport(string message, Failure expected)
    {
        var ex = new IOException("wrapper", new Exception(message));
        Assert.Equal(expected, ConnectFailureClassifier.Classify(ex, false,
            ConnectFailureClassifier.HasLoginLimitError(ex), false));
    }

    [Fact]
    public void TypedAuthenticationStillExcludesBncFailureAndHonorsCancellation()
    {
        var auth = new FtpAuthenticationException("530", "Login incorrect");
        Assert.Equal(Failure.AuthRejected, ConnectFailureClassifier.Classify(auth, false, false, false));
        var bnc = new FtpAuthenticationException("530", "BNC upstream temporarily unavailable");
        Assert.Equal(Failure.ConnectFault, ConnectFailureClassifier.Classify(bnc, false, false, false));
        var cancelled = new OperationCanceledException("530 Login incorrect", auth);
        Assert.Equal(Failure.CallerAbandoned, ConnectFailureClassifier.Classify(cancelled, true, false, false));
    }

    [Theory]
    [InlineData("User limit exceeded")]
    [InlineData("Maximum number of connections reached")]
    [InlineData("Max users reached")]
    [InlineData("Maximum logins exceeded")]
    [InlineData("Too many logins")]
    public void TypedCapacityReplyNeverCreatesAuthenticationHold(string message)
    {
        var ex = new FtpAuthenticationException("530", message);
        Assert.Equal(Failure.BncLoginLimit, ConnectFailureClassifier.Classify(ex, false, false, false));
        Assert.Equal(Failure.ServerRefused, ConnectFailureClassifier.Classify(ex, false, false, true));
    }

    [Fact]
    public void AccountKeyNormalizesHostUserButSeparatesPortAndAccount()
    {
        var circuit = new AuthenticationCircuitBreaker();
        Reject(circuit);
        Assert.True(circuit.IsHeld(new(" FTP.EXAMPLE.INVALID ", 21, "TEST-USER")));
        Assert.False(circuit.IsHeld(Account with { Port = 2121 }));
        Assert.False(circuit.IsHeld(Account with { Username = "other" }));
    }

    [Fact]
    public void DueRecoveryHasOneAtomicPermitAcrossConcurrentConsumers()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        Reject(circuit);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        clock.Advance(TimeSpan.FromMinutes(30));
        var admitted = new System.Collections.Concurrent.ConcurrentBag<FtpLoginAttempt>();
        Parallel.For(0, 24, _ => { try { admitted.Add(Begin(circuit)); } catch (FtpLoginHeldException) { } });
        var attempt = Assert.Single(admitted);
        circuit.Failed(attempt, Failure.ConnectFault);
        Assert.True(circuit.IsHeld(Account));
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        clock.Advance(TimeSpan.FromMinutes(30));
        circuit.Succeeded(Begin(circuit));
        Assert.False(circuit.IsHeld(Account));
        Assert.Null(circuit.RetryAt(Account));
    }

    [Fact]
    public void WallClockJumpCannotAdmitEarlyAutomaticProbe()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        Reject(circuit);
        clock.ShiftUtc(TimeSpan.FromDays(1));
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        clock.ShiftUtc(TimeSpan.FromDays(-2));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(Begin(circuit).RecoveryProbe);
    }

    [Fact]
    public void ManualRetryAdmitsOneProbeAndDoesNotRepeatNotification()
    {
        var notifications = 0;
        var circuit = new AuthenticationCircuitBreaker { NotificationSink = _ => { notifications++; return true; } };
        Reject(circuit);
        circuit.RequestManualRetry(Account);
        var probe = Begin(circuit);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        circuit.Failed(probe, Failure.AuthRejected);
        Assert.Equal(1, notifications);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
    }

    [Fact]
    public void ChangedPasswordGetsSingleImmediateProbe()
    {
        var circuit = new AuthenticationCircuitBreaker();
        Reject(circuit);
        var probe = Begin(circuit, "fixed");
        Assert.True(probe.RecoveryProbe);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit, "fixed"));
        circuit.Succeeded(probe);
        Assert.False(circuit.IsHeld(Account));
    }

    [Fact]
    public void EarlierParallelSuccessCannotClearNewRejection()
    {
        var circuit = new AuthenticationCircuitBreaker();
        var earlier = Begin(circuit);
        Reject(circuit);
        circuit.Succeeded(earlier);
        Assert.True(circuit.IsHeld(Account));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetDoesNotOverlapProbeAndIgnoresItsStaleResult(bool oldSucceeded)
    {
        var circuit = new AuthenticationCircuitBreaker();
        Reject(circuit);
        var old = Begin(circuit, manual: true);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit, "fixed"));
        if (oldSucceeded) circuit.Succeeded(old); else circuit.Failed(old, Failure.AuthRejected);
        var current = Begin(circuit, "fixed");
        circuit.Succeeded(current);
        Assert.False(circuit.IsHeld(Account));
    }

    [Fact]
    public void GhostCannotProbeHeldAccountEvenWhenDue()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        Reject(circuit);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit, probe: false));
        Assert.True(Begin(circuit).RecoveryProbe);
    }

    [Fact]
    public void UnsupportedGhostUsernameDoesNotRejectNormalPassword()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        circuit.Failed(Begin(circuit, probe: false), Failure.AuthRejected);
        Assert.False(circuit.IsHeld(Account));
        // One rejected !username is an isolated fault, not a burst: normal logins proceed at once.
        Assert.False(Begin(circuit).RecoveryProbe);
    }

    [Fact]
    public void FaultBurstHasSharedFiveSecondBackoffAndCancellationDoesNot()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        for (var i = 0; i < AuthenticationCircuitBreaker.FaultBurstThreshold; i++)
            circuit.Failed(Begin(circuit), Failure.ConnectFault);
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        clock.Advance(TimeSpan.FromSeconds(5));
        circuit.Failed(Begin(circuit), Failure.CallerAbandoned);
        Assert.False(Begin(circuit).RecoveryProbe);
    }

    [Fact]
    public void IsolatedFaultsDoNotStallOtherPoolsAndSuccessResetsTheBurst()
    {
        var circuit = new AuthenticationCircuitBreaker(new Clock());
        for (var i = 0; i < AuthenticationCircuitBreaker.FaultBurstThreshold - 1; i++)
            circuit.Failed(Begin(circuit), Failure.ConnectFault);
        circuit.Succeeded(Begin(circuit));
        for (var i = 0; i < AuthenticationCircuitBreaker.FaultBurstThreshold - 1; i++)
            circuit.Failed(Begin(circuit), Failure.ConnectFault);
        Assert.False(Begin(circuit).RecoveryProbe); // still admitted: the success reset the burst
    }

    [Fact]
    public void UndeliveredNotificationIsRetriedUntilSubscriberConsumesIt()
    {
        var circuit = new AuthenticationCircuitBreaker();
        Reject(circuit);
        var count = 0;
        circuit.NotificationSink = _ => { count++; return false; };
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        circuit.NotificationSink = _ => { count++; return true; };
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        Assert.Equal(2, count);
    }

    [Fact]
    public void PreviousNotificationCannotMarkNewEpisodeDelivered()
    {
        var circuit = new AuthenticationCircuitBreaker();
        var notifications = 0;
        circuit.NotificationSink = _ =>
        {
            if (++notifications == 1)
            {
                circuit.Succeeded(Begin(circuit, manual: true));
                circuit.NotificationSink = _ => false;
                Reject(circuit);
            }
            return true;
        };
        Reject(circuit);
        circuit.NotificationSink = _ => { notifications++; return true; };
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task GuardBlocksConnectDelegateAndCancelledRecoveryKeepsHold()
    {
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock);
        var connects = 0;
        await Assert.ThrowsAsync<FtpAuthenticationException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "wrong", _ =>
        {
            connects++;
            throw new FtpAuthenticationException("530", "Login incorrect");
        }));
        await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "wrong", _ =>
        {
            connects++;
            return Task.CompletedTask;
        }));
        Assert.Equal(1, connects);
        clock.Advance(TimeSpan.FromMinutes(30));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "wrong", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, cts.Token));
        Assert.True(circuit.IsHeld(Account));
        Assert.Throws<FtpLoginHeldException>(() => Begin(circuit));
    }

    private sealed class StoreFixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "gldrive-auth-test-" + Guid.NewGuid().ToString("N"));
        internal string PathName => Path.Combine(DirectoryPath, "holds.json");
        internal StoreFixture() => Directory.CreateDirectory(DirectoryPath);
        internal AuthHoldStore Store() => new(PathName,
            bytes => bytes.Select(b => (byte)(b ^ 0xa5)).ToArray(),
            bytes => bytes.Select(b => (byte)(b ^ 0xa5)).ToArray());
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Fact]
    public void RestartRetainsDeadlineNotificationAndProtectedPasswordMarker()
    {
        using var fixture = new StoreFixture();
        var clock = new Clock();
        var first = new AuthenticationCircuitBreaker(clock, fixture.Store()) { NotificationSink = _ => true };
        Reject(first);
        var text = File.ReadAllText(fixture.PathName);
        Assert.DoesNotContain(Account.Host, text);
        Assert.DoesNotContain("wrong", text);
        Assert.DoesNotContain(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("wrong"))), text);
        clock.Advance(TimeSpan.FromMinutes(10));
        var notifications = 0;
        var restarted = new AuthenticationCircuitBreaker(clock, fixture.Store()) { NotificationSink = _ => { notifications++; return true; } };
        Assert.Throws<FtpLoginHeldException>(() => Begin(restarted));
        Assert.Equal(0, notifications);
        Assert.Equal(first.RetryAt(Account), restarted.RetryAt(Account));
        Assert.True(Begin(restarted, "fixed").RecoveryProbe);
        var duringProbe = new AuthenticationCircuitBreaker(clock, fixture.Store());
        Assert.Throws<FtpLoginHeldException>(() => Begin(duringProbe, "fixed"));
    }

    [Fact]
    public void UnreadableCredentialMarkerCannotPretendPasswordChanged()
    {
        using var fixture = new StoreFixture();
        var clock = new Clock();
        Reject(new AuthenticationCircuitBreaker(clock, fixture.Store()));
        var unavailable = new AuthHoldStore(fixture.PathName, bytes => bytes, _ => throw new CryptographicException());
        var restarted = new AuthenticationCircuitBreaker(clock, unavailable);
        Assert.Throws<FtpLoginHeldException>(() => Begin(restarted, "different"));
        Assert.True(Begin(restarted, "different", manual: true).RecoveryProbe);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("null")]
    [InlineData("{broken")]
    public void InvalidPersistedSchemaIsQuarantinedAndHoldsNoAccount(string json)
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.PathName, json);
        var circuit = new AuthenticationCircuitBreaker(new Clock(), fixture.Store());
        Assert.False(circuit.IsHeld(Account));
        Assert.False(Begin(circuit).RecoveryProbe);
        // A genuine rejection still re-arms the hold on its first attempt.
        Reject(circuit);
        Assert.True(circuit.IsHeld(Account));
    }

    [Fact]
    public async Task FailedDurableSaveNeverStartsRecoveryNetworkOperation()
    {
        using var fixture = new StoreFixture();
        var clock = new Clock();
        var circuit = new AuthenticationCircuitBreaker(clock, fixture.Store());
        Reject(circuit);
        File.Delete(fixture.PathName);
        Directory.CreateDirectory(fixture.PathName); // deterministic atomic-rename failure, no ACL assumptions
        var connected = false;
        await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(circuit, Account, "fixed", _ =>
        {
            connected = true;
            return Task.CompletedTask;
        }));
        Assert.False(connected);
        Assert.True(circuit.IsHeld(Account));
    }
}
