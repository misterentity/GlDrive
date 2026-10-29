using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Services.Statistics;

namespace GlDrive.UI;

public partial class StatisticsWindow : Window
{
    private readonly StatisticsService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _working;
    private bool _closed;
    private bool _lifetimeDisposed;

    public StatisticsWindow(StatisticsService service)
    {
        _service = service;
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            TrafficGrid.ItemsSource = RoutesGrid.ItemsSource = HealthGrid.ItemsSource = LatencyGrid.ItemsSource = null;
            DisposeLifetimeIfClosed();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _closed) return;
        var days = DaysBox.SelectedItem is ComboBoxItem { Tag: string value } && int.TryParse(value, out var parsed) ? parsed : 30;
        SetWorking(true);
        StatusText.Text = "Reading the selected telemetry window…";
        try
        {
            Task<StatisticsSummary> read;
            using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
                read = Task.Run(() => _service.ReadAsync(days, _lifetime.Token), _lifetime.Token);
            var summary = await read;
            if (!_closed) Render(summary);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch { if (!_closed) StatusText.Text = "Statistics could not be read. Choose Refresh to try again; the previous view is retained."; }
        finally { SetWorking(false); }
    }

    private void Render(StatisticsSummary summary)
    {
        WindowText.Text = $"{summary.FromUtc:yyyy-MM-dd HH:mm} — {summary.ToUtc:yyyy-MM-dd HH:mm} UTC";
        var races = summary.Races;
        RaceText.Text = $"{races.Observed:N0} observed races · {races.Completed:N0} completed";
        CleanText.Text = $"Clean rate: {Percent(races.CleanRate)} · {races.Clean:N0} clean of {races.CleanEvidenceSamples:N0} classified races";
        LatencyGrid.ItemsSource = new[]
        {
            new TimingRow("Queue wait", races.QueueWaitSamples, Seconds(races.QueueWaitMedianSeconds), Seconds(races.QueueWaitP95Seconds)),
            new TimingRow("Announce to start", races.AnnounceLagSamples, Seconds(races.AnnounceLagMedianSeconds), Seconds(races.AnnounceLagP95Seconds))
        };
        var downloads = summary.Downloads;
        DownloadsText.Text = $"Success rate: {Percent(downloads.SuccessRate)} · {downloads.Completed:N0} completed · {downloads.Failed:N0} failed recorded outcomes";
        if (downloads.Partial) DownloadsText.Text += " · Incomplete download evidence; window totals are unknown.";
        var coverage = summary.Coverage;
        CoverageText.Text = $"{coverage.FilesRead:N0} files · {coverage.BytesRead / (1024d * 1024):N2} MiB read · {coverage.RowsRead:N0} rows read · {coverage.RowsInWindow:N0} rows in the window\n" +
            $"{coverage.InvalidRows:N0} invalid rows · {coverage.OversizedRows:N0} oversized rows\n" +
            string.Join("\n", coverage.Streams.Select(stream => $"{stream.Stream}: {stream.RowsInWindow:N0} rows · " +
                $"{stream.OldestObservationUtc:yyyy-MM-dd} — {stream.NewestObservationUtc:yyyy-MM-dd} UTC · " +
                (stream.Partial ? "Incomplete: " + string.Join(", ", stream.Limits) : "Available evidence read")));
        LimitsText.Text = coverage.Limits.Count == 0 ? "No read limit was reached." : "Read limits: " + string.Join("; ", coverage.Limits);
        NotesText.Text = string.Join(Environment.NewLine, summary.Notes);
        TrafficGrid.ItemsSource = summary.Traffic;
        RoutesGrid.ItemsSource = summary.Routes;
        HealthGrid.ItemsSource = summary.Health;
        TrafficGrid.Items.Refresh();
        RoutesGrid.Items.Refresh();
        HealthGrid.Items.Refresh();
        LatencyGrid.Items.Refresh();
        StatusText.Text = coverage.Partial
            ? "Partial read: some evidence was unavailable or a limit was reached. Figures describe the evidence read; review coverage in Summary."
            : "Read complete for the available evidence. Missing observations are not counted as zero or as uptime.";
    }

    private static string Percent(double? value) => value is { } number ? number.ToString("P1", CultureInfo.CurrentCulture) : "Unknown";
    private static string Seconds(double? value) => value is { } number ? number.ToString("N1", CultureInfo.CurrentCulture) + " s" : "Unknown";
    private sealed record TimingRow(string Name, int Samples, string Median, string P95);

    private void SetWorking(bool working)
    {
        _working = working;
        if (!_closed) RefreshButton.IsEnabled = DaysBox.IsEnabled = !working;
        DisposeLifetimeIfClosed();
    }

    private void DisposeLifetimeIfClosed()
    {
        if (!_closed || _working || _lifetimeDisposed) return;
        _lifetimeDisposed = true;
        _lifetime.Dispose();
    }
}
