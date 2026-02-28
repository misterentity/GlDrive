using System.IO;
using System.Text.Json;
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
}
