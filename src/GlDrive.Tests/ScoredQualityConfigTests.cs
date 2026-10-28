using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class ScoredQualityConfigTests
{
    [Fact]
    public void Old_config_loads_with_scored_decisions_disabled_and_settings_are_live()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"downloads":{"qualityDefault":"2160p"}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(config.Downloads.ScoredQualityEnabled);
        Assert.Empty(config.Downloads.Profiles);
        var server = new ServerConfig();
        var before = RuntimeConfigPolicy.Capture(server, config.Downloads, "", 1);
        config.Downloads.ScoredQualityEnabled = true;
        config.Downloads.Profiles.Add(ScoredQualityProfile.Recommended(QualityProfile.Q2160p));
        Assert.Equal(before.Fingerprint, RuntimeConfigPolicy.Capture(server, config.Downloads, "", 1).Fingerprint);
    }

    [Fact]
    public void Schema_and_atomic_patch_include_profiles()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        var schema = JsonSerializer.Serialize(automation.Schema());
        Assert.Contains("scoredQualityEnabled", schema);
        Assert.Contains("cutoffScore", schema);
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.Id = "movie-hd";
        automation.Apply(new JsonArray(
            new JsonObject { ["op"] = "add", ["path"] = "/downloads/profiles/-", ["value"] = JsonSerializer.SerializeToNode(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)) },
            new JsonObject { ["op"] = "replace", ["path"] = "/downloads/scoredQualityEnabled", ["value"] = true }));
        Assert.True(config.Downloads.ScoredQualityEnabled);
        Assert.Equal("movie-hd", Assert.Single(config.Downloads.Profiles).Id);
    }

    [Theory]
    [InlineData("[{\"id\":\"same\"},{\"id\":\"SAME\"}]")]
    [InlineData("[{\"id\":\"bad\",\"allowedResolutions\":[999]}]")]
    [InlineData("[{\"id\":\"bad\",\"minimumBytes\":200,\"maximumBytes\":100}]")]
    [InlineData("[{\"id\":\"bad\",\"customFormats\":[{\"pattern\":\"(\",\"score\":10}]}]")]
    public void Invalid_profile_is_rejected_before_persistence(string profiles)
    {
        var saved = false;
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => saved = true);
        Assert.Throws<ArgumentException>(() => automation.Apply(new JsonArray(new JsonObject
        {
            ["op"] = "replace", ["path"] = "/downloads/profiles", ["value"] = JsonNode.Parse(profiles)
        })));
        Assert.False(saved);
        Assert.Empty(config.Downloads.Profiles);
    }

    [Fact]
    public async Task Automation_lists_profiles_explains_candidates_and_protects_download_state()
    {
        var config = new AppConfig { Downloads = new() { ScoredQualityEnabled = true } };
        var service = new AutomationService(config, null!, new NotificationStore(persistToDisk: false));
        var id = Guid.NewGuid().ToString("N");
        async Task<JsonElement> Call(string operation, object args) =>
            (JsonElement)(await service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args), default))!;
        try
        {
            var profiles = await Call("wishlist.profiles", new { });
            Assert.True(profiles.GetProperty("enabled").GetBoolean());
            Assert.Equal(10, profiles.GetProperty("builtIn").GetArrayLength());
            await Call("wishlist.upsert", new { item = new { id, title = "Synthetic Movie", year = 2026, profileId = "scored-q1080p" } });
            var verdict = await Call("wishlist.score", new { itemId = id, releaseName = "Synthetic.Movie.2026.1080p.WEB.H264-TEST" });
            Assert.True(verdict.GetProperty("accepted").GetBoolean());
            Assert.True(verdict.GetProperty("meetsCutoff").GetBoolean());
            await Assert.ThrowsAsync<ArgumentException>(() => Call("wishlist.upsert", new { item = new { id, bestOwned = Array.Empty<object>() } }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call("wishlist.upsert", new { item = new { id, profileId = "missing-profile" } }));
        }
        finally { await Call("wishlist.remove", new { id }); }
    }
}
