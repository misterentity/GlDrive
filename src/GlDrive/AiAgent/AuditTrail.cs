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

/// <summary>
/// Stub. Full implementation (append to jsonl, ReadAll, MarkUndone) lands in Task 8.1.
/// ChangeApplier calls Append; for now we just log at Debug so the dispatcher is testable
/// ahead of persistence.
/// </summary>
public class AuditTrail
{
    public virtual void Append(AuditRow row)
    {
        Serilog.Log.Debug("AUDIT [stub]: {Category} target={Target} applied={Applied} reason={Reason} confidence={Confidence:F2}",
            row.Category, row.Target, row.Applied, row.RejectionReason ?? "-", row.Confidence);
    }
}
