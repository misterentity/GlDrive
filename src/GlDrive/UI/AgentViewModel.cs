using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.AiAgent;
using GlDrive.Config;
using System.Text.Json;

namespace GlDrive.UI;

public sealed class AgentViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    private string _briefMarkdown = "";
    public string BriefMarkdown { get => _briefMarkdown; set { _briefMarkdown = value; Raise(); } }

    public ObservableCollection<AuditRow> AuditRows { get; } = new();
    public ObservableCollection<AuditRow> Suggestions { get; } = new();
    public ObservableCollection<FreezeEntry> Frozen { get; } = new();

    private string _memo = "";
    public string Memo { get => _memo; set { _memo = value; Raise(); } }

    public ICommand RunNowCommand { get; }
    public ICommand UndoAuditRowCommand { get; }
    public ICommand ApplyAnywayCommand { get; }
    public ICommand DismissSuggestionCommand { get; }
    public ICommand UnfreezeCommand { get; }
    public ICommand SaveMemoCommand { get; }
    public ICommand RefreshCommand { get; }

    public AgentViewModel()
    {
        RunNowCommand = new RelayCommand(async () =>
        {
            try { if (App.AgentRunner != null) await App.AgentRunner.RunNowAsync(); }
            catch (Exception ex) { MessageBox.Show("Run failed: " + ex.Message); }
            Refresh();
        });
        UndoAuditRowCommand = new RelayCommand<AuditRow>(UndoRow);
        ApplyAnywayCommand = new RelayCommand<AuditRow>(ApplyAnyway);
        DismissSuggestionCommand = new RelayCommand<AuditRow>(Dismiss);
        UnfreezeCommand = new RelayCommand<FreezeEntry>(e =>
        {
            if (e != null) App.FreezeStore?.Unfreeze(e.Path);
            RefreshFrozen();
        });
        SaveMemoCommand = new RelayCommand(() =>
        {
            App.AgentMemo?.Save(Memo);
            MessageBox.Show("Memo saved. Agent will see this as ground truth next run.");
        });
        RefreshCommand = new RelayCommand(Refresh);

        Refresh();
    }

    public void Refresh()
    {
        RefreshBrief();
        RefreshAudit();
        RefreshSuggestions();
        RefreshFrozen();
        RefreshMemo();
    }

    private void RefreshBrief()
    {
        try
        {
            var dir = Path.Combine(
                GlDrive.Util.AppDataPaths.Root, "ai-data", "ai-briefs");
            var latest = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").OrderByDescending(f => f).FirstOrDefault()
                : null;
            BriefMarkdown = latest != null
                ? File.ReadAllText(latest)
                : "_No brief yet. The agent hasn't run — enable it in Settings and wait for the scheduled hour, or click Run now._";
        }
        catch (Exception ex) { BriefMarkdown = $"Error loading brief: {ex.Message}"; }
    }

    private void RefreshAudit()
    {
        AuditRows.Clear();
        if (App.AuditTrail is null) return;
        foreach (var r in App.AuditTrail.ReadAll().Where(r => r.WasApplied).Reverse().Take(500))
            AuditRows.Add(r);
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (App.AuditTrail is null) return;
        foreach (var r in App.AuditTrail.ReadAll()
                    .Where(r => r.IsSuggestion)
                    .Reverse().Take(500))
            Suggestions.Add(r);
    }

    private void RefreshFrozen()
    {
        Frozen.Clear();
        if (App.FreezeStore is null) return;
        foreach (var e in App.FreezeStore.All) Frozen.Add(e);
    }

    private void RefreshMemo()
    {
        if (App.AgentMemo is null) return;
        Memo = App.AgentMemo.Load();
    }

    private async Task UndoRow(AuditRow row)
    {
        if (!row.WasApplied || row.Undone) return;
        try
        {
            var inverse = new AgentChange
            {
                Category = row.Category,
                Target = row.Target,
                Before = row.After,
                After = row.Before,
                Reasoning = "User-initiated undo",
                EvidenceRef = "user-undo",
                Confidence = 1.0
            };
            var (baseline, cfg) = ReadCandidate();
            var pending = new List<AuditRow>();
            var undoRunId = "undo-" + Guid.NewGuid().ToString()[..8];
            var report = AgentUndoCompletion.ApplyInverse(row,
                () => App.ChangeApplier?.Apply(new[] { inverse }, cfg, cfg.Agent, undoRunId, dryRun: false, pending.Add, explicitUndo: true));
            if (report is { Applied: 1, Rejected: 0 })
                await CommitChanges(baseline, cfg, pending);
            else
                foreach (var rejected in pending) App.AuditTrail?.Append(rejected);
            if (!AgentUndoCompletion.TryComplete(report,
                () => { }, // already persisted and published before marking the original
                () => App.AuditTrail?.MarkUndone(row.RunId, row.Target, "user-click"),
                () => App.TelemetryRecorder?.Record(TelemetryStream.Overrides, new ConfigOverrideEvent
                {
                    JsonPointer = row.Target,
                    BeforeValue = ConfigSecretPointers.MaskValue(row.Target, row.After?.ToString()),
                    AfterValue = ConfigSecretPointers.MaskValue(row.Target, row.Before?.ToString()),
                    AiAuditRef = row.RunId
                }), out var failure))
                MessageBox.Show(failure, "Undo not applied");
            RefreshAudit();
        }
        catch (Exception ex) { MessageBox.Show("Undo failed: " + ex.Message); }
    }

    private async Task ApplyAnyway(AuditRow row)
    {
        if (!row.IsSuggestion) return;
        try
        {
            var change = new AgentChange
            {
                Category = row.Category,
                Target = row.Target,
                Before = row.Before,
                After = row.After,
                Reasoning = row.Reasoning + " [user apply-anyway]",
                EvidenceRef = row.EvidenceRef,
                Confidence = 1.0  // force through confidence gate
            };
            var (baseline, cfg) = ReadCandidate();
            var pending = new List<AuditRow>();
            var report = App.ChangeApplier?.Apply(new[] { change }, cfg, cfg.Agent,
                "manual-" + Guid.NewGuid().ToString()[..8], dryRun: false, pending.Add);
            if (report is { Applied: 1, Rejected: 0 })
            {
                await CommitChanges(baseline, cfg, pending);
                App.AuditTrail?.Dismiss(row, "applied-manually");
            }
            else
            {
                foreach (var rejected in pending) App.AuditTrail?.Append(rejected);
                MessageBox.Show(report == null ? "The AI change service is unavailable."
                    : "The change was not applied: " + string.Join(", ", report.RejectionByReason.Keys), "Change not applied");
            }
            RefreshAudit();
            RefreshSuggestions();
        }
        catch (Exception ex) { MessageBox.Show("Apply-anyway failed: " + ex.Message); }
    }

    private void Dismiss(AuditRow row)
    {
        App.AuditTrail?.Dismiss(row);
        RefreshSuggestions();
    }

    private static (string Baseline, AppConfig Candidate) ReadCandidate()
    {
        var baseline = File.ReadAllText(ConfigManager.ConfigPath);
        var candidate = JsonSerializer.Deserialize<AppConfig>(baseline,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new IOException("Configuration is null");
        return (baseline, candidate);
    }

    private static Task CommitChanges(string baseline, AppConfig candidate, List<AuditRow> rows)
        => Application.Current is App app ? app.CommitAgentChangesAsync(baseline, candidate, rows)
            : throw new InvalidOperationException("The running application is unavailable.");
}

