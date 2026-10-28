using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceLifetimeConfigTests
{
    [Fact]
    public void Old_camel_case_config_and_history_get_safe_defaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var config = JsonSerializer.Deserialize<AppConfig>("""{"spread":{"hardTimeoutSeconds":1200}}""", options)!;
        Assert.Equal(1200, config.Spread.HardTimeoutSeconds);
        Assert.Equal(90, config.Spread.AbsoluteRaceTimeoutMinutes);
        Assert.Equal(10, config.Spread.PartialRaceDeadlineHours);
        var history = JsonSerializer.Deserialize<RaceHistoryItem>("{}", options)!;
        Assert.Equal("", history.StopReason);
        Assert.Equal(0, history.ResumeCount);
        Assert.Null(history.FirstUploadAtUtc);
    }

    [Theory]
    [InlineData("absoluteRaceTimeoutMinutes", 0)]
    [InlineData("absoluteRaceTimeoutMinutes", 1441)]
    [InlineData("partialRaceDeadlineHours", 0)]
    [InlineData("partialRaceDeadlineHours", 12)]
    public void Invalid_lifetime_setting_rejects_entire_patch(string property, int value)
    {
        var config = new AppConfig();
        var writes = 0;
        var api = new ConfigurationAutomation(config, _ => writes++);
        Assert.Throws<ArgumentException>(() => api.Apply(new JsonArray(new JsonObject
        {
            ["op"] = "replace", ["path"] = "/spread/" + property, ["value"] = value
        })));
        Assert.Equal(0, writes);
        Assert.Equal(90, config.Spread.AbsoluteRaceTimeoutMinutes);
        Assert.Equal(10, config.Spread.PartialRaceDeadlineHours);
    }

    [Fact]
    public void Settings_are_editable_and_schema_exposes_defaults_and_bounds()
    {
        var config = new AppConfig();
        var api = new ConfigurationAutomation(config, _ => { });
        api.Apply(new JsonArray(
            new JsonObject { ["op"] = "replace", ["path"] = "/spread/absoluteRaceTimeoutMinutes", ["value"] = 60 },
            new JsonObject { ["op"] = "replace", ["path"] = "/spread/partialRaceDeadlineHours", ["value"] = 8 }));
        Assert.Equal(60, config.Spread.AbsoluteRaceTimeoutMinutes);
        Assert.Equal(8, config.Spread.PartialRaceDeadlineHours);
        var schema = JsonSerializer.SerializeToNode(api.Schema())!;
        var properties = schema["schema"]!["properties"]!["spread"]!["properties"]!;
        Assert.Equal(1440, properties["absoluteRaceTimeoutMinutes"]!["maximum"]!.GetValue<int>());
        Assert.Equal(11, properties["partialRaceDeadlineHours"]!["maximum"]!.GetValue<int>());
        Assert.Equal(90, schema["defaults"]!["spread"]!["absoluteRaceTimeoutMinutes"]!.GetValue<int>());
        Assert.Equal(10, schema["defaults"]!["spread"]!["partialRaceDeadlineHours"]!.GetValue<int>());
    }
}
