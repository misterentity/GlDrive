using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Config;

public class ConfigManager
{
    // Runs under the settings path lock, only after a successful durable save.
    // Consumers must use these snapshots: the caller's live graph may be reconciled later.
    internal static event Action<string, JsonNode?, JsonNode, ConfigWriteContext>? SettingsSaved;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConfigHistoryStore> Histories = new(StringComparer.OrdinalIgnoreCase);
    private static int _historyWarning;
    public static ConfigHistoryStore History => Histories.GetOrAdd(ConfigPath, _ => new ConfigHistoryStore(AppDataPath));
    private static string AppDataFolder => AppDataPath;
    private static string ConfigFilePath => Path.Combine(AppDataPath, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string AppDataPath => AppDataPaths.Root;
    // Set once by the test assembly's module initializer, before any store is constructed.
    internal static string? AppDataPathOverride
    {
        get => AppDataPaths.TestOverride;
        set => AppDataPaths.TestOverride = value;
    }
    public static string ConfigPath => ConfigFilePath;
    public static bool ConfigExists => File.Exists(ConfigFilePath);

    /// <summary>
    /// When true, Save() is a no-op. Set by transient/diagnostic modes that load the
    /// real config, mutate it in memory (e.g. seeding demo data), and must NEVER write
    /// it back — most importantly the --screenshots renderer, which replaces the real
    /// servers with demo placeholders. Without this guard, any incidental Save() on a
    /// background path (a VM constructor, a timer) clobbers the user's real config.
    /// (Root cause of the 2026-06-17 "all servers gone" incident.)
    /// </summary>
    public static bool ReadOnly { get; set; }

    public static AppConfig Load() => Load(out _);

    internal static AppConfig Load(out bool loadedFromFile)
    {
        loadedFromFile = false;
        using var attribution = ConfigWriteScope.Begin("migration");
        if (!File.Exists(ConfigFilePath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(ConfigFilePath);

            // Check if this is the old single-server format (has "connection" at root)
            if (NeedsMigration(json))
            {
                Log.Information("Migrating config from single-server to multi-server format");
                var config = MigrateFromLegacy(json);
                Save(config);
                loadedFromFile = true;
                return config;
            }

            var result = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            MigrateApiKeys(json, result);
            var mappingsMigrated = MigrateSectionsToMappings(result);
            MigrateThemeToCyberpunk(result);
            if (mappingsMigrated)
            {
                // Keep the persisted baseline aligned with the live graph even
                // when the independent theme migration has already run.
                try { Save(result); }
                catch (Exception ex) { Log.Warning(ex, "Section mapping migration could not be persisted"); }
            }
            loadedFromFile = true;
            return result;
        }
        catch (Exception ex)
        {
            // OneDrive corruption hazard: an interrupted sync can leave appsettings.json
            // unparseable. Silently returning defaults here would WIPE every server
            // definition on the next Save(). Back up the corrupt file (so the user can
            // recover their servers) and log at ERROR (not Warning) before defaulting.
            string backupPath = Path.Combine(
                AppDataFolder, $"appsettings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(ConfigFilePath, backupPath, overwrite: false);
            }
            catch (Exception copyEx)
            {
                // Never let a backup failure mask the original load error.
                Log.Error(copyEx, "Failed to back up corrupt config to {BackupPath}", backupPath);
            }
            Log.Error(ex, "Config file at {ConfigPath} was unreadable (likely an interrupted OneDrive sync). " +
                "Backed up to {BackupPath}; loading default config", ConfigFilePath, backupPath);
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        SecureFile.WithPathLock(ConfigFilePath, () => SaveCore(config));
    }

    public static void Save(AppConfig config, string actor)
    {
        using var attribution = ConfigWriteScope.Fallback(actor);
        Save(config);
    }

    internal static void SaveRawSettings(JsonObject config, string actor)
    {
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        using var attribution = ConfigWriteScope.Fallback(actor);
        SecureFile.WithPathLock(ConfigFilePath, () =>
        {
            var saved = JsonNode.Parse(SaveSerialized(config.ToJsonString(JsonOptions)));
            if (saved?["library"] is { } library) config["library"] = library.DeepClone();
        });
    }

    private static void SaveCore(AppConfig config)
    {
        var saved = SaveSerialized(JsonSerializer.Serialize(config, JsonOptions));
        // Reflect the first-enable safety normalization only after persistence succeeds.
        if (JsonNode.Parse(saved)?["library"]?["dryRun"] is JsonValue dryRun)
            config.Library.DryRun = dryRun.GetValue<bool>();
    }

    private static string SaveSerialized(string json)
    {
        if (ReadOnly)
        {
            Log.Debug("ConfigManager.Save suppressed (ReadOnly mode)");
            return json;
        }
        Tls.CertificateManager.GuardPendingConfiguration(JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)!);
        Directory.CreateDirectory(AppDataFolder);
        string? beforeJson = File.Exists(ConfigFilePath) ? File.ReadAllText(ConfigFilePath) : null;
        var next = JsonNode.Parse(json)!;
        JsonNode? before = null;
        try { if (beforeJson != null) before = JsonNode.Parse(beforeJson); }
        catch (JsonException) { /* Unknown prior enablement cannot authorize destructive imports. */ }
        NormalizeLibraryEnable(before, next);
        // Keep default-only additions invisible to older strict MCP config readers.
        // Runtime snapshots still include them so JSON Pointer edits and schema defaults work.
        ConfigDowngradeCompatibility.OmitDefaultSections(next);
        // Preserve the hand-edited optional block byte-for-byte when normalization
        // only changes other settings (including an unparseable hooks draft).
        var hookSource = before is JsonObject && JsonNode.DeepEquals(before["hooks"], next["hooks"])
            ? beforeJson! : json;
        json = PreserveHookText(next.ToJsonString(JsonOptions), hookSource);
        SecureFile.WriteAllTextRestricted(ConfigFilePath, json);

        if (SettingsSaved is { } saved)
        {
            var effectiveBefore = ConfigDowngradeCompatibility.EffectiveSnapshot(before);
            var effectiveAfter = ConfigDowngradeCompatibility.EffectiveSnapshot(next)!;
            foreach (Action<string, JsonNode?, JsonNode, ConfigWriteContext> observer in saved.GetInvocationList())
            {
                try { observer(ConfigFilePath, effectiveBefore, effectiveAfter, ConfigWriteScope.Current); }
                catch { Log.Warning("A configuration save observer failed; settings were saved"); }
            }
        }

        // Journal failure must not turn a successful settings write into an error.
        try
        {
            var options = JsonNode.Parse(json)?["configHistory"]?.Deserialize<ConfigHistoryConfig>(JsonOptions);
            if (options?.Enabled == true) History.Record(beforeJson, json, ConfigWriteScope.Current, options.RetentionCount);
        }
        catch
        {
            if (Interlocked.Exchange(ref _historyWarning, 1) == 0)
                Log.Warning("Configuration history was unavailable; settings were saved");
        }

        if (beforeJson != null)
        {
            try
            {
                var beforeNode = JsonNode.Parse(beforeJson);
                var afterNode  = JsonNode.Parse(json);
                var recorder = App.TelemetryRecorder;
                if (recorder != null && beforeNode != null && afterNode != null)
                {
                    var journalPointers = new Services.Operations.ConfigPointerSummary();
                    foreach (var (ptr, b, a) in AiAgent.ConfigDiff.Diff(beforeNode, afterNode))
                    {
                        var canonicalPointer = AiAgent.CorrectionDetector.CanonicalOverridePointer(ptr, beforeNode, afterNode);
                        journalPointers.Add(canonicalPointer ?? ptr);
                        // Never write a credential's value to the telemetry stream — the
                        // digest still proves the field changed. See ConfigSecretPointers.
                        recorder.Record(AiAgent.TelemetryStream.Overrides,
                            new AiAgent.ConfigOverrideEvent
                            {
                                JsonPointer = ptr,
                                CanonicalPointer = canonicalPointer,
                                Actor = ConfigWriteScope.Current.Actor,
                                AiAuditRef = ConfigWriteScope.Current.AuditRef,
                                BeforeValue = AiAgent.ConfigSecretPointers.MaskValue(ptr, b),
                                AfterValue  = AiAgent.ConfigSecretPointers.MaskValue(ptr, a)
                            });
                    }
                    App.RecordOperation("config-change-set", detail: journalPointers.Detail,
                        actor: ConfigWriteScope.Current.Actor, correlation: ConfigWriteScope.Current.AuditRef ?? "");
                }
            }
            catch (Exception ex) { Log.Debug(ex, "ConfigOverride emit failed"); }
        }
        return json;
    }

    internal static string PreserveHookText(string serialized, string source)
    {
        using var original = JsonDocument.Parse(source);
        if (!original.RootElement.TryGetProperty("hooks", out var hooks)) return serialized;
        var bytes = Encoding.UTF8.GetBytes(serialized);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("hooks")) continue;
            reader.Read();
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            return Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + hooks.GetRawText() +
                Encoding.UTF8.GetString(bytes.AsSpan(checked((int)reader.BytesConsumed)));
        }
        return serialized;
    }

