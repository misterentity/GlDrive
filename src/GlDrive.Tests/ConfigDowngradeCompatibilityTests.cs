using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Config downgrade", DisableParallelization = true)]
public sealed class ConfigDowngradeCollection;

[Collection("Config downgrade")]
public sealed class ConfigDowngradeCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-downgrade-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot = ConfigManager.AppDataPathOverride;
    private readonly bool _priorReadOnly = ConfigManager.ReadOnly;

    public ConfigDowngradeCompatibilityTests()
    {
        Directory.CreateDirectory(_root);
        ConfigManager.AppDataPathOverride = _root;
        ConfigManager.ReadOnly = false;
    }

    // These are exactly the AppConfig/ControlApiConfig members added since 3.18.1.
    // Removing them from metadata reproduces that release's strict deserializer,
    // while retaining every other real config type instead of a permissive test DTO.
    private static readonly JsonSerializerOptions PreviousReleaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                var removed = info.Type == typeof(AppConfig) ? new[] { "dataStewardship", "recovery" } :
                    info.Type == typeof(ControlApiConfig) ? new[] { "security" } : [];
                foreach (var property in info.Properties.Where(p => removed.Contains(p.Name)).ToArray())
                    info.Properties.Remove(property);
            } }
        }
    };

    private static AppConfig PreviousRead() => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigManager.ConfigPath), PreviousReleaseJson)!;
    private static JsonArray Edit(string value) => [new JsonObject { ["op"] = "replace", ["path"] = "/logging/level", ["value"] = value }];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_saves_remain_readable_by_previous_MCP_config_tools(bool rawSave)
    {
        var config = new AppConfig { ControlApi = new() { Enabled = true, Token = "synthetic-owner-token-for-downgrade-tests-1234567890" } };
        var currentJson = JsonSerializer.Serialize(config, ConfigurationAutomation.Json);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AppConfig>(currentJson, PreviousReleaseJson));
        if (rawSave) ConfigManager.SaveRawSettings(JsonNode.Parse(currentJson)!.AsObject(), "settings-ui");
        else ConfigManager.Save(config);
        Assert.Equal("synthetic-owner-token-for-downgrade-tests-1234567890", PreviousRead().ControlApi.Token);
        var saved = JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!;
        Assert.Null(saved["dataStewardship"]); Assert.Null(saved["recovery"]); Assert.Null(saved["controlApi"]!["security"]);

        var automation = new ConfigurationAutomation(config, ConfigManager.Save, PreviousRead);
        Assert.NotNull(automation.Get());
        Assert.NotNull(automation.Preview(Edit("Debug")));
        automation.Apply(Edit("Debug"));
        Assert.Equal("Debug", PreviousRead().Logging.Level);
        using var history = new ConfigChangeService(config, automation, _ => true, _root);
        var snapshot = JsonSerializer.SerializeToNode(PreviousRead(), PreviousReleaseJson)!;
        snapshot["logging"]!["level"] = "Warning";
        history.Restore(snapshot, "restore:history");
        Assert.Equal("Warning", PreviousRead().Logging.Level);
        Assert.Equal("synthetic-owner-token-for-downgrade-tests-1234567890", PreviousRead().ControlApi.Token);
    }

    [Fact]
    public void Current_MCP_can_edit_omitted_sections_and_nondefaults_roundtrip()
    {
        var config = new AppConfig();
        ConfigManager.Save(config);
        var automation = new ConfigurationAutomation(config);
        Assert.NotNull(automation.Get("/recovery/autoInstallUpdates"));
        automation.Apply(JsonNode.Parse("""
            [{"op":"replace","path":"/recovery/autoInstallUpdates","value":false},
             {"op":"replace","path":"/dataStewardship/guardFeedPreferences","value":true}]
            """)!.AsArray());
        config.ControlApi.Security.RequireLegacyApproval = true;
        ConfigManager.Save(config);
        var reloaded = ConfigManager.Load();
        Assert.False(reloaded.Recovery.AutoInstallUpdates);
        Assert.True(reloaded.DataStewardship.GuardFeedPreferences);
        Assert.True(reloaded.ControlApi.Security.RequireLegacyApproval);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!["controlApi"]!["security"]);
    }

    [Theory]
    [InlineData("dataStewardship", "{\"copyConfigSecrets\":\"draft\"}")]
    [InlineData("recovery", "{\"autoInstallUpdates\":\"draft\"}")]
    public void Malformed_optional_draft_is_preserved_during_default_omission(string section, string draft)
    {
        var raw = JsonSerializer.SerializeToNode(new AppConfig(), ConfigurationAutomation.Json)!.AsObject();
        raw[section] = JsonNode.Parse(draft);
        ConfigManager.SaveRawSettings(raw, "settings-ui");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(draft), JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))![section]));
    }

    [Fact]
    public void Omitted_defaults_do_not_supersede_a_pending_lease_on_an_unrelated_save()
    {
        var config = new AppConfig();
        ConfigManager.Save(config);
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var idle = false;
        var automation = new ConfigurationAutomation(config);
        using var changes = new ConfigChangeService(config, automation, _ => idle, _root);
        changes.CreateLease("/recovery/autoInstallUpdates", JsonValue.Create(false), now.AddHours(1), "Update trial", true, now);
        automation.Apply(Edit("Debug"));
        Assert.Equal("pending", Assert.Single(changes.Leases()).Status);
        idle = true;
        changes.Reconcile(now.AddMinutes(1));
        Assert.False(ConfigManager.Load().Recovery.AutoInstallUpdates);
        changes.Reconcile(now.AddHours(2));
        Assert.Equal("reverted", Assert.Single(changes.Leases()).Status);
        Assert.True(PreviousRead().Recovery.AutoInstallUpdates);
    }

    [Fact]
    public void Rollback_projection_preserves_exact_original_and_owner_access_and_can_restore_after_failure()
    {
        var config = new AppConfig { ControlApi = new() { Enabled = true, Token = "synthetic-owner-token-for-downgrade-tests-1234567890" } };
        config.Recovery.AutoInstallUpdates = false;
        config.DataStewardship.GuardFeedPreferences = true;
        config.ControlApi.Security.RequireLegacyApproval = true;
        File.WriteAllText(ConfigManager.ConfigPath, JsonSerializer.Serialize(config, ConfigurationAutomation.Json), Encoding.Unicode);
        var original = File.ReadAllBytes(ConfigManager.ConfigPath);
        var preparation = ConfigDowngradeCompatibility.Prepare(ConfigManager.ConfigPath);
        Assert.Equal(original, File.ReadAllBytes(preparation.BackupPath!));
        Assert.Equal("synthetic-owner-token-for-downgrade-tests-1234567890", PreviousRead().ControlApi.Token);
        Assert.True(PreviousRead().ControlApi.Enabled);
        preparation.Restore();
        Assert.Equal(original, File.ReadAllBytes(ConfigManager.ConfigPath));
        Assert.False(ConfigManager.Load().Recovery.AutoInstallUpdates);
        var retry = ConfigDowngradeCompatibility.Prepare(ConfigManager.ConfigPath);
        Assert.NotEqual(preparation.BackupPath, retry.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(preparation.BackupPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_or_unverified_recovery_copy_never_changes_primary(bool throwOnWrite)
    {
        var original = Encoding.UTF8.GetBytes("{\"recovery\":{\"autoInstallUpdates\":false}}");
        File.WriteAllBytes(ConfigManager.ConfigPath, original);
        Assert.Throws<IOException>(() => ConfigDowngradeCompatibility.Prepare(ConfigManager.ConfigPath, (path, _) =>
        {
            if (throwOnWrite) throw new IOException("Synthetic failure");
            File.WriteAllText(path, "wrong bytes");
        }));
        Assert.Equal(original, File.ReadAllBytes(ConfigManager.ConfigPath));
    }

    [Fact]
    public void Failed_rollback_restoration_never_overwrites_an_intervening_edit()
    {
        File.WriteAllText(ConfigManager.ConfigPath, "{\"recovery\":{\"autoInstallUpdates\":false}}");
        var preparation = ConfigDowngradeCompatibility.Prepare(ConfigManager.ConfigPath);
        const string edited = "{\"logging\":{\"level\":\"Debug\"}}";
        File.WriteAllText(ConfigManager.ConfigPath, edited);
        Assert.Throws<IOException>(() => preparation.Restore());
        Assert.Equal(edited, File.ReadAllText(ConfigManager.ConfigPath));
        Assert.True(File.Exists(preparation.BackupPath));
    }

    public void Dispose()
    {
        ConfigManager.AppDataPathOverride = _priorRoot;
        ConfigManager.ReadOnly = _priorReadOnly;
        Directory.Delete(_root, true);
    }
}
