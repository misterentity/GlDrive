using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class MaintenanceHookRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-hook-restore-" + Guid.NewGuid().ToString("N"));

    public MaintenanceHookRestoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyRestoreKeepsCurrentHooksWhileRestoringOrdinarySettings(bool hasCurrentHook, bool malformedHistoricalHooks)
    {
        var config = new AppConfig();
        config.Downloads.MaxRetries = 7;
        config.Hooks = Hooks("historic");
        var automation = new MaintenanceAutomation(config, new CertificateManager(Path.Combine(_root, "certificates.json")),
            _root, _ => { });
        var created = JsonSerializer.SerializeToNode(await automation.ExecuteAsync("config_snapshot_create", Args(new { }), default))!;
        var name = created["name"]!.GetValue<string>();
        if (malformedHistoricalHooks)
        {
            var path = Path.Combine(_root, "ai-data", "ai-snapshots", name);
            var snapshot = JsonNode.Parse(File.ReadAllText(path))!;
            snapshot["hooks"] = "obsolete optional hook representation";
            File.WriteAllText(path, snapshot.ToJsonString());
        }

        config.Downloads.MaxRetries = 2;
        config.Hooks = hasCurrentHook ? Hooks("current") : new();
        var expectedHooks = JsonSerializer.Serialize(config.Hooks);
        var result = await automation.ExecuteAsync("config_snapshot_restore", Args(new { name }), default);

        Assert.NotNull(result);
        Assert.Equal(7, config.Downloads.MaxRetries);
        Assert.Equal(expectedHooks, JsonSerializer.Serialize(config.Hooks));
    }

    private HookConfig Hooks(string id) => new()
    {
        QueueCapacity = 7,
        Commands = [new() { Id = id, Enabled = true, Events = ["download.complete"], Executable = Path.Combine(_root, id + ".exe") }]
    };

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}
