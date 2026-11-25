using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Util;

namespace GlDrive.AiAgent;

internal static class AgentConfigCommit
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal sealed record Pending(string BeforeHash, string AfterHash, List<AuditRow> Rows);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Called on the dispatcher, before taking the file lock: readers/savers on
    // that dispatcher cannot observe the gap between disk commit and publication.
    internal static void CommitAndPublish(string configPath, string expected, AppConfig candidate,
        List<AuditRow> rows, string root, Action<AppConfig> save, AuditTrail audit, AppConfig live)
        => Commit(configPath, expected, candidate, rows, root, save, audit, live);

    internal static void Recover(string configPath, string root, AuditTrail audit)
    {
        SecureFile.WithPathLock(configPath, () =>
        {
            var path = Path.Combine(root, "pending-config-commit.json");
            if (!File.Exists(path)) return;
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(path))
                ?? throw new IOException("Unreadable pending AI commit; restore it before running the agent.");
            var current = File.ReadAllText(configPath);
            if (MatchesCommittedSettings(current, pending.AfterHash)) audit.AppendBatchRequired(pending.Rows);
            else if (Hash(current) != pending.BeforeHash)
                throw new IOException("Pending AI commit conflicts with current settings; retained for recovery.");
            File.Delete(path);
        });
    }

    internal static void Commit(string configPath, string expected, AppConfig candidate,
        List<AuditRow> rows, string root, Action<AppConfig> save, AuditTrail audit,
        AppConfig? live = null)
    {
        var runId = rows.FirstOrDefault()?.RunId ?? "maintenance";
        using var attribution = ConfigWriteScope.Current.Actor == "mcp:agent_run"
            ? ConfigWriteScope.Begin("agent:" + runId, runId)
            : ConfigWriteScope.Fallback("agent:" + runId, runId);
        SecureFile.WithPathLock(configPath, () =>
        {
            Recover(configPath, root, audit);
            if (File.ReadAllText(configPath) != expected)
                throw new IOException("Settings changed during the AI run; proposals were discarded to preserve your edits.");
            var typed = JsonSerializer.Serialize(candidate, Options);
            foreach (var row in rows) row.Id ??= Guid.NewGuid().ToString("N");
            if (live != null)
            {
                var baseline = JsonSerializer.Deserialize<AppConfig>(expected, Options)
                    ?? throw new IOException("Configuration is null");
                var before = JsonSerializer.SerializeToNode(baseline, Options)!.AsObject();
                // Rejected proposals and previews can be audited without publishing settings.
                // A dry-run counter change still requires a real settings transaction.
                if (!rows.Any(row => row.WasApplied) && JsonNode.DeepEquals(before, JsonNode.Parse(typed)))
                {
                    audit.AppendBatchRequired(rows);
                    return;
                }

                var current = JsonSerializer.SerializeToNode(live, Options)!.AsObject();
                var conflicts = before.Where(section => !JsonNode.DeepEquals(section.Value, current[section.Key]))
                    .Select(section => section.Key).ToArray();
                if (conflicts.Length > 0)
                    throw new IOException($"Settings have unsaved changes in {string.Join(", ", conflicts)}; AI proposals were discarded to preserve your edits.");
            }
            var next = ConfigManager.PrepareSerializedSettings(typed, expected);
            var journal = Path.Combine(root, "pending-config-commit.json");
            SecureFile.WriteAllTextRestricted(journal, JsonSerializer.Serialize(new Pending(Hash(expected), Hash(next), rows)));
            save(candidate);
            if (Hash(File.ReadAllText(configPath)) != Hash(next))
                throw new IOException("AI settings were not persisted; commit evidence retained.");
            if (live != null) ConfigReconciler.Apply(live, candidate);
            audit.AppendBatchRequired(rows);
            File.Delete(journal);
        });
    }

    private static bool MatchesCommittedSettings(string current, string expectedHash)
    {
        if (Hash(current) == expectedHash) return true;
        // v3.19.0 journals hashed the full typed candidate before Save omitted default
        // sections and retained hook formatting. Recover those already-saved commits only
        // when rebuilding that exact candidate proves its old hash. Also compare the whole
        // persisted document, so unknown fields or intervening edits cannot disappear in
        // typed deserialization and be mistaken for a successful commit.
        try
        {
            var candidate = JsonSerializer.Deserialize<AppConfig>(current, Options);
            if (candidate == null) return false;
            var typed = JsonSerializer.Serialize(candidate, Options);
            return Hash(typed) == expectedHash && JsonNode.DeepEquals(JsonNode.Parse(current),
                JsonNode.Parse(ConfigManager.PrepareSerializedSettings(typed, current)));
        }
        catch (JsonException) { return false; }
    }
}
