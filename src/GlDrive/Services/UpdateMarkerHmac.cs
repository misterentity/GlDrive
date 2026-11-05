using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Writes and validates HMAC-SHA256-protected .updating marker files so the watchdog
/// can reject forged or replayed markers written by unprivileged user-context processes.
/// Key material is persisted via DPAPI (CurrentUser scope) in a companion .updating-key file.
/// </summary>
public static class UpdateMarkerHmac
{
    private static string KeyPath => Path.Combine(GlDrive.Util.AppDataPaths.Root, ".updating-key");

    // The first update-marker calls can arrive concurrently (the test runner does this reliably
    // on a clean profile). Without a gate, two callers can both observe a missing key, generate
    // different values, and overwrite the same file. A marker written with the losing value then
    // fails validation immediately. The atomic publish below also keeps another process from ever
    // observing a partially-written DPAPI blob.
    private static readonly object KeyCreationGate = new();

    private static byte[] GetOrCreateKey() => GetOrCreateKeyAt(KeyPath);

    internal static byte[] GetOrCreateKeyAt(string keyPath)
    {
        lock (KeyCreationGate)
        {
            if (TryReadKey(keyPath, out var existingKey)) return existingKey;

            var key = RandomNumberGenerator.GetBytes(32);
            var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            var dir = Path.GetDirectoryName(keyPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Publish from the same directory so the rename is atomic. If another process wins
            // the create race, its persisted key is authoritative and every caller converges on
            // it instead of overwriting it with a different value.
            var tempPath = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, protectedKey);
                try
                {
                    File.Move(tempPath, keyPath);
                    return key;
                }
                catch (IOException)
                {
                    if (TryReadKey(keyPath, out existingKey)) return existingKey;

                    // Preserve the old self-healing behaviour for a corrupt key file, but replace
                    // it atomically rather than exposing a truncated blob to another reader.
                    File.Move(tempPath, keyPath, overwrite: true);
                    return key;
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
    }

    private static bool TryReadKey(string keyPath, out byte[] key)
    {
        key = [];
        if (!File.Exists(keyPath)) return false;
        try
        {
            var encrypted = File.ReadAllBytes(keyPath);
            var candidate = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            if (candidate.Length != 32) return false;
            key = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ComputeHmac(byte[] key, string payload)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    public static void Write(string markerPath, int processId)
    {
        WriteForProcess(markerPath, processId, Environment.ProcessPath ?? string.Empty);
    }

    internal static void WriteForProcess(string markerPath, int processId, string processPath)
    {
        try
        {
            var key = GetOrCreateKey();
            var payload = $"{processId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{processPath}";
            var mac = Convert.ToHexString(ComputeHmac(key, payload));
            var dir = Path.GetDirectoryName(markerPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, payload + "\n" + mac);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write HMAC update marker at {Path}", markerPath);
        }
    }

    public static void WriteAuthorization(string markerPath, int processId, string extractDir, string installDir)
    {
        try
        {
            var key = GetOrCreateKey();
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var manifest = ComputeDirectoryManifest(fullExtract);
            var payload = $"{processId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{fullExtract}|{fullInstall}|{manifest}";
            var mac = Convert.ToHexString(ComputeHmac(key, payload));
            var dir = Path.GetDirectoryName(markerPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, payload + "\n" + mac);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write update authorization at {Path}", markerPath);
        }
    }

    public static bool IsValidAuthorization(
        string markerPath, int processId, string extractDir, string installDir)
    {
        if (!TryReadValidPayload(markerPath, out var payload)) return false;
        try
        {
            var parts = payload.Split('|');
            if (parts.Length != 5 || !int.TryParse(parts[0], out var markerPid) || markerPid != processId)
                return false;
            if (!long.TryParse(parts[1], out var ts) ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
                return false;

            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            if (!string.Equals(parts[2], fullExtract, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[3], fullInstall, StringComparison.OrdinalIgnoreCase))
                return false;

            var currentManifest = ComputeDirectoryManifest(fullExtract);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(parts[4]), Convert.FromHexString(currentManifest));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the marker exists AND has a valid HMAC AND the embedded timestamp
    /// is within 5 minutes of now. Plain (non-HMAC) markers from older versions are rejected.
    /// </summary>
    public static bool IsValid(string markerPath)
    {
        if (!TryReadValidPayload(markerPath, out var payload)) return false;
        var parts = payload.Split('|');
        return parts.Length >= 2 && long.TryParse(parts[1], out var ts) &&
               Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= 300;
    }

    private static bool TryReadValidPayload(string markerPath, out string payload)
    {
        payload = string.Empty;
        if (!File.Exists(markerPath)) return false;
        try
        {
            var text = File.ReadAllText(markerPath).Trim();
            var nl = text.IndexOf('\n');
            if (nl < 0) return false;
            payload = text[..nl].Trim();
            var storedMac = Convert.FromHexString(text[(nl + 1)..].Trim());
            var expectedMac = ComputeHmac(GetOrCreateKey(), payload);
            return CryptographicOperations.FixedTimeEquals(expectedMac, storedMac);
        }
        catch
        {
            payload = string.Empty;
            return false;
        }
    }

    internal static IReadOnlyList<string> EnumerateManifestFiles(string directory)
    {
        var files = new List<string>();
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(directory));
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Reparse point in update staging: {entry.FullName}");
                if (entry is DirectoryInfo childDirectory)
                    pending.Enqueue(childDirectory);
                else if (entry is FileInfo file)
                    files.Add(file.FullName);
            }
        }

        return files.OrderBy(path => Path.GetRelativePath(directory, path),
            StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ComputeDirectoryManifest(string directory)
    {
        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateManifestFiles(directory))
        {
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            manifestHash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            using var stream = File.OpenRead(file);
            manifestHash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(manifestHash.GetHashAndReset());
    }
}
