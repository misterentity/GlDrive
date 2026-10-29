using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Util;

namespace GlDrive.Config;

/// <summary>Keep the preceding release's strict config reader usable after a downgrade.</summary>
internal static class ConfigDowngradeCompatibility
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static void OmitDefaultSections(JsonNode root)
    {
        OmitDefault<DataStewardshipConfig>(root, "dataStewardship");
        OmitDefault<RecoveryConfig>(root, "recovery");
        if (root["controlApi"] is JsonObject control) OmitDefault<AutomationSecurityConfig>(control, "security");
    }

    internal static JsonNode? EffectiveSnapshot(JsonNode? source)
    {
        if (source?.DeepClone() is not JsonObject root) return source?.DeepClone();
        if (!root.ContainsKey("dataStewardship")) root["dataStewardship"] = JsonSerializer.SerializeToNode(new DataStewardshipConfig(), Json);
        if (!root.ContainsKey("recovery")) root["recovery"] = JsonSerializer.SerializeToNode(new RecoveryConfig(), Json);
        if (root["controlApi"] is JsonObject control && !control.ContainsKey("security"))
            control["security"] = JsonSerializer.SerializeToNode(new AutomationSecurityConfig(), Json);
        return root;
    }

    private static void OmitDefault<T>(JsonNode root, string name) where T : new()
    {
        if (root is not JsonObject parent || !parent.TryGetPropertyValue(name, out var value) || value is not JsonObject) return;
        try
        {
            // Compare effective values, so an explicit empty/default-only block is omitted too.
            // Isolating converters retain unknown/malformed drafts; those cannot equal defaults.
            var canonical = JsonSerializer.SerializeToNode(value.Deserialize<T>(Json), Json);
            if (JsonNode.DeepEquals(canonical, JsonSerializer.SerializeToNode(new T(), Json))) parent.Remove(name);
        }
        catch (JsonException) { /* Preserve drafts verbatim instead of changing optional behavior. */ }
    }

    /// <summary>
    /// Called by the local rollback helper with its fixed, verified account profile path.
    /// Preserve every original byte before projecting the three additions out of a down-level profile.
    /// </summary>
    internal static Preparation Prepare(string configPath, Action<string, byte[]>? write = null)
    {
        write ??= SecureFile.WriteAllBytesRestricted;
        Preparation? result = null;
        SecureFile.WithPathLock(configPath, () =>
        {
            if (!File.Exists(configPath)) { result = new(configPath, null, null, null, write); return; }
            var original = File.ReadAllBytes(configPath);
            // StreamReader detects legacy UTF encodings without changing the preserved original.
            using var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var root = JsonNode.Parse(reader.ReadToEnd())?.AsObject() ?? throw new JsonException("Settings must be an object");
            var changed = root.Remove("dataStewardship") | root.Remove("recovery");
            if (root["controlApi"] is JsonObject control) changed |= control.Remove("security");
            if (!changed) { result = new(configPath, null, null, null, write); return; }
            var backup = configPath + "." + Guid.NewGuid().ToString("N") + ".pre-downgrade";
            write(backup, original);
            if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original))
                throw new IOException("The settings recovery copy could not be verified");
            var projected = Encoding.UTF8.GetBytes(root.ToJsonString(Json));
            write(configPath, projected);
            result = new(configPath, backup, original, projected, write);
        });
        return result!;
    }

    internal sealed class Preparation(string path, string? backupPath, byte[]? original, byte[]? projected, Action<string, byte[]> write)
    {
        internal string? BackupPath => backupPath;

        internal void Restore()
        {
            if (original == null || projected == null) return;
            SecureFile.WithPathLock(path, () =>
            {
                // Never undo a subsequent settings edit made while recovery was running.
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(projected))
                    throw new IOException("Settings changed after rollback preparation; the original is retained in its recovery copy");
                write(path, original);
            });
        }
    }
}