    internal static void NormalizeLibraryEnable(JsonNode? before, JsonNode after)
    {
        static bool Enabled(JsonNode? root) => root is JsonObject obj &&
            obj["library"] is JsonObject library && library["enabled"] is JsonValue value &&
            value.TryGetValue<bool>(out var enabled) && enabled;
        if (Enabled(after) && !Enabled(before))
            after["library"]!["dryRun"] = true;
    }

    private static bool NeedsMigration(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["connection"] != null;
        }
        catch
        {
            return false;
        }
    }

    private static AppConfig MigrateFromLegacy(string json)
    {
        var node = JsonNode.Parse(json)!;
        var config = new AppConfig();

        // Migrate global settings
        if (node["logging"] != null)
            config.Logging = node["logging"].Deserialize<LoggingConfig>(JsonOptions) ?? new();
        if (node["downloads"] != null)
            config.Downloads = node["downloads"].Deserialize<DownloadConfig>(JsonOptions) ?? new();

        // Migrate single-server settings into Servers[0]
        var server = new ServerConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = "Server 1"
        };

        if (node["connection"] != null)
            server.Connection = node["connection"].Deserialize<ConnectionConfig>(JsonOptions) ?? new();
        if (node["mount"] != null)
            server.Mount = node["mount"].Deserialize<MountConfig>(JsonOptions) ?? new();
        if (node["tls"] != null)
            server.Tls = node["tls"].Deserialize<TlsConfig>(JsonOptions) ?? new();
        if (node["cache"] != null)
            server.Cache = node["cache"].Deserialize<CacheConfig>(JsonOptions) ?? new();
        if (node["pool"] != null)
            server.Pool = node["pool"].Deserialize<PoolConfig>(JsonOptions) ?? new();
        if (node["notifications"] != null)
            server.Notifications = node["notifications"].Deserialize<NotificationConfig>(JsonOptions) ?? new();

        // Use host as server name if available
        if (!string.IsNullOrEmpty(server.Connection.Host))
            server.Name = server.Connection.Host;

        config.Servers.Add(server);
        return config;
    }

    /// <summary>
    /// Migrate plaintext API keys from config JSON to Credential Manager, then re-save to strip them.
    /// </summary>
    /// <summary>
    /// Seeds SectionMappings from the legacy Sections dict if the mappings list
    /// is empty. Runs every load so existing configs get upgraded in place.
    /// The Sections dict is kept in sync during save (ServerEditDialog derives
    /// it from SectionMappings) so SpreadJob / SpreadManager / DashboardViewModel
    /// continue to work unchanged.
    /// </summary>
    internal static bool MigrateSectionsToMappings(AppConfig config)
    {
        var migrated = false;
        foreach (var server in config.Servers)
        {
            if (server.SpreadSite.SectionMappings.Count > 0) continue;
            if (server.SpreadSite.Sections.Count == 0) continue;

            foreach (var (key, path) in server.SpreadSite.Sections)
            {
                server.SpreadSite.SectionMappings.Add(new SectionMapping
                {
                    IrcSection = key,
                    RemoteSection = key,
                    Path = path,
                    TriggerRegex = ".*",
                    Enabled = true
                });
            }
            Log.Information("Migrated {Count} sections to SectionMappings on server {Name}",
                server.SpreadSite.Sections.Count, server.Name);
            migrated = true;
        }
        return migrated;
    }

    private static void MigrateApiKeys(string json, AppConfig config)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var downloads = node?["downloads"];
            if (downloads == null) return;

            var omdb = downloads["omdbApiKey"]?.GetValue<string>();
            var tmdb = downloads["tmdbApiKey"]?.GetValue<string>();
            bool migrated = false;

            if (!string.IsNullOrEmpty(omdb) && CredentialStore.GetApiKey("omdb") == null)
            {
                CredentialStore.SaveApiKey("omdb", omdb);
                Log.Information("Migrated OMDB API key to Credential Manager");
                migrated = true;
            }
            if (!string.IsNullOrEmpty(tmdb) && CredentialStore.GetApiKey("tmdb") == null)
            {
                CredentialStore.SaveApiKey("tmdb", tmdb);
                Log.Information("Migrated TMDB API key to Credential Manager");
                migrated = true;
            }

            if (migrated)
                Save(config); // Re-save without the plaintext keys (now JsonIgnored)
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "API key migration check failed");
        }
    }

    private static void MigrateThemeToCyberpunk(AppConfig config)
    {
        try
        {
            if (config.Downloads.CyberpunkPromoted) return;
            config.Downloads.CyberpunkPromoted = true;
            if (config.Downloads.Theme == "Dark")
            {
                config.Downloads.Theme = "Cyberpunk";
                Log.Information("Theme migrated: Dark -> Cyberpunk (one-time)");
            }
            Save(config);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Theme migration failed");
        }
    }
}
