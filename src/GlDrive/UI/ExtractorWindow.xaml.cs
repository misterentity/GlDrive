using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using GlDrive.Downloads.ArchiveExtraction;
using Microsoft.Win32;

namespace GlDrive.UI;

/// <summary>
/// View over the shared <see cref="ArchiveExtractionEngine"/>: queue grid, buttons, settings
/// controls and progress. All extraction, watching and persistence live in the engine.
/// </summary>
public partial class ExtractorWindow : Window
{
    private readonly ArchiveExtractionEngine _engine;
    private readonly bool _demoMode;

    // True once the constructor has finished initializing the window, and false while the
    // controls are being set FROM the engine. Guards the change handlers so that the Checked
    // events raised by InitializeComponent or by a programmatic refresh never write
    // intermediate control state back into the engine or onto disk.
    private bool _initialized;

    public ObservableCollection<ArchiveItem> Archives => _engine.Archives;

    public ExtractorWindow() : this(demoMode: false) { }

    internal ExtractorWindow(bool demoMode)
    {
        _demoMode = demoMode;
        InitializeComponent();

        // Demo mode (screenshots) gets a private in-memory engine: it skips saved watch
        // folders and never writes extractor settings.
        var ui = new DispatcherSynchronizationContext(Dispatcher);
        _engine = demoMode
            ? new ArchiveExtractionEngine(ui, new ExtractorEngineOptions { DemoMode = true })
            : ExtractorEngineHost.Acquire(ui, startWatchers: true);

        ArchiveGrid.ItemsSource = Archives;
        Archives.CollectionChanged += Archives_CollectionChanged;
        UpdateDropHint();
        ShowEngineSettings();
        if (Archives.Count > 0) UpdateStatus();

        _engine.StatusChanged += Engine_StatusChanged;
        _engine.ExtractingChanged += Engine_ExtractingChanged;
        _engine.BatchProgressChanged += Engine_BatchProgressChanged;
        _engine.SettingsChanged += Engine_SettingsChanged;

        OutputMode.SelectionChanged += (_, _) => PushOptions();
        OverwriteMode.SelectionChanged += (_, _) => PushOptions();
        CustomOutputPath.TextChanged += (_, _) => PushOptions();
        ArchivePassword.PasswordChanged += (_, _) => PushOptions();

        _initialized = true;
    }

    // ── Engine → view ──

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Archives_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnUi(UpdateDropHint);

    private void Engine_StatusChanged() => OnUi(UpdateStatus);

    private void Engine_ExtractingChanged(bool extracting) =>
        OnUi(() => ExtractButton.Content = extracting ? "Cancel" : "Extract All");

    private void Engine_BatchProgressChanged(double percent) => OnUi(() =>
    {
        TotalProgress.Value = percent;
        TotalProgressText.Text = $"{percent:F0}%";
    });

    private void Engine_SettingsChanged() => OnUi(ShowEngineSettings);

    /// <summary>Show the engine's settings and watch state in the controls.</summary>
    private void ShowEngineSettings()
    {
        var wasInitialized = _initialized;
        _initialized = false;
        try
        {
            WatchFoldersText.Text = string.Join("; ", _engine.WatchFolders);
            ChkDeleteAfter.IsChecked = _engine.DeleteAfterExtract;
            ChkRecursive.IsChecked = _engine.ScanSubfolders;
            ChkCreateSubfolder.IsChecked = _engine.CreateSubfolder;
            OutputMode.SelectedIndex = _engine.OutputMode;
            OverwriteMode.SelectedIndex = _engine.OverwriteMode;
            if (CustomOutputPath.Text != _engine.CustomOutputPath)
                CustomOutputPath.Text = _engine.CustomOutputPath;
            if (ArchivePassword.Password != _engine.Password)
                ArchivePassword.Password = _engine.Password;
            ShowWatchState();
            ExtractButton.Content = _engine.IsExtracting ? "Cancel" : "Extract All";
        }
        finally
        {
            _initialized = wasInitialized;
        }
    }

