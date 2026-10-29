using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Irc;
using Meziantou.Framework.Win32;

namespace GlDrive.AiAgent;

/// <summary>Reads the credentials that must be removed at the outbound LLM boundary.</summary>
internal static partial class OutboundSecretInventory
{
    // Runtime stores can contain a newer key than disk after an unsuccessful save.
    // The application supplies snapshots; the inventory never owns or changes those stores.
    internal static Func<IEnumerable<string>>? ReadLiveSecrets { get; set; }

    public static IReadOnlyList<string> Collect()
    {
        try
        {
            if (CredentialStore.AccessDisabled)
                throw new InvalidOperationException("Credential access is disabled.");

            return Collect(ConfigManager.ConfigPath, ConfigManager.AppDataPath,
                () => CredentialManager.EnumerateCredentials("GlDrive:*")
                    .Select(credential => credential.Password).OfType<string>(), ReadLiveSecrets);
        }
        catch (Exception)
        {
            // Neither a malformed JSON excerpt nor a file/credential name belongs in
            // an exception that callers might log. Do not return a partial inventory.
            throw new InvalidOperationException("Outbound secret inventory is unavailable.");
        }
    }

    internal static IReadOnlyList<string> Collect(string configPath, string fishDirectory,
        Func<IEnumerable<string>> readCredentialSecrets, Func<IEnumerable<string>>? readLiveSecrets = null)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))
            ?? throw new InvalidDataException("Configuration must contain a JSON object.");
        if (config is not JsonObject)
            throw new InvalidDataException("Configuration must contain a JSON object.");

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value)) secrets.Add(value);
        }

        foreach (var value in ConfigSecrets(config)) Add(value);
        foreach (var value in readCredentialSecrets()) Add(value);
        foreach (var file in FishKeyStore.InventoryFiles(fishDirectory))
            foreach (var value in ReadFishSecrets(file, File.ReadAllBytes(file))) Add(value);
        if (readLiveSecrets != null)
            foreach (var value in readLiveSecrets()) Add(value);

        return secrets.OrderByDescending(value => value.Length).ToArray();
    }

    internal static IReadOnlyList<string> ConfigSecrets(JsonNode node)
    {
        var secrets = new List<string>();
        Visit(node, false);
        return secrets;

        void Visit(JsonNode? current, bool secret)
        {
            if (current is JsonObject obj)
            {
                foreach (var property in obj)
                {
                    // Hook definitions are private configuration, not credentials. Mask the
                    // entire block in config views without making its ordinary values tokens.
                    if (ReferenceEquals(obj, node) && property.Key.Equals("hooks", StringComparison.OrdinalIgnoreCase)) continue;
                    Visit(property.Value, secret || ConfigSecretPointers.IsSecret(property.Key));
                }
            }
            else if (current is JsonArray array)
            {
                foreach (var item in array) Visit(item, secret);
            }
            else if (secret && current is JsonValue scalar)
            {
                // Preserve even short credential strings; numbers and booleans are not secrets.
                if (scalar.TryGetValue<string>(out var value) && !string.IsNullOrEmpty(value)) secrets.Add(value);
            }
        }
    }

    internal static IReadOnlyList<string> ReadFishSecrets(byte[] bytes)
    {
        string json;
        try
        {
            var plaintext = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            try { json = Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException)
        {
            // FishKeyStore supports legacy plaintext files. Read that format too,
            // but do not invoke its constructor: Load would migrate/rewrite the file.
            json = Encoding.UTF8.GetString(bytes);
        }

        using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("FiSH keys must contain a JSON object.");
        // Even an invalid entry can contain a secret. Inventory strings independently
        // of the runtime entry validation, without letting one malformed row poison all keys.
        return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .SelectMany(p => p.Value.EnumerateObject()).Where(p => p.Name is "key" or "altKey" or "dhSecretHex")
            .Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!)
            .Where(value => !string.IsNullOrEmpty(value)).ToArray();
    }

    internal static IReadOnlyList<string> ReadFishSecrets(string path, byte[] bytes)
    {
        try { return ReadFishSecrets(bytes); }
        catch when (FishKeyStore.HasVerifiedQuarantine(path, bytes)) { return []; }
    }
}
