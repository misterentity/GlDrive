using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GlDrive.Util;

/// <summary>
/// JSON files encrypted with DPAPI (CurrentUser scope, no entropy) — the on-disk format the
/// PM history, IRC unread and addline-offer stores have always used. Writes go through
/// <see cref="SecureFile.WriteAllBytesRestricted"/>: unique staging file, restricted ACL in
/// place before the content lands, atomic replace, retry on transient locks.
///
/// Stores with verified-readback or quarantine flows (FishKeyStore, ConfigSecretCopy,
/// AuthHoldStore) deliberately do not use this.
/// </summary>
public static class DpapiFile
{
    /// <summary>
    /// The decoded value, or null when the file does not exist. An unreadable file (DPAPI
    /// failure, bad JSON, a JSON null, over <paramref name="maxBytes"/>, or rejected by
    /// <paramref name="validate"/>) also reads as null: it is reported to
    /// <paramref name="onCorrupt"/> and, when <paramref name="keepCorrupt"/>, copied to
    /// "&lt;path&gt;.corrupt" first so the evidence survives the next save.
    /// </summary>
    public static T? ReadJson<T>(string path, Func<T, bool>? validate = null, JsonSerializerOptions? options = null,
        bool keepCorrupt = true, long maxBytes = long.MaxValue, Action<Exception>? onCorrupt = null) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            byte[] encrypted;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > maxBytes) throw new InvalidDataException($"File exceeds {maxBytes} bytes.");
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                encrypted = memory.ToArray();
            }
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var value = JsonSerializer.Deserialize<T>(json, options) ?? throw new InvalidDataException("File holds a JSON null.");
            if (validate != null && !validate(value)) throw new InvalidDataException("File failed validation.");
            return value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (keepCorrupt)
                try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            onCorrupt?.Invoke(ex);
            return null;
        }
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null)
        => WriteBytes(path, Encode(value, options));

    /// <summary>The encrypted bytes <see cref="WriteJson{T}"/> would write.</summary>
    public static byte[] Encode<T>(T value, JsonSerializerOptions? options = null)
        => ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value, options), null, DataProtectionScope.CurrentUser);

    /// <summary>Writes bytes from <see cref="Encode{T}"/>.</summary>
    public static void WriteBytes(string path, byte[] encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SecureFile.WriteAllBytesRestricted(path, encrypted);
    }
}
