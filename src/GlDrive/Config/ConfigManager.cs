using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace GlDrive.Config;

public class ConfigManager
{
    private static readonly string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");

    private static readonly string ConfigFilePath =
        Path.Combine(AppDataFolder, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string AppDataPath => AppDataFolder;
    public static string ConfigPath => ConfigFilePath;
    public static bool ConfigExists => File.Exists(ConfigFilePath);

    public static AppConfig Load()
    {
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
                return config;
            }

            var result = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            MigrateApiKeys(json, result);
            MigrateSectionsToMappings(result);
            MigrateSlotDefaults(result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load config, using defaults");
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(AppDataFolder);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
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
    private static void MigrateSectionsToMappings(AppConfig config)
    {
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
        }
    }

    /// <summary>
    /// Bump per-server spread slot defaults from 1 (pre-v1.44.82) to 3 for
    /// existing configs. Slots=1 forces serial transfers in chain mode which
    /// can't keep up with glftpd's stale-release timeouts on large releases,
    /// causing dirscript to deny MKD mid-race and leaving incomplete releases
    /// that get nuked. Only migrates the exact old-default value of 1 so
    /// users who explicitly chose a different number are left alone.
    /// </summary>
    private static void MigrateSlotDefaults(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            var changed = false;
            if (server.SpreadSite.MaxUploadSlots == 1)
            {
                server.SpreadSite.MaxUploadSlots = 3;
                changed = true;
            }
            if (server.SpreadSite.MaxDownloadSlots == 1)
            {
                server.SpreadSite.MaxDownloadSlots = 3;
                changed = true;
            }
            if (changed)
                Log.Information("Migrated spread slot defaults 1→3 on server {Name} (was serial, now parallel)",
                    server.Name);
        }
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
}
