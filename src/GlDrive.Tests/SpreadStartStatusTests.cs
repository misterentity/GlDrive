using System.Text.Json;
using GlDrive.Spread;
using GlDrive.UI;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadStartStatusTests
{
    [Theory]
    [InlineData(RaceStartOutcome.Started, "Race started", "started")]
    [InlineData(RaceStartOutcome.Queued, "Race queued", "queued")]
    [InlineData(RaceStartOutcome.AlreadyActive, "Race already active", "already_active")]
    [InlineData(RaceStartOutcome.Duplicate, "Race already queued", "duplicate")]
    [InlineData(RaceStartOutcome.Suppressed, "Race suppressed", "suppressed")]
    [InlineData(RaceStartOutcome.Unavailable, "Race not started", "unavailable")]
    public void UI_and_automation_report_the_engine_decision(RaceStartOutcome outcome, string prefix, string state)
    {
        var result = new RaceStartResult(outcome, "video", "Example.Release", QueuePosition: 3,
            SuppressedFor: TimeSpan.FromSeconds(75), Reason: "fixture reason");
        var status = SpreadStartStatus.Format(result, 2);
        Assert.StartsWith(prefix + ": Example.Release", status);
        Assert.Equal(outcome == RaceStartOutcome.Started, status.Contains("scanning"));
        if (outcome is RaceStartOutcome.Queued or RaceStartOutcome.Duplicate) Assert.Contains("position 3", status);
        if (outcome == RaceStartOutcome.Unavailable) Assert.Contains("fixture reason", status);
        var json = JsonSerializer.SerializeToElement(AutomationService.RaceStartResponse(result));
        Assert.Equal(state, json.GetProperty("state").GetString());
        Assert.Equal(3, json.GetProperty("queuePosition").GetInt32());
        Assert.Equal(75, json.GetProperty("retryAfterSeconds").GetInt64());
    }

    [Theory]
    [InlineData(0.1, "1 sec", 1)]
    [InlineData(30, "30 sec", 30)]
    [InlineData(60, "1 min", 60)]
    [InlineData(60.1, "2 min", 61)]
    [InlineData(3600, "60 min", 3600)]
    public void Suppression_duration_rounds_up_to_avoid_claiming_early_expiry(double seconds, string duration, long retrySeconds)
    {
        var result = new RaceStartResult(RaceStartOutcome.Suppressed, "video", "Release",
            SuppressedFor: TimeSpan.FromSeconds(seconds), Reason: "recent failure");
        Assert.Contains("retry in " + duration, SpreadStartStatus.Format(result, 2));
        Assert.Contains("recent failure", SpreadStartStatus.Format(result, 2));
        var json = JsonSerializer.SerializeToElement(AutomationService.RaceStartResponse(result));
        Assert.Equal(retrySeconds, json.GetProperty("retryAfterSeconds").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("queuePosition").ValueKind);
    }

    [Fact]
    public void Missing_retry_time_is_reported_without_inventing_a_deadline()
    {
        var result = new RaceStartResult(RaceStartOutcome.Suppressed, "video", "Release");
        Assert.Contains("retry time unavailable", SpreadStartStatus.Format(result, 2));
        var json = JsonSerializer.SerializeToElement(AutomationService.RaceStartResponse(result));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("retryAfterSeconds").ValueKind);
    }

    [Fact]
    public void A_deferred_duplicate_does_not_invent_an_immediate_queue_position()
    {
        var result = new RaceStartResult(RaceStartOutcome.Duplicate, "video", "Release");
        var status = SpreadStartStatus.Format(result, 2);
        Assert.Contains("already queued", status);
        Assert.Contains("waiting for sites or a deferred retry", status);
        Assert.DoesNotContain("position", status);
    }
}
