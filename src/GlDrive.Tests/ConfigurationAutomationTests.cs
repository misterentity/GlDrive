using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class ConfigurationAutomationTests
{
    private static JsonNode Json(object value) => JsonSerializer.SerializeToNode(value)!;
    private static JsonArray Patch(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void ReadsHideNestedCredentialsAndNeverReturnControlToken()
    {
        var config = new AppConfig
        {
            ControlApi = new() { Token = "private-control-token" },
            Servers = [new() { Id = "a", Irc = new() { Channels = [new() { Name = "#test", Key = "private-channel-key" }] } }]
        };
        var automation = new ConfigurationAutomation(config, _ => { });
        var hidden = Json(automation.Get());
        Assert.Equal("[redacted]", hidden["value"]!["servers"]![0]!["irc"]!["channels"]![0]!["key"]!.GetValue<string>());
        Assert.DoesNotContain("private-control-token", hidden.ToJsonString());
        var revealed = Json(automation.Get(includeSecrets: true));
        Assert.Contains("private-channel-key", revealed.ToJsonString());
        Assert.DoesNotContain("private-control-token", revealed.ToJsonString());
        Assert.Equal("[redacted]", Json(automation.Get("/controlApi/token", true))["value"]!.GetValue<string>());
    }

    [Fact]
    public void SuccessfulPatchPersistsCandidateAndPreservesRuntimeReferences()
    {
        var server = new ServerConfig { Id = "site-a" };
        var config = new AppConfig { Servers = [server] };
        var downloads = config.Downloads;
        var pool = server.Pool;
        AppConfig? persisted = null;
        var automation = new ConfigurationAutomation(config, candidate => persisted = candidate);
        var revision = Json(automation.Get())["revision"]!.GetValue<string>();
        var result = Json(automation.Apply(Patch("""
            [
              {"op":"replace","path":"/downloads/maxConcurrentDownloads","value":3},
              {"op":"replace","path":"/servers/0/pool/poolSize","value":4},
              {"op":"add","path":"/servers/-","value":{"id":"site-b","name":"Second site"}}
            ]
            """), revision));
        Assert.NotNull(persisted);
        Assert.NotSame(config, persisted);
        Assert.Same(downloads, config.Downloads);
        Assert.Same(server, config.Servers[0]);
        Assert.Same(pool, server.Pool);
        Assert.Equal(3, config.Downloads.MaxConcurrentDownloads);
        Assert.Equal(4, pool.PoolSize);
        Assert.Equal(2, config.Servers.Count);
        Assert.True(result["restartRequired"]!.GetValue<bool>());
    }

    [Fact]
    public void PersistenceFailureDoesNotMutateLiveSettings()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => throw new IOException("disk unavailable"));
        Assert.Throws<IOException>(() => automation.Apply(Patch("""[{"op":"replace","path":"/downloads/maxRetries","value":8}]""")));
        Assert.Equal(3, config.Downloads.MaxRetries);
    }

    [Theory]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/downloads/maxConcurrentDownloads\",\"value\":0}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/servers\",\"value\":[null]}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/logging\",\"value\":null}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/controlApi/port\",\"value\":70000}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/downloads/speedLimitKbps\",\"value\":-1}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/servers/-\",\"value\":{\"id\":\"a\",\"irc\":{\"announceRules\":[{\"pattern\":\"(\"}]}}}]")]
    public void InvalidConfigurationIsRejectedBeforePersistence(string patch)
    {
        var saves = 0;
        var config = new AppConfig();
        var before = JsonSerializer.Serialize(config);
        var automation = new ConfigurationAutomation(config, _ => saves++);
        Assert.Throws<ArgumentException>(() => automation.Apply(Patch(patch)));
        Assert.Equal(0, saves);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Fact]
    public void UnknownSettingsCannotBeSilentlyDiscarded()
    {
        var saves = 0;
        var automation = new ConfigurationAutomation(new(), _ => saves++);
        Assert.Throws<JsonException>(() => automation.Apply(Patch("""[{"op":"add","path":"/downloads/mispelledSetting","value":123}]""")));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void StaleRevisionAndFailedTestPreventTheEntirePatch()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        var edit = Patch("""[{"op":"replace","path":"/downloads/maxRetries","value":8}]""");
        Assert.Throws<InvalidOperationException>(() => automation.Apply(edit, "stale-revision"));
        Assert.Throws<InvalidOperationException>(() => automation.Apply(Patch("""
            [{"op":"replace","path":"/downloads/maxRetries","value":8},
             {"op":"test","path":"/downloads/maxConcurrentDownloads","value":9}]
            """)));
        Assert.Equal(3, config.Downloads.MaxRetries);
    }

    [Fact]
    public void ExplicitSecretChangesRequireOptInAndRedactedRoundTripsPreserveSecrets()
    {
        var config = new AppConfig { Servers = [new() { Id = "a", Irc = new() { Channels = [new() { Name = "#a", Key = "secret-a" }] } }] };
        var automation = new ConfigurationAutomation(config, _ => { });
        var patch = Patch("""[{"op":"replace","path":"/servers/0/irc/channels/0/key","value":"new-secret"}]""");
        Assert.Throws<ArgumentException>(() => automation.Apply(patch));
        automation.Apply(patch, allowSecrets: true);
        Assert.Equal("new-secret", config.Servers[0].Irc.Channels[0].Key);
        automation.Apply(Patch("""[{"op":"replace","path":"/servers/0/irc/channels/0","value":{"name":"#a","key":"[redacted]","autoJoin":false}}]"""));
        Assert.Equal("new-secret", config.Servers[0].Irc.Channels[0].Key);
        Assert.False(config.Servers[0].Irc.Channels[0].AutoJoin);
    }

    [Fact]
    public void SubtreeSecretChangesCannotBypassOptIn()
    {
        var automation = new ConfigurationAutomation(new(), _ => { });
        Assert.Throws<ArgumentException>(() => automation.Apply(Patch("""
            [{"op":"add","path":"/servers/-","value":{"id":"a","irc":{"channels":[{"name":"#a","key":"hidden"}]}}}]
            """)));
    }

    [Fact]
    public void PointerEscapingSupportsDictionaryKeysAndRejectsInvalidIndexes()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        automation.Apply(Patch("""[{"op":"add","path":"/downloads/categoryPaths/TV~1HD~0archive","value":"D:\\TV"}]"""));
        Assert.Equal("D:\\TV", config.Downloads.CategoryPaths["TV/HD~archive"]);
        Assert.Throws<ArgumentException>(() => automation.Get("/downloads/categoryPaths/bad~2escape"));
        Assert.Throws<ArgumentException>(() => automation.Apply(Patch("""[{"op":"remove","path":"/servers/-1"}]""")));
    }

    [Fact]
    public void SchemaContainsNestedServerPropertiesAndEnumValues()
    {
        var schema = Json(new ConfigurationAutomation(new(), _ => { }).Schema())["schema"]!;
        var serverProperties = schema["properties"]!["servers"]!["items"]!["properties"]!;
        Assert.NotNull(serverProperties["connection"]!["properties"]!["proxy"]!["anyOf"]);
        Assert.NotNull(serverProperties["spreadSite"]!["properties"]!["sectionMappings"]!["items"]!["properties"]!["triggerRegex"]);
        Assert.Contains("1250", serverProperties["spreadSite"]!["properties"]!["priority"]!["enum"]!.ToJsonString());
    }

    [Fact]
    public void ReadsAndEditsUseLatestPersistedSettingsWithoutOverwritingDetachedAgentChanges()
    {
        var runtime = new AppConfig();
        var persisted = new AppConfig();
        var automation = new ConfigurationAutomation(runtime, candidate => persisted = candidate, () => persisted);
        var beforeRevision = Json(automation.Get())["revision"]!.GetValue<string>();
        // Simulate an AI commit which replaced disk settings without updating UI objects.
        persisted = new AppConfig();
        persisted.Downloads.MaxRetries = 9;
        Assert.Equal(9, Json(automation.Get("/downloads/maxRetries"))["value"]!.GetValue<int>());
        var edit = Patch("""[{"op":"replace","path":"/downloads/maxConcurrentDownloads","value":2}]""");
        Assert.Throws<InvalidOperationException>(() => automation.Apply(edit, beforeRevision));
        automation.Apply(edit);
        Assert.Equal(9, persisted.Downloads.MaxRetries);
        Assert.Equal(9, runtime.Downloads.MaxRetries);
        Assert.Equal(2, runtime.Downloads.MaxConcurrentDownloads);
    }
}
