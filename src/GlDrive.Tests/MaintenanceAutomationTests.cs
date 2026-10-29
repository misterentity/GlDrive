using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class MaintenanceAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-maintenance-tests-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    public MaintenanceAutomationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private MaintenanceAutomation Create(AppConfig? config = null) => new(config ?? new(), new CertificateManager(Path.Combine(_root, "certificates.json")), _root, _ => { });
    private static async Task<JsonNode> Call(MaintenanceAutomation automation, string name, object args) => JsonSerializer.SerializeToNode(await automation.ExecuteAsync(name, Args(args), default))!;

    [Theory]
    [InlineData("certificates_trust")]
    [InlineData("certificates_remove")]
    [InlineData("certificates_clear")]
    public async Task CertificateMutationIsUnavailableToAutomation(string operation)
    {
        var automation = Create();
        Assert.DoesNotContain(automation.Tools, tool => tool.Name == operation);
        await Assert.ThrowsAsync<ArgumentException>(() => Call(automation, operation,
            new { hostPort = "site.example:21", fingerprint = new string('a', 64) }));
        Assert.False(File.Exists(Path.Combine(_root, "certificates.json")));
    }

    [Fact]
    public async Task CertificatesAndPendingDecisionsRemainReadable()
    {
        var certificates = new CertificateManager(Path.Combine(_root, "certificates.json"));
        certificates.TrustCertificate("site.example:21", new string('A', 64));
        var listed = await Call(Create(), "certificates_list", new { });
        Assert.Equal(new string('A', 64), listed["certificates"]![0]!["fingerprint"]!.GetValue<string>());
        Assert.Empty(listed["pendingDecisions"]!.AsArray());
    }

    [Fact]
    public async Task SnapshotRestorePersistsSettingsPreservesControlIdentityAndCreatesBackup()
    {
        var config = new AppConfig { ControlApi = new() { Token = new string('a', 64) } };
        config.Downloads.MaxRetries = 8;
        var automation = Create(config);
        var name = (await Call(automation, "config_snapshot_create", new { }))["name"]!.GetValue<string>();
        config.ControlApi.Token = new string('b', 64);
        config.Downloads.MaxRetries = 2;
        var restored = await Call(automation, "config_snapshot_restore", new { name });
        Assert.Equal(8, config.Downloads.MaxRetries);
        Assert.Equal(new string('b', 64), config.ControlApi.Token);
        var backup = restored["backup"]!.GetValue<string>();
        var previous = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "ai-data", "ai-snapshots", backup)))!;
        Assert.Equal(2, previous["downloads"]!["maxRetries"]!.GetValue<int>());
        var read = await Call(automation, "config_snapshot_get", new { name, includeSecrets = true });
        Assert.DoesNotContain(new string('a', 64), read.ToJsonString());
        Assert.Equal("[redacted]", read["config"]!["controlApi"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task SnapshotRestoreHonorsRevisionAndCannotReadArbitraryFiles()
    {
        var automation = Create();
        var name = (await Call(automation, "config_snapshot_create", new { }))["name"]!.GetValue<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(automation, "config_snapshot_restore", new { name, expectedRevision = "stale" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(automation, "config_snapshot_get", new { name = "../appsettings.json" }));
        await Call(automation, "config_snapshot_delete", new { name });
        Assert.Empty((await Call(automation, "config_snapshots", new { }))["snapshots"]!.AsArray());
    }
}
