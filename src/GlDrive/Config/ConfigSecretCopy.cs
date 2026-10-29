using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Config;

/// <summary>
/// First migration stage. Verified protected copies are recovery evidence, never a
/// competing runtime source. Originals remain readable by older apps and MCP bridges.
/// </summary>
internal sealed class ConfigSecretCopy
{
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly Action<string, string> _write;
    private int _warned;
    internal ConfigSecretCopy(string directory, Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null, Action<string, string>? write = null)
    {
        _path = Path.Combine(directory, "config-secrets.dpapi");
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        _write = write ?? SecureFile.WriteAllTextRestricted;
    }

    internal bool CopyAndVerify(AppConfig config)
    {
        if (ConfigManager.ReadOnly || !config.DataStewardship.CopyConfigSecrets) return false;
        try
        {
            var entries = new List<Entry>();
            if (!string.IsNullOrEmpty(config.ControlApi.Token))
                entries.Add(new("control", "", "", config.ControlApi.Token));
            foreach (var server in config.Servers)
                foreach (var channel in server.Irc.Channels)
                    if (!string.IsNullOrEmpty(channel.Key))
                        entries.Add(new("channel", server.Id, channel.Name, channel.Key));
            // A structured identity preserves server/channel boundaries and JOIN-key syntax.
            var content = JsonSerializer.Serialize(entries.OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.ServerId, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal));
            SecureFile.WithPathLock(_path, () =>
            {
                if (entries.Count == 0 && File.Exists(_path)) return;
                if (File.Exists(_path))
                {
                    var readable = false;
                    try
                    {
                        if (Read() == content) return;
                        readable = true;
                    }
                    catch
                    {
                        File.Copy(_path, _path + ".corrupt", overwrite: true);
                        SecureFile.RestrictFilePermissions(_path + ".corrupt");
                    }
                    if (readable) _write(_path + ".previous", File.ReadAllText(_path));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var plain = Encoding.UTF8.GetBytes(content);
                try { _write(_path, Convert.ToBase64String(_protect(plain))); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (Read() != content) throw new IOException("Protected copy verification failed");
            });
            return true;
        }
        catch
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                Log.Warning("Protected configuration copy was unavailable; original secrets remain in settings; retrying next start");
            return false;
        }
    }

    private string Read()
    {
        var plain = _unprotect(Convert.FromBase64String(File.ReadAllText(_path)));
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private sealed record Entry(string Kind, string ServerId, string Target, string Value);
}
