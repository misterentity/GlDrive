using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Spread;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceQueueConfigTests
{
    [Fact]
    public void Old_config_defaults_to_bounded_queue_ordering_without_changing_race_slots()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(config.Spread.SizeAwareRaceQueue);
        Assert.True(config.Spread.MergeRaceAnnounces);
        Assert.Equal(120, config.Spread.RaceQueueMaxAgeMinutes);
        Assert.Equal(1, config.Spread.MaxConcurrentRaces);
        var schema = JsonSerializer.Serialize(new ConfigurationAutomation(config, _ => { }).Schema());
        Assert.Contains("sizeAwareRaceQueue", schema);
        Assert.Contains("mergeRaceAnnounces", schema);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Invalid_queue_age_is_rejected_before_persistence(int minutes)
    {
        var saved = false;
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => saved = true);
        Assert.Throws<ArgumentException>(() => automation.Apply(new JsonArray(new JsonObject
        {
            ["op"] = "replace", ["path"] = "/spread/raceQueueMaxAgeMinutes", ["value"] = minutes
        })));
        Assert.False(saved);
        Assert.Equal(120, config.Spread.RaceQueueMaxAgeMinutes);
    }

    [Theory]
    [InlineData(RaceStartOutcome.Duplicate)]
    [InlineData(RaceStartOutcome.AlreadyActive)]
    public void Ui_and_mcp_report_actual_announce_merge(RaceStartOutcome outcome)
    {
        var result = new RaceStartResult(outcome, "video", "Example.Release.2026", QueuePosition: 2, HintsMerged: true);
        Assert.Contains("announce hints merged", SpreadStartStatus.Format(result, 2));
        Assert.DoesNotContain("announce hints merged", SpreadStartStatus.Format(result with { HintsMerged = false }, 2));
        var json = JsonSerializer.SerializeToElement(AutomationService.RaceStartResponse(result));
        Assert.True(json.GetProperty("hintsMerged").GetBoolean());
        Assert.Equal(2, json.GetProperty("queuePosition").GetInt32());
    }
}
