using System.Windows;

namespace GlDrive.UI;

public partial class RevertRunsDialog : Window
{
    public sealed class RunOption
    {
        public string RunId { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public List<string> SelectedRunIds { get; } = new();

    public RevertRunsDialog()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        var audit = App.AuditTrail;
        if (audit is null) return;
        var runs = audit.ReadAll()
            .Where(r => r.Applied && !r.Undone)
            .GroupBy(r => r.RunId)
            .OrderByDescending(g => g.First().Ts)
            .Take(30)
            .Select(g => new RunOption
            {
                RunId = g.Key,
                Label = $"{g.First().Ts}  run {g.Key[..Math.Min(8, g.Key.Length)]}  ({g.Count()} change(s))"
            })
            .ToList();
        RunList.ItemsSource = runs;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in RunList.SelectedItems.Cast<RunOption>()) SelectedRunIds.Add(item.RunId);
        DialogResult = true;
        Close();
    }
}
