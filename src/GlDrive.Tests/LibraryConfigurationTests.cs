using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Library configuration persistence", DisableParallelization = true)]
public sealed class LibraryConfigurationCollection;

[Collection("Library configuration persistence")]
public sealed class LibraryConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-library-config-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prior = ConfigManager.AppDataPathOverride;
    private readonly bool _readOnly = ConfigManager.ReadOnly;

    public LibraryConfigurationTests()
    {
        Directory.CreateDirectory(_root);
        ConfigManager.AppDataPathOverride = _root;
        ConfigManager.ReadOnly = false;
    }

    public void Dispose()
    {
        ConfigManager.AppDataPathOverride = _prior;
        ConfigManager.ReadOnly = _readOnly;
        Directory.Delete(_root, true);
    }

    private static JsonObject Read() => JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!.AsObject();
    private static JsonArray EnablePatch() => JsonNode.Parse("""[{"op":"replace","path":"/library/enabled","value":true},{"op":"replace","path":"/library/dryRun","value":false}]""")!.AsArray();

    [Fact]
    public void Defaults_are_disabled_unconfigured_and_dry_run()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}")!.Library;
        Assert.False(config.Enabled);
        Assert.True(config.DryRun);
        Assert.False(config.ScanPlexAfterImport);
        Assert.Empty(config.StagingRoot);
        Assert.Empty(config.MovieRoot);
        Assert.Empty(config.TvRoot);
    }

    [Fact]
    public void Typed_save_forces_dry_run_on_first_enable_then_allows_separate_opt_in()
    {
        var config = new AppConfig { Library = new() { Enabled = true, DryRun = false } };
        ConfigManager.Save(config, "settings-ui");
        Assert.True(config.Library.DryRun);
        Assert.True(Read()["library"]!["dryRun"]!.GetValue<bool>());
        config.Library.DryRun = false;
        ConfigManager.Save(config, "settings-ui");
        Assert.False(Read()["library"]!["dryRun"]!.GetValue<bool>());
        config.Library.Enabled = false;
        ConfigManager.Save(config, "settings-ui");
        config.Library.Enabled = true;
        ConfigManager.Save(config, "settings-ui");
        Assert.True(config.Library.DryRun);
    }

    [Fact]
    public void Raw_save_preserves_unknown_fields_and_first_enable_guard()
    {
        File.WriteAllText(ConfigManager.ConfigPath, "{\"futureSetting\":42}");
        var raw = JsonNode.Parse("""{"futureSetting":42,"library":{"enabled":true,"dryRun":false}}""")!.AsObject();
        ConfigManager.SaveRawSettings(raw, "mcp:settings");
        Assert.Equal(42, Read()["futureSetting"]!.GetValue<int>());
        Assert.True(raw["library"]!["dryRun"]!.GetValue<bool>());
        Assert.True(Read()["library"]!["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public void Patch_result_revision_and_live_state_match_normalized_persistence()
    {
        var config = new AppConfig();
        ConfigManager.Save(config);
        var automation = new ConfigurationAutomation(config);
        var result = JsonSerializer.SerializeToNode(automation.Apply(EnablePatch()))!;
        Assert.True(config.Library.DryRun);
        Assert.True(Read()["library"]!["dryRun"]!.GetValue<bool>());
        Assert.Equal(result["revision"]!.GetValue<string>(),
            JsonSerializer.SerializeToNode(automation.Get())!["revision"]!.GetValue<string>());
    }

    [Fact]
    public void Restore_to_enabled_live_mode_also_starts_dry_run()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        automation.Apply(JsonNode.Parse("""[{"op":"replace","path":"/library","value":{"enabled":true,"dryRun":false}}]""")!.AsArray());
        Assert.True(config.Library.Enabled);
        Assert.True(config.Library.DryRun);
    }

    [Fact]
    public void Failed_save_does_not_change_live_enablement()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => throw new IOException("synthetic disk failure"));
        Assert.Throws<IOException>(() => automation.Apply(EnablePatch()));
        Assert.False(config.Library.Enabled);
        Assert.True(config.Library.DryRun);
    }

    [Fact]
    public void Invalid_negative_hash_threshold_is_rejected()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        Assert.Throws<ArgumentException>(() => automation.Apply(JsonNode.Parse("""[{"op":"replace","path":"/library/hashVerifyMaxBytes","value":-1}]""")!.AsArray()));
    }

    [Fact]
    public void Unknown_prior_state_never_authorizes_a_live_import()
    {
        File.WriteAllText(ConfigManager.ConfigPath, "{torn");
        var config = new AppConfig { Library = new() { Enabled = true, DryRun = false } };
        ConfigManager.Save(config);
        Assert.True(config.Library.DryRun);
    }
}
