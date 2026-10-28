using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.AiAgent;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class ConfigChangeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-config-changes-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppConfig _live = new();
    private AppConfig _saved;
    private readonly ConfigurationAutomation _configuration;
    private readonly List<string> _actors = [];
    private bool _idle = true;
    private bool _failWrite;
    private bool _failConfigSave;

    public ConfigChangeServiceTests()
    {
        Directory.CreateDirectory(_root);
        _live.Servers.Add(new ServerConfig { Id = "alpha", Name = "Alpha" });
        _live.Servers.Add(new ServerConfig { Id = "beta", Name = "Beta" });
        _saved = Clone(_live);
        _configuration = new ConfigurationAutomation(_live, candidate =>
        {
            if (_failConfigSave) throw new IOException("Injected config save failure");
            _actors.Add(ConfigWriteScope.Current.Actor);
            _saved = Clone(candidate);
        }, () => _saved);
    }

    private static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
    private ConfigChangeService Service() => new(_live, _configuration, _ => _idle, _root,
        Path.Combine(_root, "appsettings.json"), s => s, s => s,
        (path, text) => { if (_failWrite) throw new IOException("Injected"); File.WriteAllText(path, text); },
        _ => new ConfigExperimentMetrics(10, 8, 9, true));
    private void Edit(string pointer, JsonNode? value) => _configuration.Apply(
        [new JsonObject { ["op"] = "replace", ["path"] = pointer, ["value"] = value }]);
    private ConfigLease Lease(ConfigChangeService service, bool idle = false) => service.CreateLease(
        "/downloads/multiStream/slowMode", JsonValue.Create(true), _now.AddHours(1), "Temporary slow mode", idle, _now);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_hook_draft_does_not_block_unrelated_edit_lease_expiry_or_restore(bool enabled)
    {
        _live.Hooks = new() { QueueCapacity = -1, Commands = [new() { Id = "draft", Enabled = enabled,
            Executable = "relative.bat", Events = [], TimeoutSeconds = -1 }, null!] };
        _saved = Clone(_live);
        var expected = JsonSerializer.Serialize(_live.Hooks);
        Edit("/downloads/maxRetries", JsonValue.Create(7));
        Assert.Equal(7, _live.Downloads.MaxRetries);
        var service = Service();
        Lease(service);
        service.Reconcile(_now.AddHours(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("reverted", Assert.Single(service.Leases()).Status);
        var snapshot = _configuration.CurrentSnapshot();
        snapshot["downloads"]!["maxRetries"] = 4;
        snapshot["hooks"] = new JsonObject();
        service.Restore(snapshot, "restore:test");
        Assert.Equal(4, _live.Downloads.MaxRetries);
        Assert.Equal(expected, JsonSerializer.Serialize(_live.Hooks));
        Assert.Throws<ArgumentException>(() => Edit("/hooks", new JsonObject()));
        Assert.Throws<ArgumentException>(() => service.CreateLease("/hooks/queueCapacity", JsonValue.Create(8), _now.AddHours(1), "Trial", nowOverride: _now));
    }

    [Fact]
    public void Active_lease_survives_restart_and_expiry_uses_attributed_save_path()
    {
        var first = Service();
        var row = Lease(first);
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        var restarted = Service();
        restarted.Reconcile(_now.AddHours(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("reverted", Assert.Single(restarted.Leases()).Status);
        Assert.Equal(new[] { "lease:" + row.Id, "lease-revert:" + row.Id }, _actors);
    }

    [Fact]
    public void Later_user_edit_wins_over_expiry()
    {
        var service = Service();
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "Trial", nowOverride: _now);
        Edit("/downloads/maxConcurrentDownloads", JsonValue.Create(3));
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(3, _live.Downloads.MaxConcurrentDownloads);
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Observed_edit_away_and_back_cannot_revive_active_lease_ownership()
    {
        var service = Service();
        Lease(service);
        Edit("/downloads/multiStream/slowMode", JsonValue.Create(false));
        service.Reconcile(_now.AddMinutes(10));
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
        Edit("/downloads/multiStream/slowMode", JsonValue.Create(true));
        Service().Reconcile(_now.AddHours(2));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("superseded", Assert.Single(Service().Leases()).Status);
    }

    [Fact]
    public void Failed_immediate_apply_is_terminal_and_never_retried_after_restart()
    {
        var service = Service();
        _failConfigSave = true;
        Assert.Throws<IOException>(() => Lease(service));
        Assert.Equal("failed", Assert.Single(service.Leases()).Status);
        _failConfigSave = false;
        service.Reconcile(_now.AddMinutes(1));
        Service().Reconcile(_now.AddMinutes(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Assert.Empty(_actors);
        Assert.Equal("failed", Assert.Single(Service().Leases()).Status);
        Assert.Equal("active", Lease(service).Status);
    }

    [Theory]
    [InlineData("/library/enabled")]
    [InlineData("/library/dryRun")]
    public void Library_safety_controls_cannot_be_leased(string pointer)
    {
        var service = Service();
        Assert.Throws<ArgumentException>(() => service.CreateLease(pointer,
            JsonValue.Create(pointer.EndsWith("enabled")), _now.AddHours(1), "Trial", nowOverride: _now));
        Assert.Empty(service.Leases());
        Assert.Empty(_actors);
    }

    [Theory]
    [InlineData(-7, 3)]
    [InlineData(14, 20)]
    public void Experiment_metrics_include_local_date_files_across_utc_midnight(int offsetHours, int hour)
    {
        var now = new DateTime(2026, 10, 2, hour, 0, 0, DateTimeKind.Utc);
        var inside = offsetHours < 0 ? now.AddHours(-23) : now.AddMinutes(-1);
        var outside = offsetHours < 0 ? now.AddHours(-25) : now.AddHours(1);
        foreach (var at in new[] { inside, outside })
        {
            var row = new RaceOutcomeEvent { Ts = at.ToString("O"), Result = "complete",
                Participants = [new("destination", "dst", 100, 1, 1, null)] };
            File.AppendAllText(Path.Combine(_root, $"races-{at.AddHours(offsetHours):yyyyMMdd}.jsonl"),
                JsonSerializer.Serialize(row) + Environment.NewLine);
        }
        Assert.NotEqual(inside.Date, inside.AddHours(offsetHours).Date);
        var metrics = ConfigChangeService.ReadMetrics(_root, now);
        Assert.Equal(new ConfigExperimentMetrics(1, 1, 1, true), metrics);
    }

    [Fact]
    public void Idle_deferred_lease_does_not_claim_a_later_matching_user_edit()
    {
        _idle = false;
        var service = Service();
        Lease(service, idle: true);
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Edit("/downloads/multiStream/slowMode", JsonValue.Create(true));
        _idle = true;
        service.Reconcile(_now.AddHours(2));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Explicit_idle_option_defers_both_apply_and_expiry()
    {
        _idle = false;
        var service = Service();
        Lease(service, idle: true);
        Assert.Equal("pending", Assert.Single(service.Leases()).Status);
        _idle = true;
        service.Reconcile(_now.AddMinutes(5));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        _idle = false;
        service.Reconcile(_now.AddHours(2));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        _idle = true;
        service.Reconcile(_now.AddHours(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
    }

    [Fact]
    public void Ordinary_lease_expires_even_while_races_are_busy()
    {
        _idle = false;
        var service = Service();
        Lease(service);
        service.Reconcile(_now.AddHours(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
    }

    [Fact]
    public void Deferred_lease_that_expired_before_idle_never_applies()
    {
        _idle = false;
        var service = Service();
        Lease(service, idle: true);
        service.Reconcile(_now.AddHours(2));
        _idle = true;
        service.Reconcile(_now.AddHours(3));
        Assert.Empty(_actors);
        Assert.Equal("expired-before-apply", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Server_reorder_preserves_identity_at_revert()
    {
        var service = Service();
        var original = _live.Servers[0].SpeedLimitKbps;
        service.CreateLease("/servers/0/speedLimitKbps", JsonValue.Create(42), _now.AddHours(1), "Trial", nowOverride: _now);
        var reordered = JsonSerializer.SerializeToNode(_saved.Servers.AsEnumerable().Reverse(), ConfigurationAutomation.Json);
        Edit("/servers", reordered);
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(original, _live.Servers.Single(s => s.Id == "alpha").SpeedLimitKbps);
        Assert.Equal("beta", _live.Servers[0].Id);
        Assert.Equal("reverted", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Deleted_server_cannot_revert_replacement_at_same_index()
    {
        var service = Service();
        service.CreateLease("/servers/0/speedLimitKbps", JsonValue.Create(42), _now.AddHours(1), "Trial", nowOverride: _now);
        Edit("/servers/0/id", JsonValue.Create("replacement"));
        service.Reconcile(_now.AddHours(2));
        Assert.Equal(42, _live.Servers[0].SpeedLimitKbps);
        Assert.Equal("superseded", Assert.Single(service.Leases()).Status);
    }

    [Theory]
    [InlineData("/controlApi/token")]
    [InlineData("/servers/0/id")]
    [InlineData("/servers/0/irc/channels/0/name")]
    [InlineData("/downloads")]
    public void Unsafe_or_unstable_pointers_never_create_intent(string pointer)
    {
        var service = Service();
        Assert.ThrowsAny<ArgumentException>(() => service.CreateLease(pointer, JsonValue.Create("x"), _now.AddHours(1), "Trial", nowOverride: _now));
        Assert.Empty(service.Leases());
        Assert.Empty(_actors);
    }

    [Fact]
    public void Invalid_replacement_is_rejected_before_pending_intent_is_written()
    {
        var service = Service();
        Assert.ThrowsAny<ArgumentException>(() => service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(-1), _now.AddHours(1), "Trial", nowOverride: _now));
        Assert.Empty(service.Leases());
        Assert.False(File.Exists(Path.Combine(_root, "config-changes.json")));
    }

    [Fact]
    public void Overlapping_leases_cannot_restore_an_expired_intermediate_value()
    {
        var service = Service();
        service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(2), _now.AddHours(1), "First", nowOverride: _now);
        Assert.Throws<ArgumentException>(() => service.CreateLease("/downloads/maxConcurrentDownloads", JsonValue.Create(3), _now.AddHours(2), "Second", nowOverride: _now));
        Assert.Single(service.Leases());
    }

    [Fact]
    public void Unwritable_bookkeeping_keeps_lease_in_memory_and_allows_expiry()
    {
        _failWrite = true;
        var service = Service();
        Lease(service);
        Assert.False(service.PersistenceHealthy);
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        service.Reconcile(_now.AddHours(2));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("reverted", Assert.Single(service.Leases()).Status);
    }

    [Fact]
    public void Corrupt_store_is_copied_then_new_tracking_continues()
    {
        var path = Path.Combine(_root, "config-changes.json");
        File.WriteAllText(path, "invalid");
        var service = Service();
        Assert.Equal("invalid", File.ReadAllText(path + ".corrupt"));
        Assert.Empty(service.Leases());
        Lease(service);
        Assert.True(_live.Downloads.MultiStream.SlowMode);
    }

    [Fact]
    public void Keep_stops_expiry_and_persists_across_restart()
    {
        var service = Service();
        var row = Lease(service);
        service.KeepLease(row.Id);
        Service().Reconcile(_now.AddHours(2));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
    }

    [Fact]
    public void Experiment_keep_and_revert_capture_metrics_and_use_normal_save()
    {
        var service = Service();
        var experiment = service.StartExperiment("Slow", "Reduce local load", _now);
        Assert.Empty(experiment.Snapshot);
        Edit("/downloads/multiStream/slowMode", JsonValue.Create(true));
        var result = service.EndExperiment(experiment.Id, revert: true, _now.AddHours(1));
        Assert.False(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal(result.Before, result.After);
        Assert.Equal("experiment-revert:" + experiment.Id, _actors.Last());
        Assert.Equal("reverted", result.Status);
    }

    [Fact]
    public void Restore_retains_control_token_and_supports_optional_idle_precondition()
    {
        var service = Service();
        var snapshot = _configuration.CurrentSnapshot();
        snapshot["controlApi"]!["token"] = "old-token";
        snapshot["downloads"]!["multiStream"]!["slowMode"] = true;
        _idle = false;
        Assert.Throws<InvalidOperationException>(() => service.Restore(snapshot, "restore:test", requireIdle: true));
        service.Restore(snapshot, "restore:test");
        Assert.Equal("", _live.ControlApi.Token);
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("restore:test", _actors.Last());
    }

    [Fact]
    public void Tuning_agent_cannot_create_a_lease()
    {
        using var scope = ConfigWriteScope.Begin("agent:synthetic-run");
        Assert.Throws<InvalidOperationException>(() => Lease(Service()));
        Assert.Empty(_actors);
    }

    [Fact]
    public void Actor_scope_restores_outer_context()
    {
        using var outer = ConfigWriteScope.Begin("settings-ui");
        using (ConfigWriteScope.Begin("restore:test")) Assert.Equal("restore:test", ConfigWriteScope.Current.Actor);
        using (ConfigWriteScope.Fallback("application")) Assert.Equal("settings-ui", ConfigWriteScope.Current.Actor);
        Assert.Equal("settings-ui", ConfigWriteScope.Current.Actor);
    }

    [Fact]
    public void History_defaults_and_retention_validation_are_exposed_by_schema()
    {
        var schema = JsonSerializer.SerializeToNode(_configuration.Schema())!;
        Assert.False(schema["defaults"]!["configHistory"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(512, schema["defaults"]!["configHistory"]!["retentionCount"]!.GetValue<int>());
        Assert.Equal(1, schema["schema"]!["properties"]!["configHistory"]!["properties"]!["retentionCount"]!["minimum"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => Edit("/configHistory/retentionCount", JsonValue.Create(0)));
        Assert.Empty(_actors);
    }

    [Fact]
    public void Impact_preview_classifies_remount_without_saving()
    {
        var result = JsonSerializer.SerializeToNode(_configuration.Preview(
            [new JsonObject { ["op"] = "replace", ["path"] = "/servers/0/pool/poolSize", ["value"] = _saved.Servers[0].Pool.PoolSize + 1 }]))!;
        Assert.True(result["servers"]![0]!["remount"]!.GetValue<bool>());
        Assert.Empty(_actors);
        Assert.Equal(_saved.Servers[0].Pool.PoolSize, _live.Servers[0].Pool.PoolSize);
    }

    [Fact]
    public void Kept_experiment_records_outcome_without_reverting()
    {
        var service = Service();
        var row = service.StartExperiment("Trial", "An observed hypothesis", _now);
        Edit("/downloads/multiStream/slowMode", JsonValue.Create(true));
        var result = Service().EndExperiment(row.Id, revert: false, _now.AddHours(1));
        Assert.True(_live.Downloads.MultiStream.SlowMode);
        Assert.Equal("kept", result.Status);
        Assert.NotNull(result.After);
        Assert.Single(_actors);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
