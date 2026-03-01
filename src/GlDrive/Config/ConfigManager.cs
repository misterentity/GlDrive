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

            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
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
        File.WriteAllText(ConfigFilePath, json);
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
}
