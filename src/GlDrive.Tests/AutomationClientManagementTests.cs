using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationClientManagementTests
{
    private static JsonObject Snapshot(AppConfig config) => JsonSerializer.SerializeToNode(config, ConfigurationAutomation.Json)!.AsObject();
    private static AutomationClientConfig Client() => new()
    {
        Name = "Synthetic observer", TokenHash = AutomationAccessController.HashToken("synthetic-scoped-credential"), Scopes = ["Observe"]
    };
    private static string PathName() => Path.Combine(Path.GetTempPath(), "client-management-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Local_add_preserves_saved_legacy_token_and_unrelated_latest_settings()
    {
        var runtime = new AppConfig { ControlApi = new() { Token = "stale-runtime" } };
        var disk = new AppConfig { ControlApi = new() { Token = "current-legacy" }, Logging = new() { Level = "Debug" } };
        JsonObject? saved = null;
        string? actor = null;
        AutomationClientManagement.Edit(runtime, PathName(), () => Snapshot(disk),
            node => { saved = node; actor = ConfigWriteScope.Current.Actor; }, security => security.Clients.Add(Client()));
        Assert.Equal("current-legacy", saved!["controlApi"]!["token"]!.GetValue<string>());
        Assert.Equal("Debug", saved["logging"]!["level"]!.GetValue<string>());
        Assert.Single(runtime.ControlApi.Security.Clients);
        Assert.Equal("ui:automation-security", actor);
        Assert.DoesNotContain("synthetic-scoped-credential", saved.ToJsonString());
    }

    [Fact]
    public void Failed_save_does_not_grant_new_capabilities_or_revoke_existing_client()
    {
        var runtime = new AppConfig();
        runtime.ControlApi.Security.Clients.Add(Client());
        var original = runtime.ControlApi.Security;
        Assert.Throws<IOException>(() => AutomationClientManagement.Edit(runtime, PathName(), () => Snapshot(runtime),
            _ => throw new IOException(), security => security.Clients.Clear()));
        Assert.Same(original, runtime.ControlApi.Security);
        Assert.Single(runtime.ControlApi.Security.Clients);
    }

    [Fact]
    public void Invalid_or_malformed_security_cannot_be_silently_replaced_by_editor()
    {
        var runtime = new AppConfig();
        var snapshot = Snapshot(runtime);
        snapshot["controlApi"]!["security"] = JsonNode.Parse("{\"clients\":\"malformed\"}");
        var writes = 0;
        Assert.Throws<InvalidOperationException>(() => AutomationClientManagement.Edit(runtime, PathName(), () => snapshot,
            _ => writes++, security => security.Clients.Add(Client())));
        Assert.Equal(0, writes);
        Assert.Empty(runtime.ControlApi.Security.Clients);
    }

    [Fact]
    public void Revoke_applies_to_exact_client_identity_only()
    {
        var runtime = new AppConfig();
        var first = Client(); var second = Client();
        second.TokenHash = AutomationAccessController.HashToken("another-synthetic-credential");
        runtime.ControlApi.Security.Clients = [first, second];
        AutomationClientManagement.Edit(runtime, PathName(), () => Snapshot(runtime), _ => { },
            security => security.Clients.RemoveAll(c => c.Id == first.Id));
        Assert.Equal(second.Id, Assert.Single(runtime.ControlApi.Security.Clients).Id);
    }
}
