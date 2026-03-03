using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Irc;

public class FishKeyEntry
{
    public string Key { get; set; } = "";
    public FishMode Mode { get; set; } = FishMode.ECB;
    public DateTime SetAt { get; set; } = DateTime.UtcNow;
}

public class FishKeyStore
{
    private readonly string _filePath;
    private Dictionary<string, FishKeyEntry> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FishKeyStore(string serverId)
    {
        _filePath = Path.Combine(ConfigManager.AppDataPath, $"fish-keys-{serverId}.json");
        Load();
    }

    public FishKeyEntry? GetKey(string target)
    {
        _keys.TryGetValue(target, out var entry);
        return entry;
    }

    public void SetKey(string target, string key, FishMode mode = FishMode.ECB)
    {
        _keys[target] = new FishKeyEntry { Key = key, Mode = mode, SetAt = DateTime.UtcNow };
        Save();
    }

    public void RemoveKey(string target)
    {
        if (_keys.Remove(target))
            Save();
    }

    public IReadOnlyDictionary<string, FishKeyEntry> GetAllKeys() => _keys;

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _keys = JsonSerializer.Deserialize<Dictionary<string, FishKeyEntry>>(json, JsonOptions)
                    ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load FiSH keys, starting empty");
            _keys = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_keys, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save FiSH keys");
        }
    }
}
