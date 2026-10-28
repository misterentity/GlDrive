using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class SeriesConfigTests
{
    [Fact]
    public void Old_settings_keep_legacy_modes_and_disable_new_source_choices()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(config.Downloads.RaceCompletedPullsEnabled);
        Assert.False(config.Downloads.PreferFastestWantedSource);
        Assert.Empty(config.Downloads.RacePullServerIds);
        Assert.Equal(SeriesMonitorMode.Legacy, new WishlistItem().MonitorMode);
        Assert.False(new WishlistItem().AutoPullCompletedRaces);
        var before = RuntimeConfigPolicy.Capture(new ServerConfig(), config.Downloads, "", 1);
        config.Downloads.PreferFastestWantedSource = true;
        Assert.Equal(before.Fingerprint, RuntimeConfigPolicy.Capture(new ServerConfig(), config.Downloads, "", 1).Fingerprint);
    }

    [Fact]
    public void Automatic_race_pulls_require_explicit_window_dependency()
    {
        var saved = false;
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => saved = true);
        Assert.Throws<ArgumentException>(() => automation.Apply(new JsonArray(new JsonObject
        { ["op"] = "replace", ["path"] = "/downloads/raceCompletedPullsEnabled", ["value"] = true })));
        Assert.False(saved);
        automation.Apply(new JsonArray(
            new JsonObject { ["op"] = "replace", ["path"] = "/downloads/wantedDecisionWindowEnabled", ["value"] = true },
            new JsonObject { ["op"] = "replace", ["path"] = "/downloads/raceCompletedPullsEnabled", ["value"] = true }));
        Assert.True(saved);
    }

    [Fact]
    public async Task Metadata_is_readonly_and_changing_show_identity_restarts_inventory()
    {
        var service = new AutomationService(new AppConfig(), null!, new NotificationStore(false));
        var store = new WishlistStore();
        var id = Guid.NewGuid().ToString("N");
        async Task Call(object item) => await service.ExecuteAsync("wishlist.upsert", JsonSerializer.SerializeToElement(new { item }), default);
        try
        {
            await Call(new { id, title = "Synthetic Show", type = "TvShow", tvMazeId = 17, monitorMode = "Missing" });
            var item = store.GetById(id)!;
            item.SeriesInventory = [new() { Season = 1, Number = 1 }];
            item.SeriesInventoryLastAttemptAtUtc = DateTimeOffset.UtcNow;
            item.SeriesInventoryUpdatedAtUtc = DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<ArgumentException>(() => Call(new { id, seriesInventory = Array.Empty<object>() }));
            await Call(new { id, tvMazeId = 18 });
            Assert.Empty(store.GetById(id)!.SeriesInventory);
            Assert.Null(store.GetById(id)!.SeriesInventoryLastAttemptAtUtc);
            Assert.Null(store.GetById(id)!.SeriesInventoryUpdatedAtUtc);
            var beforeActivation = DateTimeOffset.UtcNow;
            await Call(new { id, monitorMode = "Future" });
            Assert.True(store.GetById(id)!.MonitorSinceUtc >= beforeActivation);
        }
        finally { store.Remove(id); }
    }
}