// Shared by single-row and batch undo: rejected inverses must not erase undo history,
// persist a failed proposal, or emit an override event claiming the value changed.
internal static class AgentUndoCompletion
{
    internal static ChangeApplier.RunReport? ApplyInverse(AuditRow row,
        Func<ChangeApplier.RunReport?> apply)
    {
        string? rejection = null;
        if (!row.WasApplied || row.Undone)
            rejection = "only applied changes can be undone; previews did not change settings";
        else if (row.Target.EndsWith("/-", StringComparison.Ordinal))
            rejection = "appended entries require manual removal or a configuration snapshot restore";
        else if (row.Category == AgentCategories.Skiplist
            && (row.After is null || row.After is System.Text.Json.JsonElement
                { ValueKind: System.Text.Json.JsonValueKind.Null })
            && int.TryParse(row.Target.Split('/').LastOrDefault(), out var index) && index >= 0)
            rejection = "removed rules require manual restoration or a configuration snapshot restore; " +
                "their former position may now contain another rule";

        if (rejection is null) return apply();
        var report = new ChangeApplier.RunReport { Rejected = 1 };
        report.RejectionByReason[rejection] = 1;
        return report;
    }

    internal static bool TryComplete(ChangeApplier.RunReport? report, Action saveConfig,
        Action markUndone, Action? recordOverride, out string failure)
    {
        if (report is not { Applied: 1, Rejected: 0 })
        {
            var reasons = report?.RejectionByReason.Keys.ToArray() ?? Array.Empty<string>();
            failure = report is null ? "The AI change service is unavailable."
                : "The change could not be reverted: " +
                    (reasons.Length > 0 ? string.Join(", ", reasons) : "no change was applied") +
                    ". The original audit entry remains available.";
            return false;
        }

        // Persist first. A failed save must leave the original entry eligible for retry.
        saveConfig();
        markUndone();
        recordOverride?.Invoke();
        failure = "";
        return true;
    }
}
