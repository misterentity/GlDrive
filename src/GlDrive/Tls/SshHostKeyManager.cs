using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Tls;

public enum SshHostKeyStatus
{
    Unknown,
    Match,
    Changed
}

public sealed class SshHostKeyManager
{
    private static readonly Lock Sync = new();
    private static readonly string FilePath = Path.Combine(ConfigManager.AppDataPath, "trusted_ssh_hosts.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static Dictionary<string, SshHostKey> _keys = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public SshHostKeyStatus Check(string host, int port, string fingerprint, out string? previousFingerprint)
    {
        EnsureLoaded();
        var key = $"{host}:{port}";
        lock (Sync)
        {
            if (!_keys.TryGetValue(key, out var trusted))
            {
                previousFingerprint = null;
                return SshHostKeyStatus.Unknown;
            }

            previousFingerprint = trusted.Fingerprint;
            return string.Equals(trusted.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? SshHostKeyStatus.Match
                : SshHostKeyStatus.Changed;
        }
    }

    public void Trust(string host, int port, string algorithm, string fingerprint)
    {
        EnsureLoaded();
        lock (Sync)
        {
            _keys[$"{host}:{port}"] = new SshHostKey
            {
                Algorithm = algorithm,
                Fingerprint = fingerprint,
                TrustedAt = DateTime.UtcNow
            };
            SaveLocked();
        }
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, SshHostKey>>(
                                     File.ReadAllText(FilePath), JsonOptions)
                                 ?? new();
                    _keys = new Dictionary<string, SshHostKey>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load trusted SSH host keys");
                _keys = new(StringComparer.OrdinalIgnoreCase);
            }
            _loaded = true;
        }
    }

    private static void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        SecureFile.WriteAllTextRestricted(FilePath, JsonSerializer.Serialize(_keys, JsonOptions));
    }
}

public sealed class SshHostKey
{
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAt { get; set; }
}
