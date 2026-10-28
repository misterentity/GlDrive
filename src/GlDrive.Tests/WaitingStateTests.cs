using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class WaitingStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static SiteHealthSnapshot Health(DateTimeOffset? next = null) => new("site", SiteHealthState.Down,
        SiteFailureKind.Timeout, Now.AddHours(-1), 5, 2, next, null, null, true, "Waiting after timeout");
    private static LoginBudgetSnapshot Budget(DateTimeOffset? next = null, bool enabled = true, LoginHoldReason reason = LoginHoldReason.None)
        => new(enabled, 8, 7, 1, next?.UtcDateTime, reason, true);

    [Fact]
    public void Site_reports_latest_deadline_without_early_check_or_recovery_eta()
    {
        var wait = WaitingState.Site(true, false, true, Now.AddMinutes(30), Health(Now.AddHours(1)), true,
            Budget(Now.AddHours(2), reason: LoginHoldReason.BncCooldown), Now.AddMinutes(5), Now, "slots 1/2");
        Assert.Equal("authentication-hold", wait.ReasonCode);
        Assert.Equal(Now.AddHours(2), wait.NextCheckAtUtc);
        Assert.Null(wait.EstimatedReadyAtUtc);
        Assert.False(wait.CanCheckNow);
        Assert.Contains("slots 1/2", wait.Description);
    }

    [Fact]
    public void Disabled_governors_and_stale_deadlines_do_not_claim_a_hold()
    {
        var wait = WaitingState.Site(true, false, false, Now.AddDays(1), Health(Now.AddDays(1)), false,
            Budget(Now.AddHours(2), false, LoginHoldReason.BncCooldown), Now.AddSeconds(-1), Now, "slots unknown");
        Assert.Equal("site-retry", wait.ReasonCode);
        Assert.Null(wait.NextCheckAtUtc);
    }

    [Fact]
    public void Existing_connection_does_not_hide_new_login_hold()
    {
        var wait = WaitingState.Site(true, true, false, null, Health(), true,
            Budget(Now.AddHours(2), reason: LoginHoldReason.BncCooldown), null, Now, "slots 1/2");
        Assert.Equal("bnc-cooldown", wait.ReasonCode);
        Assert.Contains("existing pooled connections remain usable", wait.LimitingConstraint);
        Assert.False(wait.CanCheckNow);
    }

    [Fact]
    public void Paused_and_disabled_state_require_user_action()
    {
        var paused = DownloadWaiting.Describe(new() { Status = DownloadStatus.Paused });
        Assert.Contains("user action required", paused!.Description);
        var disabled = WaitingState.Site(false, false, false, null, Health(), true, Budget(), null, Now, "slots unknown");
        Assert.Equal("site-disabled", disabled.ReasonCode);
        Assert.Contains("user action required", disabled.Description);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Failed)]
    public void Stale_wait_cannot_replace_current_download_state(DownloadStatus status)
        => Assert.Null(DownloadWaiting.Describe(new() { Status = status, RuntimeWait = new("source-incomplete", "old") }));

    [Fact]
    public void Runtime_deadlines_are_diagnostic_and_not_persisted()
    {
        var item = new DownloadItem { RuntimeWait = new("source-incomplete", "missing files", Now.AddMinutes(1)) };
        Assert.Equal(Now.AddMinutes(1), DownloadWaiting.Describe(item)!.NextCheckAtUtc);
        var restored = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(item))!;
        Assert.Null(restored.RuntimeWait);
        Assert.Null(DownloadWaiting.Describe(restored)!.NextCheckAtUtc);
    }

    [Fact]
    public void Restored_positive_source_evidence_remains_visible_without_invented_deadline()
    {
        var state = DownloadWaiting.Describe(new() { SourceCompletionPending = true });
        Assert.Equal("source-incomplete", state!.ReasonCode);
        Assert.Null(state.NextCheckAtUtc);
        Assert.Null(state.EstimatedReadyAtUtc);
    }

    [Theory]
    [InlineData(true, false, false, "authentication-hold")]
    [InlineData(false, true, false, "login-budget-hold")]
    [InlineData(false, false, true, "site-health-backoff")]
    [InlineData(false, false, false, "download-retry")]
    public void Login_admission_failure_has_a_typed_download_reason(bool authentication, bool rate, bool health, string expected)
        => Assert.Equal(expected, DownloadWaiting.RetryReason(new FtpLoginHeldException("hold", authentication,
            Now.AddMinutes(1).UtcDateTime, rate, health)));

    [Theory]
    [InlineData(1, 1, 1, 60, 60)]
    [InlineData(3, 1, 1, 60, 180)]
    [InlineData(3, 2, 2, 60, 120)]
    public void Queue_eta_uses_capacity_and_observed_duration(int position, int active, int slots, double median, int seconds)
        => Assert.Equal(Now.AddSeconds(seconds), SpreadManager.EstimateQueueReady(Now, position, active, slots, median));

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_invalid_duration_produces_unknown_eta(double? median)
        => Assert.Null(SpreadManager.EstimateQueueReady(Now, 1, 1, 1, median));
}
