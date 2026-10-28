using System.IO;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Lease save observation", DisableParallelization = true)]
public sealed class LeaseSaveObservationCollection;

[Collection("Lease save observation")]
public sealed class ConfigLeaseSaveObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-lease-observation-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot = ConfigManager.AppDataPathOverride;
    private readonly bool _priorReadOnly = ConfigManager.ReadOnly;
    private readonly DateTime _now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppConfig _live = new();

    public ConfigLeaseSaveObservationTests()
    {
        Directory.CreateDirectory(_root);
        ConfigManager.AppDataPathOverride = _root;
        ConfigManager.ReadOnly = false;
        ConfigManager.Save(_live);
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("raw")]
    [InlineData("typed")]
    public void Rapid_edit_away_and_back_relinquishes_lease_without_timer_tick(string savePath)
    {
        var configuration = new ConfigurationAutomation(_live);
        using var service = new ConfigChangeService(_live, configuration, _ => true, _root);
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", nowOverride: _now);

        if (savePath == "raw")
        {
            var raw = JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!.AsObject();
            raw["downloads"]!["maxConcurrentDownloads"] = 3;
            ConfigManager.SaveRawSettings(raw, "settings-ui");
            raw["downloads"]!["maxConcurrentDownloads"] = 2;
            ConfigManager.SaveRawSettings(raw, "settings-ui");
        }
        else if (savePath == "typed")
        {
            _live.Downloads.MaxConcurrentDownloads = 3;
            ConfigManager.Save(_live, "settings-ui");
            _live.Downloads.MaxConcurrentDownloads = 2;
            ConfigManager.Save(_live, "settings-ui");
        }
        else
        {
            configuration.Apply(Patch(3));
            configuration.Apply(Patch(2));
        }

        // No timer or Reconcile call has observed the intermediate user value.
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!["downloads"]!["maxConcurrentDownloads"]!.GetValue<int>());
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
        service.Dispose();
        using var restarted = new ConfigChangeService(_live, configuration, _ => true, _root);
        restarted.Reconcile(_now.AddHours(3));
        Assert.Equal("superseded", Assert.Single(restarted.Leases()).Status);
        Assert.Equal(2, _live.Downloads.MaxConcurrentDownloads);
    }

    [Fact]
    public void Unrelated_and_same_value_saves_keep_lease_and_own_expiry_reverts_it()
    {
        var configuration = new ConfigurationAutomation(_live);
        using var service = new ConfigChangeService(_live, configuration, _ => true, _root);
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", nowOverride: _now);
        ConfigManager.Save(_live, "settings-ui");
        _live.Downloads.MultiStream.SlowMode = true;
        ConfigManager.Save(_live, "settings-ui");
        Assert.Equal("active", Assert.Single(service.Leases()).Status);
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(1, _live.Downloads.MaxConcurrentDownloads);
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("reverted", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Failed_settings_write_does_not_supersede_the_lease()
    {
        var configuration = new ConfigurationAutomation(_live);
        using var service = new ConfigChangeService(_live, configuration, _ => true, _root);
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", nowOverride: _now);
        _live.Downloads.MaxConcurrentDownloads = 3;
        using (var locked = new FileStream(ConfigManager.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => ConfigManager.Save(_live, "settings-ui"));
        Assert.Equal("active", Assert.Single(service.Leases()).Status);
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(1, _live.Downloads.MaxConcurrentDownloads);
        Assert.Equal("reverted", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Pending_lease_does_not_apply_after_a_manual_edit_away_and_back()
    {
        var idle = false;
        var configuration = new ConfigurationAutomation(_live);
        using var service = new ConfigChangeService(_live, configuration, _ => idle, _root);
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", true, _now);
        configuration.Apply(Patch(3));
        configuration.Apply(Patch(1));
        idle = true;
        service.Reconcile(_now.AddMinutes(10));
        Assert.Equal(1, _live.Downloads.MaxConcurrentDownloads);
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Server_reordering_keeps_identity_owned_lease_but_a_real_edit_supersedes_it()
    {
        _live.Servers.Add(new ServerConfig { Id = "alpha", Name = "Alpha" });
        _live.Servers.Add(new ServerConfig { Id = "beta", Name = "Beta" });
        ConfigManager.Save(_live);
        var configuration = new ConfigurationAutomation(_live);
        using var service = new ConfigChangeService(_live, configuration, _ => true, _root);
        service.CreateLease("/servers/0/speedLimitKbps", JsonValue.Create(42), _now.AddHours(1), "Trial", nowOverride: _now);
        _live.Servers.Reverse();
        ConfigManager.Save(_live, "settings-ui");
        Assert.Equal("active", Assert.Single(service.Leases()).Status);
        _live.Servers[1].SpeedLimitKbps = 43;
        ConfigManager.Save(_live, "settings-ui");
        _live.Servers[1].SpeedLimitKbps = 42;
        ConfigManager.Save(_live, "settings-ui");
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(42, _live.Servers[1].SpeedLimitKbps);
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
    }

    private static JsonArray Patch(int value) =>
        [new JsonObject { ["op"] = "replace", ["path"] = "/downloads/maxConcurrentDownloads", ["value"] = value }];

    [Fact]
    public void Disposed_service_stops_observing_subsequent_settings_saves()
    {
        var configuration = new ConfigurationAutomation(_live);
        var service = new ConfigChangeService(_live, configuration, _ => true, _root);
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", nowOverride: _now);
        service.Dispose();
        service.Dispose();
        configuration.Apply(Patch(3));
        Assert.Equal("active", Assert.Single(service.Leases()).Status);
        using var restarted = new ConfigChangeService(_live, configuration, _ => true, _root);
        restarted.Reconcile(_now.AddHours(2));
        Assert.Equal(3, _live.Downloads.MaxConcurrentDownloads);
        Assert.Equal("superseded", Assert.Single(restarted.Leases()).Status);
    }

    public void Dispose()
    {
        ConfigManager.AppDataPathOverride = _priorRoot;
        ConfigManager.ReadOnly = _priorReadOnly;
        Directory.Delete(_root, true);
    }
}
