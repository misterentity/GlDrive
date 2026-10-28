using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Ftp;

internal sealed record StoredAuthHold(string AccountKey, string? ProtectedCredentialDigest,
    DateTime NextProbeAtUtc, bool NotificationDelivered);

/// <summary>Only authentication holds are durable. No readable credential digest is written.</summary>
internal sealed class AuthHoldStore
{
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    internal bool LoadFailed { get; private set; }

    internal AuthHoldStore(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _path = path;
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    }

    internal List<StoredAuthHold> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Authentication hold state was not an array");
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty(nameof(StoredAuthHold.AccountKey), out var key)
                    || key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: 64 } accountKey
                    || accountKey.Any(c => !Uri.IsHexDigit(c))
                    || !row.TryGetProperty(nameof(StoredAuthHold.NextProbeAtUtc), out var deadline)
                    || deadline.ValueKind != JsonValueKind.String || !deadline.TryGetDateTime(out var utc)
                    || utc.Kind != DateTimeKind.Utc
                    || !row.TryGetProperty(nameof(StoredAuthHold.NotificationDelivered), out var delivered)
                    || delivered.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !row.TryGetProperty(nameof(StoredAuthHold.ProtectedCredentialDigest), out var marker)
                    || marker.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    throw new InvalidDataException("Invalid authentication hold record");
            }
            return JsonSerializer.Deserialize<List<StoredAuthHold>>(json)
                ?? throw new InvalidDataException("Authentication hold state was null");
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            Log.Warning(ex, "Authentication hold state is unreadable; quarantined it and starting with no holds");
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
            return [];
        }
    }

    internal string? Protect(byte[]? digest)
    {
        if (digest == null) return null;
        try { return Convert.ToBase64String(_protect(digest)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not protect authentication credential marker; retaining hold without a marker");
            return null;
        }
    }

    internal byte[]? Unprotect(string? marker)
    {
        if (marker == null) return null;
        try
        {
            var digest = _unprotect(Convert.FromBase64String(marker));
            return digest.Length == SHA256.HashSizeInBytes ? digest : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read protected authentication marker; manual retry or slow probe required");
            return null;
        }
    }

    internal bool Save(IReadOnlyList<StoredAuthHold> holds)
    {
        try
        {
            SecureFile.WithPathLock(_path, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(holds));
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist authentication hold; new recovery probes remain blocked");
            return false;
        }
    }
}
