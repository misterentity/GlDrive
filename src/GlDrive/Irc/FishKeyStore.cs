using System.IO;
using System.Security.Cryptography;
using System.Text;
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
            var fileBytes = File.ReadAllBytes(_filePath);

            // Try DPAPI-encrypted format first, fall back to plaintext for migration
            string json;
            try
            {
                var decrypted = ProtectedData.Unprotect(fileBytes, null, DataProtectionScope.CurrentUser);
                json = Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Legacy plaintext format — will be re-saved as encrypted
                json = Encoding.UTF8.GetString(fileBytes);
                Log.Information("Migrating FiSH keys to encrypted storage");
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, FishKeyEntry>>(json, JsonOptions);
            // Re-wrap to preserve case-insensitive lookup (deserializer uses default comparer)
            _keys = loaded != null
                ? new Dictionary<string, FishKeyEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);

            // Re-save to ensure encrypted format
            Save();
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
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            var tempPath = _filePath + ".tmp";
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save FiSH keys");
        }
    }
}
