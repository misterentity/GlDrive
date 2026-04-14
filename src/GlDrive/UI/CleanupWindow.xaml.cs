using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using GlDrive.Downloads;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.UI;

public partial class CleanupWindow : Window
{
    public ObservableCollection<CleanupItem> Items { get; } = new();

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".m4v", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".mov", ".flv", ".webm", ".vob", ".iso",
        ".mp3", ".flac", ".wav", ".ogg", ".aac", ".m4a", ".wma",
    };

    public CleanupWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = Items;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select root folder to scan (e.g. Movies, TV)" };
        if (dlg.ShowDialog() == true)
            ScanPath.Text = dlg.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = ScanPath.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            MessageBox.Show("Select a valid folder first.", "Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Items.Clear();
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Scanning...";
        ScanHint.Visibility = Visibility.Collapsed;
        SummaryText.Text = "Scanning...";

        var results = await Task.Run(() => ScanForLeftovers(root));

        foreach (var item in results)
            Items.Add(item);

        var totalSize = results.Sum(r => r.ArchiveSize);
        SummaryText.Text = results.Count > 0
            ? $"{results.Count} folder(s) with leftover archives — {FormatSize(totalSize)} reclaimable"
            : "No leftover archives found.";

        CleanAllButton.IsEnabled = results.Count > 0;
        CleanSelectedButton.IsEnabled = results.Count > 0;
        ScanButton.Content = "Scan";
        ScanButton.IsEnabled = true;

        if (results.Count == 0)
            ScanHint.Visibility = Visibility.Visible;
    }

    private static List<CleanupItem> ScanForLeftovers(string rootPath)
    {
        var results = new List<CleanupItem>();

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(rootPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 5
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate directories in {Root}", rootPath);
            return results;
        }

        // Also check the root itself
        var allDirs = new[] { rootPath }.Concat(dirs);

        foreach (var dir in allDirs)
        {
            try
            {
                var files = Directory.GetFiles(dir);
                if (files.Length == 0) continue;

                var archiveFiles = new List<string>();
                var mediaCount = 0;

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (ArchiveExtractor.IsArchiveFile(name))
                        archiveFiles.Add(file);
                    else if (MediaExtensions.Contains(Path.GetExtension(file)))
                        mediaCount++;
                }

                // Only flag folders that have BOTH media and archive files
                // (media present = extraction already happened)
                if (archiveFiles.Count > 0 && mediaCount > 0)
                {
                    var archiveSize = archiveFiles.Sum(f =>
                    {
                        try { return new FileInfo(f).Length; }
                        catch { return 0L; }
                    });

                    results.Add(new CleanupItem
                    {
                        FolderPath = dir,
                        FolderName = Path.GetFileName(dir),
                        ArchiveCount = archiveFiles.Count,
                        ArchiveSize = archiveSize,
                        SizeText = FormatSize(archiveSize),
                        MediaCount = mediaCount,
                        ArchiveFiles = archiveFiles,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error scanning directory: {Dir}", dir);
            }
        }

        // Sort by size descending (biggest savings first)
        results.Sort((a, b) => b.ArchiveSize.CompareTo(a.ArchiveSize));
        return results;
    }

    private void CleanSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsGrid.SelectedItems.Cast<CleanupItem>()
            .Where(i => i.Status != "Cleaned")
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Select folders to clean first.", "Clean", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var totalSize = selected.Sum(i => i.ArchiveSize);
        var result = MessageBox.Show(
            $"Delete archives from {selected.Count} folder(s)?\nThis will free ~{FormatSize(totalSize)}.",
            "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            CleanItems(selected);
    }

    private void CleanAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = Items.Where(i => i.Status != "Cleaned").ToList();
        if (pending.Count == 0) return;

        var totalSize = pending.Sum(i => i.ArchiveSize);
        var result = MessageBox.Show(
            $"Delete archives from all {pending.Count} folder(s)?\nThis will free ~{FormatSize(totalSize)}.",
            "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            CleanItems(pending);
    }

    private void CleanItems(List<CleanupItem> items)
    {
        var totalDeleted = 0;
        var totalFailed = 0;
        long totalFreed = 0;

        foreach (var item in items)
        {
            var deleted = 0;
            var failed = 0;
            long freed = 0;

            foreach (var file in item.ArchiveFiles)
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                    freed += size;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete {File}", file);
                    failed++;
                }
            }

            item.Status = failed == 0 ? "Cleaned" : "Error";
            totalDeleted += deleted;
            totalFailed += failed;
            totalFreed += freed;

            Log.Information("Cleaned {Folder}: {Deleted} files deleted, {Failed} failed, {Freed} freed",
                item.FolderName, deleted, failed, FormatSize(freed));
        }

        var cleaned = items.Count(i => i.Status == "Cleaned");
        SummaryText.Text = $"Cleaned {cleaned} folder(s) — {FormatSize(totalFreed)} freed" +
            (totalFailed > 0 ? $" ({totalFailed} files failed)" : "");

        CleanAllButton.IsEnabled = Items.Any(i => i.Status != "Cleaned");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
}

public class CleanupItem : INotifyPropertyChanged
{
    private string _status = "Pending";

    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public int ArchiveCount { get; init; }
    public long ArchiveSize { get; init; }
    public string SizeText { get; init; } = "";
    public int MediaCount { get; init; }
    public List<string> ArchiveFiles { get; init; } = [];

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
