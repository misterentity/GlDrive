using GlDrive.Config;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class WaitingLoginBudgetTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan time) { _utc += time; _stamp += time.Ticks; }
    }

    [Fact]
    public void ReadingUnknownAccountsCannotFillTheGovernor()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        for (var i = 0; i < 2048; i++)
        {
            var snapshot = governor.Snapshot(new("ftp.example.invalid", 21, "reader" + i));
            Assert.Equal(0, snapshot.AttemptsInWindow);
            Assert.Null(snapshot.RetryAtUtc);
        }
        var account = new FtpLoginAccount("ftp.example.invalid", 21, "member");
        var admitted = governor.Begin(account, new PoolConfig(), false, false);
        Assert.Equal(account.Key, admitted.Key);
        Assert.Equal(1, governor.Snapshot(account).AttemptsInWindow);
    }

    [Fact]
    public void ReadingAnUnknownAccountAtCapacityIsStillReadOnly()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        for (var i = 0; i < 1024; i++)
            governor.Begin(new("ftp.example.invalid", 21, "member" + i), new PoolConfig(), false, false);
        Assert.Null(governor.Snapshot(new("other.example.invalid", 21, "member")).RetryAtUtc);
    }

    [Fact]
    public void DisplayDeadlineMatchesTheOrdinaryAdmissionDeadlineAfterGhostExpires()
    {
        var clock = new Clock();
        var governor = new LoginAttemptGovernor(clock);
        var account = new FtpLoginAccount("ftp.example.invalid", 21, "member");
        governor.Begin(account, new PoolConfig(), false, true);
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 7; i++) governor.Begin(account, new PoolConfig(), false, false);
        clock.Advance(TimeSpan.FromMinutes(4));
        var snapshot = governor.Snapshot(account);
        var denial = Assert.Throws<FtpLoginHeldException>(() => governor.Begin(account, new PoolConfig(), false, false));
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(1), snapshot.RetryAtUtc);
        Assert.Equal(denial.RetryAtUtc, snapshot.RetryAtUtc);
        Assert.Equal(7, snapshot.AttemptsInWindow);
    }

    [Fact]
    public void DisplayingAHoldDoesNotClearItAndDisabledPolicyDoesNotClaimAHold()
    {
        var governor = new LoginAttemptGovernor(new Clock());
        var account = new FtpLoginAccount("ftp.example.invalid", 21, "member");
        var attempt = governor.Begin(account, new PoolConfig(), false, false);
        governor.Complete(attempt, ConnectFailureClassifier.ConnectFailure.ConnectFault, false, true);
        for (var i = 0; i < 10; i++) Assert.Equal(LoginHoldReason.BncCooldown, governor.Snapshot(account).HoldReason);
        Assert.Throws<FtpLoginHeldException>(() => governor.Begin(account, new PoolConfig(), false, false));
        governor.Begin(account, new PoolConfig { LoginRateLimitEnabled = false }, false, false);
        Assert.Null(governor.Snapshot(account).RetryAtUtc);
        Assert.Equal(LoginHoldReason.None, governor.Snapshot(account).HoldReason);
    }
}
