using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class OutboundPayloadRedactorTests
{
    [Theory]
    [InlineData("/controlApi/token")]
    [InlineData("/servers/0/irc/channels/0/key")]
    [InlineData("/connection/password")]
    public void ConfigSecretsAreClassified(string pointer) => Assert.True(ConfigSecretPointers.IsSecret(pointer));

    [Fact]
    public void StructuralPassMasksSecretsStablyWithoutMutatingConfig()
    {
        var config = JsonNode.Parse("""{"controlApi":{"token":"control-secret"},"servers":[{"irc":{"channels":[{"key":"fish-secret"}]},"password":"ftp-secret"}]}""")!;
        var result = OutboundPayloadRedactor.RedactConfig(config).ToJsonString();
        foreach (var secret in new[] { "control-secret", "fish-secret", "ftp-secret" }) Assert.DoesNotContain(secret, result);
        Assert.Equal(result, OutboundPayloadRedactor.RedactConfig(config).ToJsonString());
        Assert.Contains("control-secret", config.ToJsonString());
        Assert.Contains("sha256:", result);
        var prompt = new AgentPrompt().Compose(new DigestBundle(), "memo", [], JsonNode.Parse(result)!, []);
        var body = OutboundPayloadRedactor.Prepare(JsonSerializer.Serialize(new { messages = new[] { new { content = prompt } } }),
            ["control-secret", "fish-secret", "ftp-secret"]);
        Assert.DoesNotContain("control-secret", body);
        Assert.DoesNotContain("fish-secret", body);
        Assert.DoesNotContain("ftp-secret", body);
    }

    [Theory]
    [InlineData("agent prompt")]
    [InlineData("AI Setup SITE RULES and IRC")]
    public void FinalBodyMasksSecretsInFreeTextAndEmbeddedJson(string source)
    {
        string[] secrets = ["control-secret", "fish-secret", "password-\"\\\n-secret", "credential-manager-secret"];
        var prompt = source + string.Join(" ", secrets) + JsonSerializer.Serialize(new { password = secrets[2] });
        var body = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = prompt } } });
        var result = OutboundPayloadRedactor.Prepare(body, secrets);
        var content = JsonNode.Parse(result)!["messages"]![0]!["content"]!.GetValue<string>();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, result);
            Assert.DoesNotContain(secret, content);
            Assert.DoesNotContain(JsonSerializer.Serialize(secret)[1..^1], content);
        }
    }

    [Fact]
    public void RefusesIfSecretRemainsInAPropertyName()
    {
        Assert.Throws<InvalidOperationException>(() => OutboundPayloadRedactor.Prepare("""{"secret-in-key":"value"}""", ["secret-in-key"]));
    }
}
