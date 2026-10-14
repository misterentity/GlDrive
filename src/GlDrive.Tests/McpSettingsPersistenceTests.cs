using System.IO;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class McpSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-mcp-settings-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "appsettings.json");

    public McpSettingsPersistenceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonObject Load() => JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
    private void Save(JsonObject candidate) => SecureFile.WriteAllTextRestricted(ConfigPath, candidate.ToJsonString());
    private void Apply(AppConfig runtime, bool enabled, int port) => McpSettingsPersistence.Apply(runtime, enabled, port, ConfigPath, Load, Save, () => false);

    [Fact]
    public void ApplyPreservesLatestPersistedEditsTokenAndUnsavedRuntimeSettings()
    {
        File.WriteAllText(ConfigPath, """
            {
              "servers": [{ "id": "persisted-site", "name": "Changed by another settings writer" }],
              "downloads": { "maxRetries": 9, "theme": "Persisted theme", "futureOption": true },
              "futureSection": { "keep": [1, 2, 3] },
              "controlApi": { "enabled": false, "port": 8756, "token": "newer-persisted-token", "futureListenerOption": "keep" }
            }
            """);
        var before = Load();
        var runtime = new AppConfig
        {
            Servers = [new() { Id = "unsaved-site", Name = "Unsaved UI server" }],
            ControlApi = new() { Enabled = false, Port = 8756, Token = "stale-runtime-token" }
        };
        runtime.Downloads.MaxRetries = 2;
        runtime.Downloads.Theme = "Unsaved UI theme";
        var listenerReference = runtime.ControlApi;

        Apply(runtime, true, 9123);

        var after = Load();
        Assert.True(JsonNode.DeepEquals(before["servers"], after["servers"]));
        Assert.True(JsonNode.DeepEquals(before["downloads"], after["downloads"]));
        Assert.True(JsonNode.DeepEquals(before["futureSection"], after["futureSection"]));
        Assert.Equal("keep", after["controlApi"]!["futureListenerOption"]!.GetValue<string>());
        Assert.Equal("newer-persisted-token", after["controlApi"]!["token"]!.GetValue<string>());
        Assert.True(after["controlApi"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(9123, after["controlApi"]!["port"]!.GetValue<int>());
        Assert.Same(listenerReference, runtime.ControlApi);
        Assert.True(runtime.ControlApi.Enabled);
        Assert.Equal(9123, runtime.ControlApi.Port);
        Assert.Equal("newer-persisted-token", runtime.ControlApi.Token);
        Assert.Equal("unsaved-site", runtime.Servers.Single().Id);
        Assert.Equal(2, runtime.Downloads.MaxRetries);
        Assert.Equal("Unsaved UI theme", runtime.Downloads.Theme);
    }

    [Fact]
    public void PersistenceFailureCannotMutateRuntimeOrLoadedBaseline()
    {
        var baseline = JsonNode.Parse("""{"controlApi":{"enabled":false,"port":8756,"token":"persisted"}}""")!.AsObject();
        var runtime = new AppConfig { ControlApi = new() { Enabled = false, Port = 8756, Token = "runtime" } };
        var listenerReference = runtime.ControlApi;

        Assert.Throws<IOException>(() => McpSettingsPersistence.Apply(runtime, true, 9123, ConfigPath,
            () => baseline, _ => throw new IOException("Simulated write failure"), () => false));

        Assert.Same(listenerReference, runtime.ControlApi);
        Assert.False(runtime.ControlApi.Enabled);
        Assert.Equal(8756, runtime.ControlApi.Port);
        Assert.Equal("runtime", runtime.ControlApi.Token);
        Assert.False(baseline["controlApi"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(8756, baseline["controlApi"]!["port"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void InvalidPortsRejectBeforeReadingOrWriting(int port)
    {
        var reads = 0;
        var writes = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => McpSettingsPersistence.Apply(new(), true, port, ConfigPath,
            () => { reads++; return new(); }, _ => writes++, () => false));
        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void ValidBoundaryPortsPersistExactly(int port)
    {
        File.WriteAllText(ConfigPath, "{}");
        var runtime = new AppConfig();
        Apply(runtime, false, port);
        Assert.Equal(port, Load()["controlApi"]!["port"]!.GetValue<int>());
        Assert.Equal(port, runtime.ControlApi.Port);
    }

    [Fact]
    public void EnablingGeneratesOneTokenAndDisablingPreservesIt()
    {
        File.WriteAllText(ConfigPath, """{"controlApi":{"enabled":false,"port":8756}}""");
        var runtime = new AppConfig();
        Apply(runtime, true, 8756);
        var token = runtime.ControlApi.Token;
        Assert.Equal(64, token.Length);
        Assert.All(token, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.Equal(token, Load()["controlApi"]!["token"]!.GetValue<string>());
        Apply(runtime, false, 8756);
        Assert.Equal(token, runtime.ControlApi.Token);
        Apply(runtime, true, 8756);
        Assert.Equal(token, runtime.ControlApi.Token);
    }

    [Fact]
    public void DisablingWithNoTokenDoesNotCreateOne()
    {
        File.WriteAllText(ConfigPath, "{}");
        Apply(new(), false, 8756);
        Assert.False(Load()["controlApi"]!.AsObject().ContainsKey("token"));
    }

    [Fact]
    public void ReadOnlyRejectsBeforeLoadingOrSaving()
    {
        var reads = 0;
        var writes = 0;
        Assert.Throws<InvalidOperationException>(() => McpSettingsPersistence.Apply(new(), true, 8756, ConfigPath,
            () => { reads++; return new(); }, _ => writes++, () => true));
        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void CorruptExistingSettingsAreNotReplacedWithDefaults()
    {
        File.WriteAllText(ConfigPath, "{ corrupt JSON");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Apply(new(), true, 8756));
        Assert.Equal("{ corrupt JSON", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void FullSavePreservesLatestTokenAndPublishesAllEditsAfterPersistence()
    {
        File.WriteAllText(ConfigPath, """{"controlApi":{"enabled":false,"port":8756,"token":"latest-persisted-token"}}""");
        var runtime = new AppConfig { ControlApi = new() { Token = "stale-runtime-token" } };
        var listenerReference = runtime.ControlApi;
        var downloadsReference = runtime.Downloads;
        AppConfig? candidateSeenBySave = null;

        McpSettingsPersistence.SaveAll(runtime, candidate =>
        {
            Assert.NotSame(runtime, candidate);
            Assert.NotSame(runtime.ControlApi, candidate.ControlApi);
            Assert.NotSame(runtime.Downloads, candidate.Downloads);
            candidate.ControlApi.Enabled = true;
            candidate.ControlApi.Port = 9123;
            candidate.ControlApi.Token = "token-generated-from-stale-dialog-state";
            candidate.Downloads.MaxRetries = 8;
        }, ConfigPath, Load, candidate =>
        {
            Assert.Equal(3, runtime.Downloads.MaxRetries);
            Assert.False(runtime.ControlApi.Enabled);
            Assert.Equal(8756, runtime.ControlApi.Port);
            Assert.Equal("stale-runtime-token", runtime.ControlApi.Token);
            Assert.Equal("latest-persisted-token", candidate.ControlApi.Token);
            candidateSeenBySave = candidate;
        }, () => false);

        Assert.NotNull(candidateSeenBySave);
        Assert.Same(listenerReference, runtime.ControlApi);
        Assert.Same(downloadsReference, runtime.Downloads);
        Assert.Equal("latest-persisted-token", runtime.ControlApi.Token);
        Assert.Equal(9123, runtime.ControlApi.Port);
        Assert.True(runtime.ControlApi.Enabled);
        Assert.Equal(8, runtime.Downloads.MaxRetries);
    }

    [Fact]
    public void FailedFullSaveLeavesAllRuntimeSettingsUntouched()
    {
        var runtime = new AppConfig
        {
            ControlApi = new() { Token = "runtime-token" },
            Servers = [new() { Id = "site-a", Name = "Before" }]
        };
        var serverReference = runtime.Servers[0];
        var listenerReference = runtime.ControlApi;
        Assert.Throws<IOException>(() => McpSettingsPersistence.SaveAll(runtime, candidate =>
        {
            candidate.ControlApi.Enabled = true;
            candidate.ControlApi.Port = 9123;
            candidate.Downloads.MaxRetries = 8;
            candidate.Servers[0].Name = "Edited";
        }, ConfigPath, () => new(), _ => throw new IOException("Simulated save failure"), () => false));

        Assert.Same(listenerReference, runtime.ControlApi);
        Assert.Same(serverReference, runtime.Servers[0]);
        Assert.False(runtime.ControlApi.Enabled);
        Assert.Equal(8756, runtime.ControlApi.Port);
        Assert.Equal("runtime-token", runtime.ControlApi.Token);
        Assert.Equal(3, runtime.Downloads.MaxRetries);
        Assert.Equal("Before", runtime.Servers[0].Name);
    }

    [Fact]
    public void FullSaveGeneratesTokenWhenEnablingWithNoSavedCredential()
    {
        var runtime = new AppConfig();
        McpSettingsPersistence.SaveAll(runtime, candidate => candidate.ControlApi.Enabled = true,
            ConfigPath, () => new(), candidate => Assert.Equal(64, candidate.ControlApi.Token.Length), () => false);
        Assert.Equal(64, runtime.ControlApi.Token.Length);
        Assert.True(runtime.ControlApi.Enabled);
    }

    [Fact]
    public void FullSaveReadOnlyRejectsBeforeApplyingEdits()
    {
        var edits = 0;
        var writes = 0;
        Assert.Throws<InvalidOperationException>(() => McpSettingsPersistence.SaveAll(new(), _ => edits++,
            ConfigPath, () => new(), _ => writes++, () => true));
        Assert.Equal(0, edits);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void FullSaveInvalidPortDoesNotPersistOrPublishCandidate()
    {
        var runtime = new AppConfig();
        var writes = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => McpSettingsPersistence.SaveAll(runtime,
            candidate => candidate.ControlApi.Port = 80, ConfigPath, () => new(), _ => writes++, () => false));
        Assert.Equal(0, writes);
        Assert.Equal(8756, runtime.ControlApi.Port);
    }
}
