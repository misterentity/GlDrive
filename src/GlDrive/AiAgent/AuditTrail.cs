using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public class AuditRow
{
    public string Ts { get; set; } = DateTime.UtcNow.ToString("O");
    public string RunId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Target { get; set; } = "";
    public object? Before { get; set; }
    public object? After { get; set; }
    public string Reasoning { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public double Confidence { get; set; }
    public bool Applied { get; set; }
    public bool DryRun { get; set; }
    public string? RejectionReason { get; set; }
    public bool Undone { get; set; }
    public string? UndoneAt { get; set; }
    public string? UndoneReason { get; set; }
}

public class AuditTrail
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AuditTrail(string aiDataRoot)
    {
        Directory.CreateDirectory(aiDataRoot);
        _path = Path.Combine(aiDataRoot, "ai-audit.jsonl");
    }

    // Parameterless constructor kept for compatibility with any code that still
    // used the stub. Can be removed once all callers pass aiDataRoot explicitly.
    public AuditTrail() : this(
        Path.Combine(GlDrive.Config.ConfigManager.AppDataPath, "ai-data")) { }

    public virtual void Append(AuditRow row)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_path,
                    JsonSerializer.Serialize(row, JsonOpts) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { Log.Warning(ex, "AuditTrail append failed"); }
    }

    public IEnumerable<AuditRow> ReadAll()
    {
        if (!File.Exists(_path)) yield break;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditRow? row = null;
            try { row = JsonSerializer.Deserialize<AuditRow>(line, JsonOpts); }
            catch { continue; }
            if (row != null) yield return row;
        }
    }

    /// <summary>Marks all applied rows matching (runId, target) as undone. Rewrites the whole file.</summary>
    public void MarkUndone(string runId, string target, string reason)
    {
        lock (_lock)
        {
            var rows = ReadAll().ToList();
            var updated = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].RunId == runId && rows[i].Target == target && rows[i].Applied && !rows[i].Undone)
                {
                    rows[i].Undone = true;
                    rows[i].UndoneAt = DateTime.UtcNow.ToString("O");
                    rows[i].UndoneReason = reason;
                    updated = true;
                }
            }
            if (!updated) return;
            var sb = new StringBuilder();
            foreach (var r in rows) sb.AppendLine(JsonSerializer.Serialize(r, JsonOpts));
            File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
        }
    }
}
