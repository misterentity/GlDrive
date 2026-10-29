using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Util;

namespace GlDrive.Services;

/// <summary>Publishes settings only after persistence while preserving the latest MCP credential.</summary>
public static class McpSettingsPersistence
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Apply(AppConfig runtimeConfig, bool enabled, int port)
        => Apply(runtimeConfig, enabled, port, ConfigManager.ConfigPath,
            LoadSavedSettings,
            candidate =>
            {
                ConfigManager.SaveRawSettings(candidate, "settings-ui:mcp");
            });

    /// <summary>Replaces only the saved bearer token and publishes it after persistence succeeds.</summary>
    public static void RegenerateToken(AppConfig runtimeConfig)
        => RegenerateToken(runtimeConfig, ConfigManager.ConfigPath, LoadSavedSettings,
            candidate =>
            {
                ConfigManager.SaveRawSettings(candidate, "settings-ui:token");
            });

    internal static void RegenerateToken(AppConfig runtimeConfig, string configPath,
        Func<JsonObject> load, Action<JsonObject> save, Func<bool>? isReadOnly = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(save);
        isReadOnly ??= () => ConfigManager.ReadOnly;
        SecureFile.WithPathLock(configPath, () =>
        {
            EnsureWritable(isReadOnly);
            var candidate = load().DeepClone().AsObject();
            var listener = candidate["controlApi"] as JsonObject;
            if (candidate["controlApi"] != null && listener == null)
                throw new InvalidOperationException("The saved controlApi settings are invalid; token was not changed.");
            if (listener == null) candidate["controlApi"] = listener = new JsonObject();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            listener["token"] = token;
            EnsureWritable(isReadOnly);
            save(candidate);
            // ControlApi reads the shared config token for each request, so this invalidates
            // the old token immediately without restarting the listener or any connections.
            (runtimeConfig.ControlApi ??= new ControlApiConfig()).Token = token;
        });
    }

    /// <summary>Save the full Settings dialog from a detached candidate, retaining the saved bearer token.</summary>
    public static void SaveAll(AppConfig runtimeConfig, Action<AppConfig> applyEdits)
    {
        using var attribution = ConfigWriteScope.Fallback("settings-ui");
        SaveAll(runtimeConfig, applyEdits, ConfigManager.ConfigPath, LoadSavedSettings, ConfigManager.Save);
    }

    internal static void SaveAll(AppConfig runtimeConfig, Action<AppConfig> applyEdits, string configPath,
        Func<JsonObject> load, Action<AppConfig> save, Func<bool>? isReadOnly = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentNullException.ThrowIfNull(applyEdits);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(save);
        isReadOnly ??= () => ConfigManager.ReadOnly;
        SecureFile.WithPathLock(configPath, () =>
        {
            EnsureWritable(isReadOnly);
            var candidate = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(runtimeConfig, Json), Json)!;
            applyEdits(candidate);
            if (candidate.ControlApi == null) throw new InvalidOperationException("MCP settings must not be null.");
            ValidatePort(candidate.ControlApi.Port);
            var saved = load();
            candidate.ControlApi.Token = SavedToken(saved);
            candidate.ControlApi.Security = saved["controlApi"]?["security"]?.Deserialize<AutomationSecurityConfig>(Json) ?? new();
            ControlApi.EnsureToken(candidate);
            EnsureWritable(isReadOnly);
            save(candidate);
            ConfigReconciler.Apply(runtimeConfig, candidate);
        });
    }

    private static JsonObject LoadSavedSettings() => File.Exists(ConfigManager.ConfigPath)
        ? JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath)) as JsonObject
            ?? throw new InvalidOperationException("Application settings must contain a JSON object; settings were not saved.")
        : JsonSerializer.SerializeToNode(new AppConfig(), Json)!.AsObject();

    private static string SavedToken(JsonObject saved)
    {
        var listener = saved["controlApi"];
        if (listener == null) return "";
        if (listener is not JsonObject) throw new InvalidOperationException("The saved controlApi settings are invalid; settings were not saved.");
        return listener["token"]?.GetValue<string>() ?? "";
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "MCP port must be between 1024 and 65535.");
    }

    private static void EnsureWritable(Func<bool> isReadOnly)
    {
        if (isReadOnly()) throw new InvalidOperationException("Application settings are read-only; settings were not saved.");
    }

    // The seam keeps tests entirely inside a temporary directory. Both delegates execute
    // under the same path lock used by ConfigManager.Save and MCP configuration edits.
    internal static void Apply(AppConfig runtimeConfig, bool enabled, int port, string configPath,
        Func<JsonObject> load, Action<JsonObject> save, Func<bool>? isReadOnly = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(save);
        ValidatePort(port);
        isReadOnly ??= () => ConfigManager.ReadOnly;
        SecureFile.WithPathLock(configPath, () =>
        {
            EnsureWritable(isReadOnly);
            // Do not mutate the source or runtime graph until persistence succeeds. Raw
            // JSON also retains unknown/legacy properties that a typed round trip drops.
            var candidate = load().DeepClone().AsObject();
            JsonObject listener;
            if (!candidate.TryGetPropertyValue("controlApi", out var existing) || existing == null)
            {
                listener = new JsonObject();
                candidate["controlApi"] = listener;
            }
            else listener = existing as JsonObject
                ?? throw new InvalidOperationException("The saved controlApi settings are invalid; MCP settings were not saved.");

            var token = listener["token"]?.GetValue<string>() ?? "";
            if (enabled && string.IsNullOrWhiteSpace(token))
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                listener["token"] = token;
            }
            listener["enabled"] = enabled;
            listener["port"] = port;
            save(candidate);

            var runtimeListener = runtimeConfig.ControlApi ??= new ControlApiConfig();
            runtimeListener.Enabled = enabled;
            runtimeListener.Port = port;
            runtimeListener.Token = token;
        });
    }
}
