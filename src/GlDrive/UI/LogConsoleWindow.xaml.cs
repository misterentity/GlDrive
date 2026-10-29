using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Logging;
using Microsoft.Win32;
using Serilog.Events;

namespace GlDrive.UI;

public partial class LogConsoleWindow : Window
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private RecordedLog[] _rows = [];
    private bool _ready;
    private bool _closed;
    private bool _refreshing;
    private bool _working;
    private int _filterVersion;

    public LogConsoleWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        LevelBox.ItemsSource = Enum.GetValues<LogEventLevel>();
        LevelBox.SelectedItem = LogEventLevel.Information;
        ComponentBox.ItemsSource = new[] { "All" }.Concat(ComponentTraceSwitches.Components).ToArray();
        ComponentBox.SelectedIndex = 0;
        TraceComponentBox.ItemsSource = ComponentTraceSwitches.Components;
        TraceComponentBox.SelectedIndex = 0;
        using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => { if (FollowBox.IsChecked == true) await RefreshAsync(); };
            _timer.Start();
        }
        _ready = true;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _timer.Stop(); _rows = []; LogsGrid.ItemsSource = null; };
    }

    private async Task RefreshAsync()
    {
        if (!_ready || _closed || _refreshing || _working) return;
        if (SerilogSetup.Recorder is not { } recorder) { StatusLabel.Text = "The flight recorder is unavailable."; return; }
        _refreshing = true;
        var minimum = LevelBox.SelectedItem is LogEventLevel level ? level : LogEventLevel.Information;
        var component = ComponentBox.SelectedItem as string ?? "All";
        var text = FilterBox.Text;
        var version = _filterVersion;
        try
        {
            var rows = await RunBackground(() => recorder.Snapshot(minimum, component == "All" ? "" : component, text, 500));
            if (_closed || version != _filterVersion) return;
            _rows = rows;
            LogsGrid.ItemsSource = rows;
            if (FollowBox.IsChecked == true && rows.Length > 0) LogsGrid.ScrollIntoView(rows[^1]);
            StatusLabel.Text = $"{rows.Length} recent matching entries (up to 500). {recorder.Dropped} pending entries evicted during bursts.";
        }
        catch { if (!_closed) StatusLabel.Text = "Recent logs could not be read. Recording and engine activity continue."; }
        finally
        {
            _refreshing = false;
            if (!_closed && version != _filterVersion) await RefreshAsync();
        }
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { _filterVersion++; await RefreshAsync(); }
    private async void FilterText_Changed(object sender, TextChangedEventArgs e) { _filterVersion++; await RefreshAsync(); }
    private async void Follow_Changed(object sender, RoutedEventArgs e) { if (_ready && FollowBox.IsChecked == true) await RefreshAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_working || SerilogSetup.Recorder is not { } recorder) return;
        SetWorking(true);
        try
        {
            var path = await BackgroundAsync(() => recorder.CaptureAsync("manual"));
            if (!_closed) StatusLabel.Text = path == null ? "No capsule saved: capture is rate-limited or storage is unavailable." : "Redacted incident capsule saved.";
        }
        catch { if (!_closed) StatusLabel.Text = "The incident capsule could not be saved. Recent detail remains in memory."; }
        finally { SetWorking(false); }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_working || SerilogSetup.Recorder is not { } recorder) return;
        var selected = LogsGrid.SelectedItems.Cast<RecordedLog>().ToArray();
        var rows = selected.Length == 0 ? _rows : selected;
        SetWorking(true);
        try
        {
            var text = await BackgroundAsync(async () =>
            {
                var safe = await recorder.RedactSnapshotAsync(rows);
                return string.Join(Environment.NewLine, safe.Select(row => $"{row.Time:O} [{row.Level}] [{row.Component}] {row.Text}"));
            });
            if (_closed) return;
            Clipboard.SetText(text);
            StatusLabel.Text = $"Copied {rows.Length} redacted entries.";
        }
        catch { if (!_closed) StatusLabel.Text = "Redacted copy was unavailable; nothing was copied."; }
        finally { SetWorking(false); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_working || SerilogSetup.Recorder is not { } recorder) return;
        var dialog = new SaveFileDialog { Title = "Export redacted diagnostics to a new ZIP", Filter = "ZIP archive (*.zip)|*.zip", DefaultExt = ".zip",
            FileName = $"gldrive-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip", AddExtension = true, OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        SetWorking(true);
        try
        {
            if (await RunBackground(() => File.Exists(destination)))
            {
                if (!_closed) StatusLabel.Text = "Choose a new filename. Existing files are preserved.";
                return;
            }
            var result = await BackgroundAsync(() => recorder.ExportBundleAsync(destination, _config));
            if (!_closed) StatusLabel.Text = result.MaskedRows > 0 || result.SkippedFiles > 0
                ? $"Diagnostics exported with {result.MaskedRows} masked rows and {result.SkippedFiles} omitted files; see manifest.json."
                : "Redacted diagnostics bundle exported.";
        }
        catch { if (!_closed) StatusLabel.Text = "The diagnostics bundle could not be exported."; }
        finally { SetWorking(false); }
    }

    private void Trace_Click(object sender, RoutedEventArgs e)
    {
        if (TraceComponentBox.SelectedItem is not string component) return;
        var until = SerilogSetup.Traces.Enable(component, TimeSpan.FromMinutes(15), DateTimeOffset.UtcNow);
        StatusLabel.Text = $"{component} disk trace enabled until {until.LocalDateTime:t}. Your saved logging level is unchanged.";
    }

    private void SetWorking(bool working)
    {
        _working = working;
        if (_closed) return;
        CaptureButton.IsEnabled = CopyButton.IsEnabled = ExportButton.IsEnabled = !working;
    }

    private static Task<T> RunBackground<T>(Func<T> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action);
    }
    private static Task<T> BackgroundAsync<T>(Func<Task<T>> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action);
    }
    private static Task BackgroundAsync(Func<Task> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action);
    }
}
