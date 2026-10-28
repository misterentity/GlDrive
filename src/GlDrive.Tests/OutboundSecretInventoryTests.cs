using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
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
