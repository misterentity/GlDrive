using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>Local UI writes publish only after a successful attributed settings save.</summary>
internal sealed class AutomationClientManagement(AppConfig runtime)
{
    internal void Edit(Action<AutomationSecurityConfig> edit) => Edit(runtime, ConfigManager.ConfigPath,
        () => JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!.AsObject(),
        json => ConfigManager.SaveRawSettings(json, "ui:automation-security"), edit);

    internal static void Edit(AppConfig runtime, string configPath, Func<JsonObject> load,
        Action<JsonObject> save, Action<AutomationSecurityConfig> edit)
    {
        using var attribution = ConfigWriteScope.Begin("ui:automation-security");
        SecureFile.WithPathLock(configPath, () =>
        {
            if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
            var candidate = load().DeepClone().AsObject();
            candidate["controlApi"] ??= new JsonObject();
            var security = candidate["controlApi"]!["security"]?.Deserialize<AutomationSecurityConfig>(ConfigurationAutomation.Json) ?? new();
            if (security.UnparsedJson != null) throw new InvalidOperationException("Correct the malformed controlApi.security block before editing clients");
            edit(security);
            AutomationSecurityConfig.Validate(security);
            candidate["controlApi"]!["security"] = JsonSerializer.SerializeToNode(security, ConfigurationAutomation.Json);
            save(candidate);
            runtime.ControlApi.Security = security;
        });
    }

    internal string Issue(AutomationClientConfig client)
    {
        var token = NewToken();
        client.TokenHash = AutomationAccessController.HashToken(token);
        Edit(security => security.Clients.Add(client));
        return token;
    }

    internal string Rotate(string id)
    {
        var token = NewToken();
        Edit(security => (security.Clients.SingleOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException("The client no longer exists")).TokenHash = AutomationAccessController.HashToken(token));
        return token;
    }

    internal void Revoke(string id) => Edit(security => security.Clients.RemoveAll(c => c.Id == id));
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
