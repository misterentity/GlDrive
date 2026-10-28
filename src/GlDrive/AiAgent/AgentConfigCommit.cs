using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    {
        var baseline = JsonSerializer.Deserialize<AppConfig>(expected, Options)
            ?? throw new IOException("Configuration is null");
        if (JsonSerializer.Serialize(live, Options) != JsonSerializer.Serialize(baseline, Options))
            throw new IOException("Settings have unsaved changes; AI proposals were discarded to preserve your edits.");
        Commit(configPath, expected, candidate, rows, root, save, audit,
            value => ConfigReconciler.Apply(live, value));
    }

    internal static void Recover(string configPath, string root, AuditTrail audit)
    {
        SecureFile.WithPathLock(configPath, () =>
        {
            var path = Path.Combine(root, "pending-config-commit.json");
            if (!File.Exists(path)) return;
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(path))
                ?? throw new IOException("Unreadable pending AI commit; restore it before running the agent.");
            var current = Hash(File.ReadAllText(configPath));
            if (current == pending.AfterHash) audit.AppendBatchRequired(pending.Rows);
            else if (current != pending.BeforeHash)
                throw new IOException("Pending AI commit conflicts with current settings; retained for recovery.");
            File.Delete(path);
        });
    }

    internal static void Commit(string configPath, string expected, AppConfig candidate,
        List<AuditRow> rows, string root, Action<AppConfig> save, AuditTrail audit,
        Action<AppConfig>? publish = null)
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
            var next = JsonSerializer.Serialize(candidate, Options);
            foreach (var row in rows) row.Id ??= Guid.NewGuid().ToString("N");
            var journal = Path.Combine(root, "pending-config-commit.json");
            SecureFile.WriteAllTextRestricted(journal, JsonSerializer.Serialize(new Pending(Hash(expected), Hash(next), rows)));
            save(candidate);
            if (Hash(File.ReadAllText(configPath)) != Hash(next))
                throw new IOException("AI settings were not persisted; commit evidence retained.");
            publish?.Invoke(candidate);
            audit.AppendBatchRequired(rows);
            File.Delete(journal);
        });
    }
}
