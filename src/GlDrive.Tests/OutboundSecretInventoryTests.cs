using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Alerts;
using Xunit;

namespace GlDrive.Tests;

public sealed class OutboundSecretInventoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-outbound-inventory-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "appsettings.json");

    public OutboundSecretInventoryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ConfigSecretsIncludesNestedKeysTokensPasswordsAndLegacyFields()
    {
        var config = JsonNode.Parse("""
            {"controlApi":{"token":"control-value"},"servers":[{"irc":{"channels":[{"name":"#public","key":"channel-value"}]},"connection":{"password":"password-value"}}],"legacyApiKey":"legacy-value","privateSecret":["nested-one","nested-two"],"emptyKey":""}
            """)!;

        var values = OutboundSecretInventory.ConfigSecrets(config);

        Assert.Equal(6, values.Count);
        foreach (var secret in new[] { "control-value", "channel-value", "password-value", "legacy-value", "nested-one", "nested-two" })
            Assert.Contains(secret, values);
        Assert.DoesNotContain("#public", values);
    }

    [Fact]
    public void Default_config_contributes_no_secret_values()
    {
        Assert.Empty(OutboundSecretInventory.ConfigSecrets(JsonSerializer.SerializeToNode(new AppConfig())!));
    }

    [Fact]
    public void Non_string_scalars_are_not_secrets_but_short_credential_strings_are()
    {
        var values = OutboundSecretInventory.ConfigSecrets(JsonNode.Parse("""
            {"token":64,"password":true,"privateSecret":[false,20,null,"5","ab",{"hooks":"nested-credential"}]}
            """)!);
        Assert.Equal(new[] { "5", "ab", "nested-credential" }, values);
    }

    [Theory]
    [InlineData("agent prompt")]
    [InlineData("AI Setup SITE RULES")]
    public void Hook_configuration_does_not_rewrite_outbound_request_or_webhook_fields(string source)
    {
        var config = new AppConfig { Hooks = new() { QueueCapacity = 64, Commands = [new()
        {
            Id = "copy", Enabled = true, Events = ["download.complete"], Executable = @"C:\Tools\copy.exe",
            Arguments = ["x264", "Completed"], TimeoutSeconds = 5, OutputLimitCharacters = 0
        }] } };
        var node = JsonSerializer.SerializeToNode(config)!;
        var values = OutboundSecretInventory.ConfigSecrets(node);
        Assert.Empty(values);
        var body = JsonSerializer.Serialize(new { messages = new[] { new { content = source + " Movie.2026.x264.H264 64 download.complete Completed" } }, max_tokens = 32000, temperature = 0.2 });
        Assert.Equal(body, OutboundPayloadRedactor.Prepare(body, values));
        Assert.DoesNotContain("copy.exe", OutboundPayloadRedactor.RedactConfig(node).ToJsonString());
        var webhook = AlertPayload.Build(new(AlertKind.Hook, AlertSeverity.Information, "", "")
            { HookEvent = "download.complete", HookStatus = "Completed", Release = "Movie.2026.x264" },
            new() { IncludeReleaseNames = true }, config, values);
        var payload = JsonNode.Parse(webhook)!;
        Assert.Equal("download.complete", payload["event"]!.GetValue<string>());
        Assert.Equal("Movie.2026.x264", payload["release"]!.GetValue<string>());
        Assert.Equal("Completed", payload["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InventoryReadsFishFormatsWithoutChangingFiles(bool encrypted)
    {
        File.WriteAllText(ConfigPath, """{"controlApi":{"token":"control-value"}}""");
        var json = """{"#synthetic":{"key":"fish-value","altKey":"alternate-value","dhSecretHex":"shared-value"}}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        if (encrypted) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var fishPath = Path.Combine(_directory, "fish-keys-synthetic.json");
        File.WriteAllBytes(fishPath, bytes);

        var values = OutboundSecretInventory.Collect(ConfigPath, _directory,
            () => ["credential-value", "control-value"], () => ["memory-value"]);

        Assert.Equal(6, values.Count);
        foreach (var secret in new[] { "control-value", "fish-value", "alternate-value", "shared-value", "credential-value", "memory-value" })
            Assert.Contains(secret, values);
        Assert.Equal(bytes, File.ReadAllBytes(fishPath));
        Assert.Equal("""{"controlApi":{"token":"control-value"}}""", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void FailedCredentialInventoryDoesNotReturnPartialSecrets()
    {
        File.WriteAllText(ConfigPath, """{"controlApi":{"token":"control-value"}}""");
        Assert.Throws<IOException>(() => OutboundSecretInventory.Collect(ConfigPath, _directory,
            () => throw new IOException("Synthetic credential failure")));
    }

    [Fact]
    public void CorruptFishFileDoesNotReturnPartialSecrets()
    {
        File.WriteAllText(ConfigPath, "{}");
        File.WriteAllText(Path.Combine(_directory, "fish-keys-synthetic.json"), "invalid JSON");
        Assert.ThrowsAny<Exception>(() => OutboundSecretInventory.Collect(ConfigPath, _directory, () => []));
    }
}
