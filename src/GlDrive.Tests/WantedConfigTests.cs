using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class WantedConfigTests
{
    [Fact]
    public void Old_config_preserves_immediate_acquisition_and_new_options_apply_live()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(config.Downloads.WantedDecisionWindowEnabled);
        Assert.False(config.Downloads.WantedIndexSweepsEnabled);
        Assert.Equal(15, config.Downloads.WantedDecisionWindowMinutes);
        var before = RuntimeConfigPolicy.Capture(new ServerConfig(), config.Downloads, "", 1);
        config.Downloads.WantedDecisionWindowEnabled = config.Downloads.WantedIndexSweepsEnabled = true;
        Assert.Equal(before.Fingerprint, RuntimeConfigPolicy.Capture(new ServerConfig(), config.Downloads, "", 1).Fingerprint);
        Assert.Contains("wantedDecisionWindowMinutes", JsonSerializer.Serialize(new ConfigurationAutomation(config, _ => {}).Schema()));
    }

    [Theory]
    [InlineData("wantedDecisionWindowMinutes", 0)]
    [InlineData("wantedDecisionWindowMinutes", 61)]
    [InlineData("wantedCandidateMaxAgeHours", 169)]
    [InlineData("wantedIndexSweepLimit", 1001)]
    public void Invalid_limits_reject_atomic_patch(string name, int value)
    {
        bool saved = false;
        var automation = new ConfigurationAutomation(new AppConfig(), _ => saved = true);
        Assert.Throws<ArgumentException>(() => automation.Apply(new JsonArray(new JsonObject
        { ["op"] = "replace", ["path"] = "/downloads/" + name, ["value"] = value })));
        Assert.False(saved);
    }

    [Fact]
    public void Changes_publish_after_mutation_and_isolate_observer_errors()
    {
        var store = new WishlistStore(() => null, _ => {});
        var calls = 0;
        store.Changed += () => throw new InvalidOperationException();
        store.Changed += () => { _ = store.Items; calls++; };
        var item = new WishlistItem { Title = "Synthetic Show" };
        store.Add(item);
        store.Update(item);
        store.Remove(item.Id);
        store.AddImported([new WishlistItem { Title = "Other Synthetic Show" }]);
        Assert.Equal(4, calls);
    }
}
