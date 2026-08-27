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

    /// <summary>
    /// BOM-free UTF-8. <c>Encoding.UTF8</c> is UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
    /// so handing it to File.AppendAllText/WriteAllText stamps a BOM at position 0 — the same
    /// defect fixed in <see cref="TelemetryRecorder"/> for v3.10.80, which was left in place here
    /// because only the writer half of that subsystem was swept. Readers strip a leading BOM, so
    /// it was survivable, but any consumer reading raw bytes (jq, a digester, an external tool)
    /// chokes on line 1.
    /// </summary>
    internal static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Rows this process could not parse. Non-zero means the audit trail is degraded.</summary>
    public int ParseSkips { get; private set; }

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
                    JsonSerializer.Serialize(row, JsonOpts) + "\n", FileEncoding);
        }
        catch (Exception ex) { Log.Warning(ex, "AuditTrail append failed"); }
    }

    public IEnumerable<AuditRow> ReadAll()
    {
        if (!File.Exists(_path)) yield break;
        var skips = 0;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditRow? row = null;
            try { row = JsonSerializer.Deserialize<AuditRow>(line, JsonOpts); }
            catch (Exception ex)
            {
                // Skip the row, not the stream — but COUNT it and report at Warning.
                // The Serilog sink runs at Information, so a Debug line here would be
                // invisible; that blind spot is exactly how the 2026-08-14 poison row
                // hid for 40+ agent runs.
                skips++;
                Log.Warning(ex, "AuditTrail parse skip in {Path}", _path);
                continue;
            }
            if (row != null) yield return row;
        }
        ParseSkips = skips;
        if (skips > 0)
            Log.Warning("AuditTrail: {Skips} unparseable row(s) in {Path} — undo history is incomplete",
                skips, _path);
    }

    /// <summary>
    /// Marks all applied rows matching (runId, target) as undone. Rewrites the whole file.
    /// Unparseable rows are preserved VERBATIM: this rewrite is the only destructive operation
    /// on the audit trail, and dropping a row it merely failed to deserialize would erase undo
    /// evidence permanently. Rows that parse but do not match are also written back verbatim so
    /// a rewrite never reserializes untouched history.
    /// </summary>
    public void MarkUndone(string runId, string target, string reason)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return;
            var lines = File.ReadAllLines(_path);
            var output = new List<string>(lines.Length);
            var updated = false;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                AuditRow? row = null;
                try { row = JsonSerializer.Deserialize<AuditRow>(line, JsonOpts); }
                catch { /* fall through — preserved verbatim below */ }

                if (row is not null && row.RunId == runId && row.Target == target
                    && row.Applied && !row.Undone)
                {
                    row.Undone = true;
                    row.UndoneAt = DateTime.UtcNow.ToString("O");
                    row.UndoneReason = reason;
                    output.Add(JsonSerializer.Serialize(row, JsonOpts));
                    updated = true;
                }
                else
                {
                    output.Add(line);
                }
            }

            if (!updated) return;
            // "\n" to match Append; StringBuilder.AppendLine would emit CRLF and leave
            // the file with mixed line endings after the first undo.
            File.WriteAllText(_path, string.Join("\n", output) + "\n", FileEncoding);
        }
    }
}
