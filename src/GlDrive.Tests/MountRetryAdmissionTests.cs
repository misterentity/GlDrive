using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Config;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace GlDrive.Tests;

public class MountRetryAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 7, 15, TimeSpan.Zero);

    [Fact]
    public void ExpiredHealthDeadlineDoesNotSpinAgainstLongerAccountHold()
    {
        var hold = new FtpLoginHeldException("account-wide hold", false, Now.AddSeconds(30).UtcDateTime, true);
        var due = MountRetryPolicy.RetryAt(hold, Now, 2, Now.AddMilliseconds(-3));
        Assert.Equal(Now.AddSeconds(60), due);
        for (var seconds = 1; seconds < 60; seconds++) Assert.True(Now.AddSeconds(seconds) < due);
    }

    [Fact]
    public void AccountHoldLongerThanBackoffIsPreservedThroughWrappedException()
    {
        var hold = new InvalidOperationException("mount failed",
            new FtpLoginHeldException("account-wide hold", false, Now.AddHours(2).UtcDateTime, true));
        Assert.Equal(Now.AddHours(2), MountRetryPolicy.RetryAt(hold, Now, 2, Now.AddSeconds(-1)));
    }

    [Fact]
    public void DeferredTcpProbeBudgetDoesNotPollAnExpiredHealthDeadline()
    {
        var error = new SiteRetryDeferredException("TCP probe budget", Now.AddMinutes(15));
        Assert.Equal(Now.AddMinutes(15), MountRetryPolicy.RetryAt(error, Now, 2, Now));
    }

    [Fact]
    public void LongerPersistedOutageStillWins()
    {
        var error = new FtpLoginHeldException("account-wide hold", false, Now.AddHours(2).UtcDateTime, true);
        Assert.Equal(Now.AddHours(4), MountRetryPolicy.RetryAt(error, Now, 2, Now.AddHours(4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrStaleAdmissionDeadlineFallsBackToBoundedRetry(bool stale)
    {
        var error = new FtpLoginHeldException("temporary admission unavailable", false,
            stale ? Now.AddMinutes(-1).UtcDateTime : null, true);
        Assert.Equal(Now.AddMinutes(5), MountRetryPolicy.RetryAt(error, Now, int.MaxValue, Now.AddSeconds(-1)));
    }

    [Fact]
    public void HealthDisabledStillHonorsAccountAdmission()
    {
        var error = new FtpLoginHeldException("account-wide hold", false, Now.AddHours(2).UtcDateTime, true);
        Assert.Equal(Now.AddHours(2), MountRetryPolicy.RetryAt(error, Now, 2, null));
    }

    [Fact]
    public void AdmissionRetryDoesNotCreateAnotherRemoteFailureObservation()
    {
        var tracker = new SiteHealthTracker(() => null, _ => { });
        tracker.Failed("site", "route", SiteFailureKind.Timeout, Now.AddSeconds(-30));
        var original = tracker.Get("site", "route");
        var error = new FtpLoginHeldException("account-wide hold", false, Now.AddSeconds(30).UtcDateTime, true);
        Assert.True(MountRetryPolicy.RetryAt(error, Now, 2, original.NextRetryUtc) > Now);
        Assert.Equal(original, tracker.Get("site", "route"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackgroundSchedulerRetainsAdmissionDeadlineWithoutInventingOutage(bool healthEnabled)
    {
        var (manager, server) = Manager(healthEnabled);
        var original = manager.GetSiteHealth(server.Id);
        var due = DateTimeOffset.UtcNow.AddHours(2);
        var held = new FtpLoginHeldException("account hold", false, due.UtcDateTime, true);
        Assert.Equal(due, manager.DeferRemount(server, held, 1));
        Assert.Equal(due, manager.RemountRetryNotBefore(server.Id));
        Assert.Equal(original, manager.GetSiteHealth(server.Id));
    }

    [Fact]
    public void BackgroundDeadlineDoesNotFollowAChangedEndpoint()
    {
        var (manager, server) = Manager(true);
        var due = DateTimeOffset.UtcNow.AddHours(2);
        manager.DeferRemount(server, new FtpLoginHeldException("account hold", false, due.UtcDateTime, true), 1);
        server.Connection.Host = "changed.invalid";
        Assert.Null(manager.RemountRetryNotBefore(server.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisablingHealthDropsOnlySupervisorDeadline(bool accountHold)
    {
        var (manager, server) = Manager(true);
        var config = Field<AppConfig>(manager, "_config");
        var tracker = Field<SiteHealthTracker>(manager, "_siteHealth");
        var identity = (string)typeof(ServerManager).GetMethod("EndpointIdentity", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [server])!;
        var now = DateTimeOffset.UtcNow;
        foreach (var minutesAgo in new[] { 105, 75, 60, 0 })
            tracker.Failed(server.Id, identity, SiteFailureKind.Timeout, now.AddMinutes(-minutesAgo));
        var healthDue = now.AddHours(4);
        Assert.Equal(healthDue, manager.GetSiteHealth(server.Id).NextRetryUtc);
        Exception failure = accountHold
            ? new FtpLoginHeldException("account hold", false, now.AddHours(2).UtcDateTime, true)
            : new TimeoutException("connection timed out");
        Assert.Equal(healthDue, manager.DeferRemount(server, failure, 1));
        config.Health.Enabled = false;
        var independentDue = manager.RemountRetryNotBefore(server.Id)!.Value;
        if (accountHold) Assert.Equal(now.AddHours(2), independentDue);
        else Assert.InRange(independentDue - now, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        config.Health.Enabled = true;
        Assert.Equal(healthDue, manager.RemountRetryNotBefore(server.Id));
    }

    private static T Field<T>(ServerManager manager, string name) =>
        (T)typeof(ServerManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    [Theory]
    [InlineData("account", true)]
    [InlineData("authentication", true)]
    public void DisablingHealthSeparatesAdmissionOwners(string owner, bool retained)
    {
        var (manager, server) = Manager(true);
        var now = DateTimeOffset.UtcNow;
        var due = now.AddHours(2);
        Exception error = owner switch
        {
            "tcp-probe" => new SiteRetryDeferredException("probe budget", due),
            "supervisor" => new FtpLoginHeldException("health hold", false, due.UtcDateTime, true, siteHealthHold: true),
            "authentication" => new FtpLoginHeldException("authentication hold", true, due.UtcDateTime),
            _ => new FtpLoginHeldException("account hold", false, due.UtcDateTime, true)
        };
        Assert.Equal(due, manager.DeferRemount(server, new InvalidOperationException("mount failed", error), 1));
        Field<AppConfig>(manager, "_config").Health.Enabled = false;
        var independentDue = manager.RemountRetryNotBefore(server.Id)!.Value;
        if (retained) Assert.Equal(due, independentDue);
        else Assert.InRange(independentDue - now, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        Field<AppConfig>(manager, "_config").Health.Enabled = true;
        Assert.Equal(due, manager.RemountRetryNotBefore(server.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealLoginAdmissionMarksSupervisorSeparatelyFromAccountBudget(bool supervisor)
    {
        var account = new FtpLoginAccount("fixture.invalid", 21, "fixture");
        var governor = new LoginAttemptGovernor();
        var policy = new PoolConfig { LoginRateLimitEnabled = true, MaxLoginAttemptsPerFiveMinutes = 2 };
        if (!supervisor) governor.Begin(account, policy, manualRetry: false, ghost: false);
        var connects = 0;
        var observations = 0;
        Func<FtpLoginAccount, DateTime?>? admission = supervisor ? _ => DateTime.UtcNow.AddHours(2) : null;
        var held = await Assert.ThrowsAsync<FtpLoginHeldException>(() => GuardedFtpLogin.RunAsync(
            new AuthenticationCircuitBreaker(), account, "fixture", _ => { connects++; return Task.CompletedTask; },
            governor: governor, policy: policy, observe: _ => observations++, admissionDeadline: admission));
        Assert.Equal(supervisor, held.SiteHealthHold);
        Assert.True(held.LoginRateHold);
        Assert.Equal(0, connects);
        Assert.Equal(0, observations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupervisorDeadlinesFollowLiveRecoveryAndSettings(bool tcpProbe)
    {
        var (manager, server) = Manager(true);
        var config = Field<AppConfig>(manager, "_config");
        config.Health.TcpProbesEnabled = true;
        var tracker = Field<SiteHealthTracker>(manager, "_siteHealth");
        var identity = Identity(server);
        var now = DateTimeOffset.UtcNow;
        if (tcpProbe)
        {
            tracker.Failed(server.Id, identity, SiteFailureKind.Timeout, now.AddHours(-2));
            Assert.True(tracker.TryReserveProbe(server.Id, identity, now));
        }
        else
            foreach (var minutesAgo in new[] { 105, 75, 60, 0 })
                tracker.Failed(server.Id, identity, SiteFailureKind.Timeout, now.AddMinutes(-minutesAgo));
        var due = tcpProbe ? now.AddMinutes(15) : now.AddHours(4);
        Exception error = tcpProbe
            ? new SiteRetryDeferredException("probe budget", due)
            : new FtpLoginHeldException("supervisor hold", false, due.UtcDateTime, true, siteHealthHold: true);
        Assert.Equal(due, manager.DeferRemount(server, error, 1));
        config.Health.Enabled = false;
        AssertFallback();
        config.Health.Enabled = true;
        Assert.Equal(due, manager.RemountRetryNotBefore(server.Id));
        if (tcpProbe)
        {
            config.Health.TcpProbesEnabled = false;
            AssertFallback();
            config.Health.TcpProbesEnabled = true;
            Assert.Equal(due, manager.RemountRetryNotBefore(server.Id));
        }
        tracker.Recovered(server.Id, identity, now.AddMilliseconds(1));
        AssertFallback();
        tracker.Failed(server.Id, identity, SiteFailureKind.Timeout, now.AddMilliseconds(2));
        AssertFallback(); // A new outage episode must not inherit the old long hold.

        void AssertFallback() => Assert.InRange(manager.RemountRetryNotBefore(server.Id)!.Value - now,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
    }

    [Fact]
    public void SharedAccountSupervisorDeadlineTracksAliasRecovery()
    {
        var (manager, server) = Manager(true);
        var config = Field<AppConfig>(manager, "_config");
        var alias = new ServerConfig { Id = "alias" };
        alias.Connection.Host = server.Connection.Host;
        config.Servers.Add(alias);
        var tracker = Field<SiteHealthTracker>(manager, "_siteHealth");
        var now = DateTimeOffset.UtcNow;
        foreach (var minutesAgo in new[] { 105, 75, 60, 0 })
            tracker.Failed(alias.Id, Identity(alias), SiteFailureKind.Timeout, now.AddMinutes(-minutesAgo));
        Assert.Equal(now.AddHours(4), manager.DeferRemount(server, new TimeoutException("connect failed"), 1));
        tracker.Recovered(alias.Id, Identity(alias), now.AddMilliseconds(1));
        Assert.InRange(manager.RemountRetryNotBefore(server.Id)!.Value - now,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
    }

    private static string Identity(ServerConfig server) => (string)typeof(ServerManager)
        .GetMethod("EndpointIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [server])!;

    private static (ServerManager Manager, ServerConfig Server) Manager(bool healthEnabled)
    {
        // Construct only the scheduling owner: no application state, login boundary,
        // credentials, network connection, or background services are initialized.
        var manager = (ServerManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerManager));
        var server = new ServerConfig { Id = "retry-fixture" };
        server.Connection.Host = "fixture.invalid";
        var config = new AppConfig { Servers = [server] };
        config.Health.Enabled = healthEnabled;
        Set("_config", config);
        Set("_siteHealth", new SiteHealthTracker(() => null, _ => { }));
        Set("_remountRetryAfter", new ConcurrentDictionary<string, (string, DateTimeOffset)>());
        return (manager, server);

        void Set(string field, object value) => typeof(ServerManager)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, value);
    }
}
