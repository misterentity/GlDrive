using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Irc;
using Meziantou.Framework.Win32;

namespace GlDrive.AiAgent;

internal static partial class OutboundSecretInventory
{
    // These bounds apply only to passive diagnostics. The LLM boundary is unchanged.
    internal static IReadOnlyList<string> CollectForDiagnostics()
    {
        if (CredentialStore.AccessDisabled) throw new InvalidOperationException("Diagnostic credential access is disabled");
        return CollectForDiagnostics(ConfigManager.ConfigPath, ConfigManager.AppDataPath,
            () => CredentialManager.EnumerateCredentials("GlDrive:*").Select(c => c.Password).OfType<string>(), ReadLiveSecrets);
    }

    internal static IReadOnlyList<string> CollectForDiagnostics(string configPath, string fishDirectory,
        Func<IEnumerable<string>> credentials, Func<IEnumerable<string>>? live = null)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var count = 0; var chars = 0;
        void Add(string value)
        {
            if (++count > 8192 || value.Length > 65536 || (chars += value.Length) > 256 * 1024)
                throw new InvalidDataException("Diagnostic credential inventory exceeds its bound");
            if (value.Length > 0) secrets.Add(value);
        }
        var config = JsonNode.Parse(Encoding.UTF8.GetString(ReadDiagnosticFile(configPath, 2 * 1024 * 1024)).TrimStart('\uFEFF'))
            as JsonObject ?? throw new InvalidDataException("Diagnostic configuration is unavailable");
        foreach (var value in ConfigSecrets(config)) Add(value);
        foreach (var value in credentials()) Add(value);
        foreach (var value in ReadDiagnosticFishSecrets(fishDirectory)) Add(value);
        if (live != null) foreach (var value in live()) Add(value);
        return secrets.ToArray();
    }

    internal static IEnumerable<string> ReadDiagnosticFishSecrets(string directory)
    {
        var files = 0; var remaining = 4 * 1024 * 1024;
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in FishKeyStore.InventoryFiles(directory))
        {
            if (++files > 64) throw new InvalidDataException("Diagnostic key-file bound exceeded");
            var bytes = ReadDiagnosticFile(file, Math.Min(1024 * 1024, remaining));
            remaining -= bytes.Length;
            foreach (var secret in ReadFishSecrets(file, bytes)) yield return secret;
        }
    }

    internal static byte[] ReadDiagnosticFile(string path, int maxBytes)
    {
        // Enforce while streaming: a size precheck cannot bound a concurrently growing file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream(Math.Min(Math.Max(maxBytes, 0), 65536));
        var buffer = new byte[8192]; var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, Math.Max(1, maxBytes - total + 1)));
            if (read == 0) return output.ToArray();
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Diagnostic input exceeds its byte bound");
            output.Write(buffer, 0, read);
        }
    }
}
