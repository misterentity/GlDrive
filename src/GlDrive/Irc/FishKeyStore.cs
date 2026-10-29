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
    /// Raw DH1080 shared secret (hex) for DH-derived keys. Empty for manual keys. Kept so we can
    /// derive a peer's non-canonical KDF variant on the fly if the canonical key fails to decrypt
    /// (see Dh1080.AlternateKdfCandidates) without forcing a re-key.
    /// </summary>
    public string DhSecretHex { get; set; } = "";
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
    // half-written map could be enumerated/saved. Dual-alphabet semantics (Key/AltKey)
    // are preserved — the lock only makes each lookup/mutation atomic, never collapses variants.
    private readonly object _keyLock = new();
    private bool _unreadableOriginal;
    private bool _recoveryRequired;
    private int _writeWarning;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    public bool PersistenceHealthy => !_unreadableOriginal;
    public bool RecoveryRequired => _recoveryRequired;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FishKeyStore(string serverId) : this(Path.Combine(ConfigManager.AppDataPath, $"fish-keys-{serverId}.json"),
        bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser),
        bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)) { }

    internal FishKeyStore(string filePath, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        _filePath = filePath;
        _protect = protect;
        _unprotect = unprotect;
        Load();
    }

    public FishKeyEntry? GetKey(string target)
    {
        // Lock guards lookups against concurrent variant-promotion writes (Save is sync, no await held).
        lock (_keyLock)
        {
            _keys.TryGetValue(target, out var entry);
            return entry == null ? null : Clone(entry);
        }
    }

    public void SetKey(string target, string key, FishMode mode = FishMode.ECB, bool manual = true)
    {
        ValidateKey(target, key, mode);
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
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
    public void SetKeyWithAlt(string target, string key, string altKey, FishMode mode)
    {
        ValidateKey(target, key, mode);
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        // Read-manual-flag + replace must be atomic so a concurrent SetDh1080Keys/SetKey can't
        // interleave and flip the Manual decision or lose the promoted dual-alphabet variant.
        // Preserve the DH shared secret so alternate-KDF recovery still works after a mode flip
        // or an alphabet-variant promotion.
        lock (_keyLock)
        {
            var existed = _keys.TryGetValue(target, out var existing);
            var manual = existed && existing!.Manual;
            var secret = existed ? existing!.DhSecretHex : "";
            _keys[target] = new FishKeyEntry { Key = key, AltKey = altKey, DhSecretHex = secret, Mode = mode, Manual = manual, SetAt = DateTime.UtcNow };
            Save();
        }
    }

    /// <summary>
    /// Stores a freshly DH1080-derived key trio. Refuses to overwrite a manually-set
    /// key so /keyx doesn't blow away a working static key the user explicitly set.
    /// Returns true on success, false if a manual key blocked the write.
    /// </summary>
    public bool SetDh1080Keys(string target, string key, string altKey, string secretHex, FishMode mode)
    {
        ValidateKey(target, key, mode);
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        // Check-manual + write atomic: protects the manual-key invariant from a racing /key
        // that could otherwise let DH1080 overwrite a just-set manual key (or vice-versa).
        lock (_keyLock)
        {
            if (_keys.TryGetValue(target, out var existing) && existing.Manual)
                return false;
            _keys[target] = new FishKeyEntry { Key = key, AltKey = altKey, DhSecretHex = secretHex, Mode = mode, Manual = false, SetAt = DateTime.UtcNow };
            Save();
            return true;
        }
    }

    public void RemoveKey(string target)
    {
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
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
            return _keys.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static FishKeyEntry Clone(FishKeyEntry entry) => new()
    {
        Key = entry.Key, AltKey = entry.AltKey ?? "", DhSecretHex = entry.DhSecretHex ?? "",
        Mode = entry.Mode, SetAt = entry.SetAt, Manual = entry.Manual
    };

    /// <summary>Explicit offline restore; retain current secrets unless replacement was chosen.</summary>
    public bool TryImportVerified(string target, FishKeyEntry entry, bool replaceExisting = false)
        => ValidEntry(target, entry) && TryImportVerified(new Dictionary<string, FishKeyEntry> { [target] = entry }, replaceExisting);

    internal bool TryImportVerified(IReadOnlyDictionary<string, FishKeyEntry> entries, bool replaceExisting = false)
    {
        if (entries.Any(p => !ValidEntry(p.Key, p.Value))) return false;
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        lock (_keyLock)
        {
            if (_unreadableOriginal) return false;
            var candidate = _keys.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var (target, entry) in entries)
            {
                if (_keys.TryGetValue(target, out var prior) && !replaceExisting && !SameEntry(prior, entry)) return false;
                candidate[target] = Clone(entry);
            }
            try
            {
                SecureFile.WithPathLock(_filePath, () =>
                {
                    // Verify the encrypted candidate before replacing any working key file.
                    var staged = _filePath + ".import-candidate";
                    Write(candidate, staged);
                    var readback = Decode(File.ReadAllBytes(staged));
                    if (readback.Count != candidate.Count || candidate.Any(pair =>
                            !readback.TryGetValue(pair.Key, out var actual) || !SameEntry(pair.Value, actual)))
                        throw new IOException("FiSH import verification failed");
                    // Preserve the previous encrypted file before an explicitly chosen overwrite.
                    if (File.Exists(_filePath))
                        PreserveVerified(_filePath, ".pre-import-");
                    File.Move(staged, _filePath, overwrite: true);
                    // An explicit verified import supersedes any older pending snapshot.
                    try { File.Delete(_filePath + ".pending"); } catch { WarnWrite(); }
                });
                _keys = candidate;
                return true;
            }
            catch { WarnWrite(); return false; }
        }
    }

    private static bool SameEntry(FishKeyEntry left, FishKeyEntry right) =>
        left.Key == right.Key && (left.AltKey ?? "") == (right.AltKey ?? "") &&
        (left.DhSecretHex ?? "") == (right.DhSecretHex ?? "") && left.Mode == right.Mode &&
        left.Manual == right.Manual && left.SetAt == right.SetAt;

    private Dictionary<string, FishKeyEntry> Decode(byte[] bytes)
    {
        byte[] plain;
        try { plain = _unprotect(bytes); }
        catch (CryptographicException)
        {
            // Only an actual JSON object is a legacy store; encrypted corruption isn't JSON.
            if (!Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('{')) throw;
            plain = bytes.ToArray();
        }
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(plain).TrimStart('\uFEFF'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("FiSH store must be an object");
            var keys = new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                try
                {
                    var entry = property.Value.Deserialize<FishKeyEntry>(JsonOptions);
                    if (!ValidEntry(property.Name, entry)) { skipped++; continue; }
                    keys[property.Name] = Clone(entry!);
                }
                catch (JsonException) { skipped++; }
            }
            if (skipped > 0)
            {
                _recoveryRequired = true;
                Log.Warning("Skipped {Count} invalid FiSH entries; valid keys remain available", skipped);
            }
            return keys;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void Load()
    {
        using var inventoryChange = Logging.DiagnosticRedactorCache.BeginInputChange();
        lock (_keyLock)
        {
            try
            {
                SecureFile.WithPathLock(_filePath, () =>
                {
                    try { _recoveryRequired = QuarantineCopies(_filePath).Any(); }
                    catch { /* Recovery metadata must not prevent loading a readable primary. */ }
                    var pending = _filePath + ".pending";
                    if (File.Exists(_filePath))
                    {
                        try { _keys = Decode(File.ReadAllBytes(_filePath)); return; }
                        catch
                        {
                            _unreadableOriginal = _recoveryRequired = true;
                            try
                            {
                                var copy = PreserveVerified(_filePath, ".corrupt-");
                                // A verified copy exists before moving unknown secret material.
                                File.Move(_filePath, copy + ".quarantined");
                                _unreadableOriginal = false;
                                SecureFile.RestrictFilePermissions(copy + ".quarantined");
                            }
                            catch { /* Keep the primary and use a pending file if quarantine cannot finish. */ }
                            Log.Warning("FiSH key storage was unreadable; recovery is required. Valid and newly learned keys remain available");
                        }
                    }
                    if (File.Exists(pending))
                    {
                        try { _keys = Decode(File.ReadAllBytes(pending)); }
                        catch
                        {
                            _recoveryRequired = true;
                            PreserveVerified(pending, ".corrupt-");
                            Log.Warning("Pending FiSH keys were unreadable; preserved for local recovery");
                        }
                    }
                    if (!_unreadableOriginal && (_recoveryRequired || File.Exists(pending)))
                    {
                        // Publish a verified readable primary for older builds and inventories too.
                        var staged = _filePath + ".recovery-candidate";
                        Write(_keys, staged);
                        var verified = Decode(File.ReadAllBytes(staged));
                        if (verified.Count != _keys.Count || _keys.Any(p => !verified.TryGetValue(p.Key, out var entry) || !SameEntry(p.Value, entry)))
                            throw new IOException("FiSH recovery verification failed");
                        File.Move(staged, _filePath, overwrite: false);
                        if (File.Exists(pending)) File.Delete(pending);
                    }
                });
            }
            catch { WarnWrite(); }
        }
    }

    internal static bool ValidEntry(string target, FishKeyEntry? entry) =>
        !string.IsNullOrWhiteSpace(target) && entry != null && !string.IsNullOrWhiteSpace(entry.Key) && Enum.IsDefined(entry.Mode);

    private static void ValidateKey(string target, string key, FishMode mode)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("FiSH target is required", nameof(target));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("FiSH key is required", nameof(key));
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid FiSH mode", nameof(mode));
    }

    private static string PreserveVerified(string path, string suffix)
    {
        var bytes = File.ReadAllBytes(path);
        var copy = path + suffix + Guid.NewGuid().ToString("N");
        SecureFile.WriteAllBytesRestricted(copy, bytes);
        if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(copy))) throw new IOException("FiSH preservation verification failed");
        return copy;
    }

    private static IEnumerable<string> QuarantineCopies(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".corrupt*") : [];
    }

    internal static bool HasVerifiedQuarantine(string path, byte[] bytes)
    {
        foreach (var copy in QuarantineCopies(path))
            try { if (new FileInfo(copy).Length == bytes.Length && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(copy))) return true; }
            catch { }
        return false;
    }

    internal static IEnumerable<string> InventoryFiles(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "fish-keys-*.json").Concat(Directory.EnumerateFiles(directory, "fish-keys-*.json.pending"))
        : [];

    // MUST be called while holding _keyLock so the serialized map is a stable snapshot.
    private void Save()
    {
        try
        {
            SecureFile.WithPathLock(_filePath, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                if (_unreadableOriginal)
                {
                    // A startup sweep may have repaired the primary after this instance
                    // loaded. Re-check under its lock, but never replace current runtime keys.
                    try
                    {
                        if (File.Exists(_filePath)) _ = Decode(File.ReadAllBytes(_filePath));
                        _unreadableOriginal = false;
                    }
                    catch { /* Keep new keys in pending while the primary remains unreadable. */ }
                }
                // The primary lock also serializes pending writes with recovery promotion.
                Write(_keys, _unreadableOriginal ? _filePath + ".pending" : _filePath);
            });
        }
        catch (Exception)
        {
            WarnWrite();
        }
    }

    private void Write(Dictionary<string, FishKeyEntry> keys, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(keys, JsonOptions);
        try { SecureFile.WriteAllBytesRestricted(path, _protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void WarnWrite()
    {
        if (Interlocked.Exchange(ref _writeWarning, 1) == 0)
            Log.Warning("FiSH keys could not be persisted; current keys remain in memory");
    }
}