    private void ShowWatchState()
    {
        WatchToggle.Content = _engine.WatchEnabled ? "ON" : "OFF";
        WatchToggle.IsChecked = _engine.WatchEnabled;
    }

    // ── View → engine ──

    /// <summary>Copy the option controls into the engine (not persisted by itself).</summary>
    private void PushOptions()
    {
        if (!_initialized) return;
        _engine.DeleteAfterExtract = ChkDeleteAfter.IsChecked == true;
        _engine.ScanSubfolders = ChkRecursive.IsChecked == true;
        _engine.CreateSubfolder = ChkCreateSubfolder.IsChecked == true;
        _engine.OutputMode = OutputMode.SelectedIndex;
        _engine.OverwriteMode = OverwriteMode.SelectedIndex;
        _engine.CustomOutputPath = CustomOutputPath.Text;
        _engine.Password = ArchivePassword.Password;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        // Ignore events fired during XAML parsing or a refresh from the engine — any save at
        // that point would corrupt the on-disk settings with intermediate state.
        if (!_initialized) return;
        PushOptions();
        _engine.SaveSettings();
    }

    private void UpdateDropHint() =>
        DropHint.Visibility = Archives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStatus()
    {
        var total = Archives.Count;
        var done = Archives.Count(a => a.Status == "Done");
        var failed = Archives.Count(a => a.Status == "Error");
        var parts = new List<string> { $"{total} archive(s)" };
        if (done > 0) parts.Add($"{done} done");
        if (failed > 0) parts.Add($"{failed} failed");
        StatusText.Text = string.Join(" — ", parts);
    }

    // ── Drag & Drop ──

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _engine.AddPaths(paths);
    }

    // ── Add files/folders ──

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Archives",
            Filter = "Archive files|*.rar;*.zip;*.7z;*.tar;*.gz;*.tgz;*.bz2;*.tbz2;*.xz;*.txz;*.lz;*.iso;*.cab|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
            _engine.AddPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to scan for archives" };
        if (dlg.ShowDialog() == true)
            _engine.AddPaths([dlg.FolderName]);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ArchiveGrid.SelectedItems.Cast<ArchiveItem>().ToList();
        foreach (var item in selected) _engine.Remove(item);
        UpdateStatus();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _engine.Clear();
        TotalProgress.Value = 0;
        TotalProgressText.Text = "";
        UpdateStatus();
    }

    // ── Output folder ──

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output folder" };
        if (dlg.ShowDialog() == true)
        {
            CustomOutputPath.Text = dlg.FolderName;
            OutputMode.SelectedIndex = 2; // Custom folder
        }
    }

    // ── Extraction ──

    private async void Extract_Click(object sender, RoutedEventArgs e)
        => await _engine.ExtractAllAsync();

    // ── Watch Folders ──

    private void AddWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to watch for archives" };
        if (dlg.ShowDialog() != true) return;

        if (_engine.AddWatchFolder(dlg.FolderName))
            WatchFoldersText.Text = string.Join("; ", _engine.WatchFolders);
    }

    private void WatchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.ToggleWatch())
        {
            MessageBox.Show("Add at least one folder to watch first.", "Watch",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        ShowWatchState();
    }

    // ── Helpers ──

    private void CleanFolders_Click(object sender, RoutedEventArgs e)
    {
        var win = new CleanupWindow { Owner = this };
        win.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        PushOptions();
        _engine.StatusChanged -= Engine_StatusChanged;
        _engine.ExtractingChanged -= Engine_ExtractingChanged;
        _engine.BatchProgressChanged -= Engine_BatchProgressChanged;
        _engine.SettingsChanged -= Engine_SettingsChanged;
        Archives.CollectionChanged -= Archives_CollectionChanged;

        // Closing the extractor ends it, as it always has: settings are saved, the running
        // extraction is cancelled and the watchers stop.
        if (_demoMode) _engine.Dispose();
        else ExtractorEngineHost.Release(_engine);
        base.OnClosed(e);
    }
}
