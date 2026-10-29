using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Recovery;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class DataStewardshipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stewardship-" + Guid.NewGuid().ToString("N"));
    public DataStewardshipTests() => Directory.CreateDirectory(_root);
    private static byte[] Transform(byte[] input) => input.Select(b => (byte)(b ^ 0x5a)).ToArray();
    private static AppConfig Config() => new()
    {
        ControlApi = new() { Token = "synthetic-token" },
        Servers = [new() { Id = "alpha", Irc = new() { Channels = [new() { Name = "#test", Key = "[cbc:synthetic-key]" }] } }]
    };

    [Fact]
    public void Protected_copy_is_verified_idempotent_and_keeps_downgrade_values()
    {
        var config = Config();
        var serialized = JsonSerializer.Serialize(config);
        var writes = 0;
        var copier = new ConfigSecretCopy(_root, Transform, Transform, (p, text) => { writes++; File.WriteAllText(p, text); });
        Assert.True(copier.CopyAndVerify(config));
        Assert.True(copier.CopyAndVerify(config));
        Assert.Equal(1, writes);
        Assert.Equal(serialized, JsonSerializer.Serialize(config));
        Assert.DoesNotContain("synthetic", File.ReadAllText(Path.Combine(_root, "config-secrets.dpapi")));
        config.ControlApi.Token = "rotated-synthetic-token";
        Assert.True(copier.CopyAndVerify(config));
        Assert.Equal(3, writes); // prior verified generation plus the new copy
        Assert.Equal("rotated-synthetic-token", config.ControlApi.Token);
    }

    [Fact]
    public void Failed_write_or_verification_preserves_sources_and_next_start_retries()
    {
        var config = Config();
        var before = JsonSerializer.Serialize(config);
        Assert.False(new ConfigSecretCopy(_root, Transform, Transform, (_, _) => throw new IOException()).CopyAndVerify(config));
        Assert.False(new ConfigSecretCopy(_root, Transform, _ => throw new CryptographicException()).CopyAndVerify(config));
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.True(new ConfigSecretCopy(_root, Transform, Transform).CopyAndVerify(config));
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Fact]
    public void Corrupt_copy_is_preserved_and_does_not_change_runtime_secret_precedence()
    {
        var path = Path.Combine(_root, "config-secrets.dpapi");
        File.WriteAllText(path, "bad-copy");
        Assert.True(new ConfigSecretCopy(_root, Transform, Transform).CopyAndVerify(Config()));
        Assert.Equal("bad-copy", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Empty_fallback_cannot_replace_existing_recovery_evidence()
    {
        var copier = new ConfigSecretCopy(_root, Transform, Transform);
        Assert.True(copier.CopyAndVerify(Config()));
        var before = File.ReadAllText(Path.Combine(_root, "config-secrets.dpapi"));
        Assert.True(copier.CopyAndVerify(new AppConfig()));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "config-secrets.dpapi")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"guardFeedPreferences\":\"bad\"}")]
    public void Malformed_optional_block_preserves_servers_and_roundtrips_draft(string block)
    {
        var json = "{\"servers\":[{\"id\":\"kept\"}],\"dataStewardship\":" + block + "}";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var config = JsonSerializer.Deserialize<AppConfig>(json, options)!;
        Assert.Equal("kept", Assert.Single(config.Servers).Id);
        Assert.False(config.DataStewardship.GuardFeedPreferences);
        Assert.False(config.DataStewardship.CopyConfigSecrets);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(config, options));
        Assert.Equal(block, result.RootElement.GetProperty("dataStewardship").GetRawText());
    }

    [Fact]
    public void Inventory_is_metadata_only_and_never_reads_disabled_vault()
    {
        var rows = RecoveryInventory.Read(Config(), _root);
        var json = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("synthetic-token", json);
        Assert.DoesNotContain("synthetic-key", json);
        Assert.Contains(rows, row => row.Item == "Control API token" && row.Presence == "Present" && row.ObservedAtUtc == null);
        Assert.Contains(rows, row => row.Kind == "Credential" && row.Presence == "Unavailable");
    }

    [Fact]
    public void Malformed_nested_settings_report_unknown_inventory_without_changing_the_profile()
    {
        var config = Config();
        config.Servers[0].Connection = null!;
        var before = JsonSerializer.Serialize(config);
        var row = Assert.Single(RecoveryInventory.Read(config, _root));
        Assert.Equal("Unavailable", row.Presence);
        Assert.Equal("Inventory", row.Kind);
        Assert.Contains("offline import remains available", row.Note);
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(row));
    }

    [Fact]
    public void Automation_rejects_new_malformed_switch_but_preserves_existing_draft_on_unrelated_edit()
    {
        var config = Config();
        var automation = new ConfigurationAutomation(config, _ => { });
        Assert.Throws<ArgumentException>(() => automation.Apply(JsonNode.Parse("""
            [{"op":"replace","path":"/dataStewardship/guardFeedPreferences","value":"invalid"}]
            """)!.AsArray()));
        config.DataStewardship = JsonSerializer.Deserialize<DataStewardshipConfig>("{\"guardFeedPreferences\":\"invalid\"}")!;
        automation.Apply(JsonNode.Parse("""
            [{"op":"replace","path":"/logging/level","value":"Debug"}]
            """)!.AsArray());
        Assert.Equal("Debug", config.Logging.Level);
        Assert.NotNull(config.DataStewardship.UnparsedJson);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
