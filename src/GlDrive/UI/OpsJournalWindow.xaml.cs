using System.Windows;
using System.Windows.Controls;
using GlDrive.Services.Operations;

namespace GlDrive.UI;

public partial class OpsJournalWindow : Window
{
    private readonly OpsJournal _journal;
    private CancellationTokenSource? _operation;
    private bool _closed;

    public OpsJournalWindow(OpsJournal journal)
    {
        _journal = journal;
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true; _operation?.Cancel();
            TimelineGrid.ItemsSource = IncidentsGrid.ItemsSource = IncidentEventsGrid.ItemsSource = null;
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null || _closed) return;
        var server = ServerBox.Text.Trim();
        var days = DaysBox.SelectedItem is ComboBoxItem { Tag: string value } && int.TryParse(value, out var parsed) ? parsed : 7;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        StatusText.Text = "Reading retained operations…";
        try
        {
            Task<(IReadOnlyList<OpsEvent> Timeline, IReadOnlyList<OpsIncident> Incidents)> work;
            using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
                work = Task.Run(async () =>
                {
                    var timeline = await _journal.ReadTimelineAsync(server.Length == 0 ? null : server, days, 500, cancellation.Token).ConfigureAwait(false);
                    var incidents = await _journal.ReadIncidentsAsync(server.Length == 0 ? null : server, days, 100, cancellation.Token).ConfigureAwait(false);
                    return (timeline, incidents);
                }, cancellation.Token);
            var result = await work;
            if (_closed) return;
            TimelineGrid.ItemsSource = result.Timeline;
            IncidentsGrid.ItemsSource = result.Incidents;
            IncidentEventsGrid.ItemsSource = null;
            TimelineGrid.Items.Refresh(); IncidentsGrid.Items.Refresh();
            var limits = (result.Timeline as IOpsReadCoverage)?.Limits ?? [];
            var incidentLimits = (result.Incidents as IOpsReadCoverage)?.Limits ?? [];
            var partial = (result.Timeline as IOpsReadCoverage)?.Partial == true || (result.Incidents as IOpsReadCoverage)?.Partial == true;
            StatusText.Text = $"{result.Timeline.Count:N0} events (newest 500 maximum) · {result.Incidents.Count:N0} incident groups (newest 100 maximum). " +
                $"Queue evictions: {_journal.Dropped:N0}. " +
                (partial ? "Partial read: " + string.Join(", ", limits.Concat(incidentLimits).Distinct(StringComparer.Ordinal)) + ". " : "") +
                (_journal.PersistenceHealthy ? "" : "Persistence unavailable; recent facts are retained in bounded memory. ") +
                "Missing events are not proof that an operation did not occur.";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Read cancelled. The previous view is retained."; }
        catch { if (!_closed) StatusText.Text = "The journal could not be read. The previous view is retained."; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private void IncidentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IncidentEventsGrid != null)
            IncidentEventsGrid.ItemsSource = IncidentsGrid.SelectedItem is OpsIncident incident
                ? incident.PrecedingChanges.Concat(incident.Events).Distinct().OrderBy(row => row.Time).ToArray()
                : null;
    }
    private void SetBusy(bool busy)
    {
        ServerBox.IsEnabled = DaysBox.IsEnabled = RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
}
