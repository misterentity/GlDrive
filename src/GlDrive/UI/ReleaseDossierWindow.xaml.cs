using System.Windows;
using System.Windows.Controls;
using GlDrive.Services.Operations;

namespace GlDrive.UI;

public partial class ReleaseDossierWindow : Window
{
    private readonly ReleaseDossierService _service;
    private CancellationTokenSource? _operation;
    private bool _closed;

    public ReleaseDossierWindow(ReleaseDossierService service, string? release = null, string? section = null)
    {
        _service = service;
        InitializeComponent();
        ReleaseBox.Text = release ?? "";
        SectionBox.Text = section ?? "";
        Closed += (_, _) =>
        {
            _closed = true; _operation?.Cancel();
            TimelineGrid.ItemsSource = PredictionsGrid.ItemsSource = null;
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null || _closed) return;
        var release = ReleaseBox.Text.Trim();
        if (release.Length == 0) { StatusText.Text = "Enter an exact release name."; return; }
        var section = SectionBox.Text.Trim();
        var days = DaysBox.SelectedItem is ComboBoxItem { Tag: string value } && int.TryParse(value, out var parsed) ? parsed : 30;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        StatusText.Text = "Reading local release observations…";
        try
        {
            Task<ReleaseDossier> work;
            using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
                work = Task.Run(() => _service.ReadAsync(release, section.Length == 0 ? null : section, days, cancellation.Token), cancellation.Token);
            var dossier = await work;
            if (_closed) return;
            TimelineGrid.ItemsSource = dossier.Timeline;
            PredictionsGrid.ItemsSource = dossier.Predictions;
            TimelineGrid.Items.Refresh(); PredictionsGrid.Items.Refresh();
            var coverage = dossier.Coverage;
            StatusText.Text = $"{dossier.Timeline.Count:N0} observed events · {coverage.FilesRead:N0} files · {coverage.RowsRead:N0} rows read. " +
                (coverage.Partial ? "Partial evidence: " + string.Join(", ", coverage.Limits) + ". " : "") +
                "Not observed means unknown, not absent. Predictions perform no operations.\n" +
                string.Join(" · ", coverage.Streams.Select(stream => $"{stream.Stream}: {stream.RowsInWindow:N0} rows" +
                    (stream.Partial ? " (incomplete: " + string.Join(", ", stream.Limits) + ")" : "")));
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Read cancelled. The previous view is retained."; }
        catch { if (!_closed) StatusText.Text = "The dossier could not be read. The previous view is retained."; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        ReleaseBox.IsEnabled = SectionBox.IsEnabled = DaysBox.IsEnabled = RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
}
