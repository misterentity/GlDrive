using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Irc;

public class FishKeyEntry
{
    public string Key { get; set; } = "";
    /// <summary>
    /// Alternate-alphabet derived key (DH1080-only). Empty for manually-set keys.
    /// Standard base64 alphabet is the primary; FiSH ECB alphabet is the alt
    /// (or vice-versa after a successful alt-decrypt swap). On incoming decrypt,
    /// if Key fails we try AltKey; if AltKey works we swap them so subsequent
    /// encrypts use the alphabet peer's client expects.
    /// </summary>
    public string AltKey { get; set; } = "";
    /// <summary>
    /// Third DH1080-derived variant: fish-base64 of the RAW shared secret (no SHA256).
    /// Older mIRC fish_inj.dll forks use this KDF. Empty for manually-set keys.
    /// </summary>
    public string Key3 { get; set; } = "";
    public FishMode Mode { get; set; } = FishMode.CBC;
    public DateTime SetAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True if user set this key manually via /key. False for DH1080-derived keys.
    /// Manual keys are protected from being overwritten by /keyx so a working
    /// static key isn't blown away if someone (re)triggers a key exchange.
    /// </summary>
    public bool Manual { get; set; }
}

public class FishKeyStore
{
    private readonly string _filePath;
    private Dictionary<string, FishKeyEntry> _keys = new(StringComparer.OrdinalIgnoreCase);
    // Serializes ALL map reads/writes. Concurrent incoming messages (read loop + DH1080
    // Task.Run + UI /key) can race the GetKey→decrypt→SetKeyWithAlt variant-promotion
    // window; without this lock the wrong base64-alphabet variant can be promoted and a
    // half-written map could be enumerated/saved. Dual-alphabet semantics (Key/AltKey/Key3)
    // are preserved — the lock only makes each lookup/mutation atomic, never collapses variants.
    private readonly object _keyLock = new();

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
        // Lock guards lookups against concurrent variant-promotion writes (Save is sync, no await held).
        lock (_keyLock)
        {
            _keys.TryGetValue(target, out var entry);
            return entry;
        }
    }

    public void SetKey(string target, string key, FishMode mode = FishMode.ECB, bool manual = true)
    {
        lock (_keyLock)
        {
            _keys[target] = new FishKeyEntry { Key = key, AltKey = "", Mode = mode, Manual = manual, SetAt = DateTime.UtcNow };
            Save();
        }
    }

    /// <summary>
    /// Updates a stored key trio — preserves the existing entry's Manual flag if there is one.
    /// Used for alphabet swap / mode auto-detect; do NOT use for fresh DH1080 results.
    /// </summary>
    public void SetKeyWithAlt(string target, string key, string altKey, string key3, FishMode mode)
    {
        // Read-manual-flag + replace must be atomic so a concurrent SetDh1080Keys/SetKey can't
        // interleave and flip the Manual decision or lose the promoted dual-alphabet variant.
        lock (_keyLock)
        {
            var manual = _keys.TryGetValue(target, out var existing) && existing.Manual;
            _keys[target] = new FishKeyEntry { Key = key, AltKey = altKey, Key3 = key3, Mode = mode, Manual = manual, SetAt = DateTime.UtcNow };
            Save();
        }
    }

    /// <summary>
    /// Stores a freshly DH1080-derived key trio. Refuses to overwrite a manually-set
    /// key so /keyx doesn't blow away a working static key the user explicitly set.
    /// Returns true on success, false if a manual key blocked the write.
    /// </summary>
    public bool SetDh1080Keys(string target, string key, string altKey, string key3, FishMode mode)
    {
        // Check-manual + write atomic: protects the manual-key invariant from a racing /key
        // that could otherwise let DH1080 overwrite a just-set manual key (or vice-versa).
        lock (_keyLock)
        {
            if (_keys.TryGetValue(target, out var existing) && existing.Manual)
                return false;
            _keys[target] = new FishKeyEntry { Key = key, AltKey = altKey, Key3 = key3, Mode = mode, Manual = false, SetAt = DateTime.UtcNow };
            Save();
            return true;
        }
    }

    public void RemoveKey(string target)
    {
        lock (_keyLock)
        {
            if (_keys.Remove(target))
                Save();
        }
    }

    // Return a snapshot — the live map must never escape the lock or callers could enumerate
    // it mid-mutation (variant promotion replaces entries) and crash on concurrent modification.
    public IReadOnlyDictionary<string, FishKeyEntry> GetAllKeys()
    {
        lock (_keyLock)
            return new Dictionary<string, FishKeyEntry>(_keys, StringComparer.OrdinalIgnoreCase);
    }

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
            // Mutate the map + re-save under the same lock all other writers use, so a key set
            // during startup load can't be clobbered by the deserialized snapshot.
            lock (_keyLock)
            {
                // Re-wrap to preserve case-insensitive lookup (deserializer uses default comparer)
                _keys = loaded != null
                    ? new Dictionary<string, FishKeyEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);

                // Re-save to ensure encrypted format
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load FiSH keys, starting empty");
            lock (_keyLock)
                _keys = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // MUST be called while holding _keyLock — it reads _keys directly and is non-reentrant
    // by design (the lock is a plain Monitor; never lock inside here or it self-deadlocks).
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
            SecureFile.RestrictFilePermissions(_filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save FiSH keys");
        }
    }
}
