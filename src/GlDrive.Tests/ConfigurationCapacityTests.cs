using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class ConfigurationCapacityTests
{
    private static JsonArray CapacityPatch(int downloads, int lanes) => new(
        new JsonObject { ["op"] = "replace", ["path"] = "/logging/level", ["value"] = "Debug" },
        new JsonObject { ["op"] = "replace", ["path"] = "/downloads/maxConcurrentDownloads", ["value"] = downloads },
        new JsonObject { ["op"] = "replace", ["path"] = "/downloads/multiStream/httpLanesPerNetwork", ["value"] = lanes });

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, 4)]
    [InlineData(6, 4)]
    [InlineData(100, 4)]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 33)]
    [InlineData(1, 64)]
    public void Out_of_range_API_capacity_rejects_the_entire_patch_without_saving(int downloads, int lanes)
    {
        var config = new AppConfig();
        var original = JsonSerializer.Serialize(config);
        var saves = 0;
        var automation = new ConfigurationAutomation(config, _ => saves++);

        Assert.Throws<ArgumentException>(() => automation.Apply(CapacityPatch(downloads, lanes)));

        Assert.Equal(0, saves);
        Assert.Equal(original, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    public void Accepted_API_values_roundtrip_through_settings_without_clamping(int downloads, int lanes)
    {
        var config = new AppConfig();
        AppConfig? saved = null;
        var automation = new ConfigurationAutomation(config, candidate =>
            saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(candidate)));
        automation.Apply(CapacityPatch(downloads, lanes));
        Assert.NotNull(saved);
        Assert.Equal(downloads, saved.Downloads.MaxConcurrentDownloads);
        Assert.Equal(lanes, saved.Downloads.MultiStream.HttpLanesPerNetwork);

        // This production helper is called by ApplyTo before credential persistence.
        // Constructing with readLocalState:false avoids certificate, credential and adapter reads.
        var editor = new SettingsViewModel(saved, readLocalState: false);
        var roundtrip = new AppConfig();
        editor.ApplyDownloadCapacitySettings(roundtrip);
        Assert.Equal(downloads, roundtrip.Downloads.MaxConcurrentDownloads);
        Assert.Equal(lanes, roundtrip.Downloads.MultiStream.HttpLanesPerNetwork);
        ConfigurationAutomation.Validate(roundtrip);
    }

    [Theory]
    [InlineData("0", "0", 1, 1)]
    [InlineData("6", "33", 5, 32)]
    [InlineData("bad", "bad", 1, 4)]
    [InlineData("2147483648", "2147483648", 1, 4)]
    public void Editor_retains_clamp_and_default_behavior_without_touching_agent_settings(
        string downloads, string lanes, int expectedDownloads, int expectedLanes)
    {
        var config = new AppConfig();
        var editor = new SettingsViewModel(config, readLocalState: false)
        { MaxConcurrentDownloads = downloads, HttpLanesPerNetwork = lanes };
        // A publication while the editor is open must remain untouched by this change.
        config.Agent.MaxChangesPerRun = 17;
        config.Agent.DryRunsRemaining = 2;
        config.Spread.SpreadPoolSize = 7;
        var agent = JsonSerializer.Serialize(config.Agent);
        var spread = JsonSerializer.Serialize(config.Spread);

        editor.ApplyDownloadCapacitySettings(config);

        Assert.Equal(expectedDownloads, config.Downloads.MaxConcurrentDownloads);
        Assert.Equal(expectedLanes, config.Downloads.MultiStream.HttpLanesPerNetwork);
        Assert.Equal(agent, JsonSerializer.Serialize(config.Agent));
        Assert.Equal(spread, JsonSerializer.Serialize(config.Spread));
        ConfigurationAutomation.Validate(config);
    }

    [Fact]
    public void Published_schema_reports_the_same_supported_capacity_bounds()
    {
        var result = JsonSerializer.SerializeToNode(new ConfigurationAutomation(new(), _ => { }).Schema())!;
        var downloads = result["schema"]!["properties"]!["downloads"]!["properties"]!;
        var concurrent = downloads["maxConcurrentDownloads"]!;
        var http = downloads["multiStream"]!["properties"]!["httpLanesPerNetwork"]!;
        Assert.Equal(1, concurrent["minimum"]!.GetValue<int>());
        Assert.Equal(5, concurrent["maximum"]!.GetValue<int>());
        Assert.Equal(1, http["minimum"]!.GetValue<int>());
        Assert.Equal(32, http["maximum"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("GlDrive.Downloads.ParsedRelease, GlDrive", typeof(GlDrive.Downloads.ParsedRelease))]
    [InlineData("GlDrive.Downloads.QualityProfile, GlDrive", typeof(GlDrive.Downloads.QualityProfile))]
    [InlineData("GlDrive.Downloads.SceneNameParser, GlDrive", typeof(GlDrive.Downloads.SceneNameParser))]
    [InlineData("GlDrive.Downloads.ExecutableExtensions, GlDrive", typeof(GlDrive.Downloads.ExecutableExtensions))]
    [InlineData("GlDrive.Player.SourceAvailabilityPolicy, GlDrive", typeof(GlDrive.Player.SourceAvailabilityPolicy))]
    public void Existing_assembly_qualified_public_types_resolve_after_extraction(string name, Type expected)
        => Assert.Equal(expected, Type.GetType(name, throwOnError: true));
}
