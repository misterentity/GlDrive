using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.AiAgent;

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
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", "ai-data", "ai-briefs");
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
        foreach (var r in App.AuditTrail.ReadAll().Where(r => r.Applied).Reverse().Take(500))
            AuditRows.Add(r);
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (App.AuditTrail is null) return;
        foreach (var r in App.AuditTrail.ReadAll()
                    .Where(r => !r.Applied && r.RejectionReason != "frozen" && !r.Undone)
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

    private void UndoRow(AuditRow row)
    {
        if (!row.Applied || row.Undone) return;
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
            var cfg = GlDrive.Config.ConfigManager.Load();
            var undoRunId = "undo-" + Guid.NewGuid().ToString()[..8];
            App.ChangeApplier?.Apply(new[] { inverse }, cfg, cfg.Agent, undoRunId, dryRun: false);
            GlDrive.Config.ConfigManager.Save(cfg);
            App.AuditTrail?.MarkUndone(row.RunId, row.Target, "user-click");
            RefreshAudit();

            App.TelemetryRecorder?.Record(TelemetryStream.Overrides, new ConfigOverrideEvent
            {
                JsonPointer = row.Target,
                BeforeValue = row.After?.ToString(),
                AfterValue = row.Before?.ToString(),
                AiAuditRef = row.RunId
            });
        }
        catch (Exception ex) { MessageBox.Show("Undo failed: " + ex.Message); }
    }

    private void ApplyAnyway(AuditRow row)
    {
        if (row.Applied) return;
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
            var cfg = GlDrive.Config.ConfigManager.Load();
            App.ChangeApplier?.Apply(new[] { change }, cfg, cfg.Agent,
                "manual-" + Guid.NewGuid().ToString()[..8], dryRun: false);
            GlDrive.Config.ConfigManager.Save(cfg);
            RefreshAudit();
            RefreshSuggestions();
        }
        catch (Exception ex) { MessageBox.Show("Apply-anyway failed: " + ex.Message); }
    }

    private void Dismiss(AuditRow row)
    {
        App.AuditTrail?.MarkUndone(row.RunId, row.Target, "user-dismiss");
        RefreshSuggestions();
    }
}
