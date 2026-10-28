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
