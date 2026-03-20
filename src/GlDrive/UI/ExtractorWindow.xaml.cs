using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GlDrive.UI;

public partial class ExtractorWindow : Window
{
    public ObservableCollection<ArchiveItem> Archives { get; } = new();
    private CancellationTokenSource? _cts;
    private bool _extracting;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".lz", ".lzma", ".iso", ".cab",
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lz",
    };

    // Volume extensions to skip (they're handled by the first volume)
    private static readonly HashSet<string> VolumeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".r00", ".r01", ".r02", ".r03", ".r04", ".r05", ".r06", ".r07", ".r08", ".r09",
        ".r10", ".r11", ".r12", ".r13", ".r14", ".r15", ".r16", ".r17", ".r18", ".r19",
        ".r20", ".r21", ".r22", ".r23", ".r24", ".r25", ".r26", ".r27", ".r28", ".r29",
        ".r30", ".r31", ".r32", ".r33", ".r34", ".r35", ".r36", ".r37", ".r38", ".r39",
        ".r40", ".r41", ".r42", ".r43", ".r44", ".r45", ".r46", ".r47", ".r48", ".r49",
        ".s00", ".s01", ".s02", ".s03", ".s04", ".s05",
        ".001", ".002", ".003", ".004", ".005", ".006", ".007", ".008", ".009",
    };

    public ExtractorWindow()
    {
        InitializeComponent();
        ArchiveGrid.ItemsSource = Archives;
        Archives.CollectionChanged += (_, _) => UpdateDropHint();
        UpdateDropHint();
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
        AddPaths(paths);
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
            AddPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to scan for archives" };
        if (dlg.ShowDialog() == true)
            AddPaths([dlg.FolderName]);
    }

    private void AddPaths(string[] paths)
    {
        var recursive = ChkRecursive.IsChecked == true;
        var existing = new HashSet<string>(Archives.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*.*", option))
                    {
                        if (IsArchiveFile(file) && existing.Add(file))
                            Archives.Add(CreateItem(file));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error scanning folder {Path}", path);
                }
            }
            else if (File.Exists(path) && IsArchiveFile(path) && existing.Add(path))
            {
                Archives.Add(CreateItem(path));
            }
        }
        UpdateStatus();
    }

    private static bool IsArchiveFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        if (VolumeExtensions.Contains(ext)) return false;

        // Check for compound extensions like .tar.gz
        var name = Path.GetFileName(path);
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lz" })
        {
            if (name.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return SupportedExtensions.Contains(ext);
    }

    private static ArchiveItem CreateItem(string path)
    {
        var info = new FileInfo(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        // Detect compound types
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) ext = "TGZ";
        else if (name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)) ext = "TBZ2";
        else if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) ext = "TXZ";

        return new ArchiveItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            ArchiveType = ext,
            Size = info.Exists ? info.Length : 0,
            SizeText = FormatSize(info.Exists ? info.Length : 0),
            Status = "Queued",
        };
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ArchiveGrid.SelectedItems.Cast<ArchiveItem>().ToList();
        foreach (var item in selected) Archives.Remove(item);
        UpdateStatus();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        Archives.Clear();
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

    private string GetOutputDir(ArchiveItem item)
    {
        var archiveDir = Path.GetDirectoryName(item.FilePath) ?? "";
        var archiveName = Path.GetFileNameWithoutExtension(item.FilePath);

        // Strip compound extension (.tar from file.tar.gz)
        if (archiveName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            archiveName = archiveName[..^4];

        return OutputMode.SelectedIndex switch
        {
            1 => Path.Combine(archiveDir, archiveName),                          // Archive subfolder
            2 when !string.IsNullOrWhiteSpace(CustomOutputPath.Text) =>
                ChkCreateSubfolder.IsChecked == true
                    ? Path.Combine(CustomOutputPath.Text, archiveName)
                    : CustomOutputPath.Text,
            _ => ChkCreateSubfolder.IsChecked == true                            // Same as archive
                    ? Path.Combine(archiveDir, archiveName)
                    : archiveDir,
        };
    }

    // ── Extraction ──

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_extracting)
        {
            _cts?.Cancel();
            return;
        }

        var toExtract = Archives.Where(a => a.Status != "Done").ToList();
        if (toExtract.Count == 0) return;

        _extracting = true;
        ExtractButton.Content = "Cancel";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var password = ArchivePassword.Password;
        var deleteAfter = ChkDeleteAfter.IsChecked == true;
        var overwriteIdx = OverwriteMode.SelectedIndex;

        var completed = 0;

        try
        {
            foreach (var item in toExtract)
            {
                if (ct.IsCancellationRequested) break;

                item.Status = "Extracting";
                item.Progress = 0;

                var outputDir = GetOutputDir(item);

                try
                {
                    await Task.Run(() => ExtractArchive(item, outputDir, password, overwriteIdx, ct), ct);
                    item.Status = "Done";
                    item.Progress = 100;

                    if (deleteAfter)
                        DeleteSourceFiles(item.FilePath);
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    break;
                }
                catch (InvalidFormatException ex)
                {
                    Log.Warning(ex, "Invalid archive format: {File}", item.FileName);
                    item.Status = "Error";
                    item.ErrorMessage = "Invalid or corrupt archive";
                }
                catch (CryptographicException)
                {
                    item.Status = "Error";
                    item.ErrorMessage = "Wrong password";
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Extraction failed: {File}", item.FileName);
                    item.Status = "Error";
                    item.ErrorMessage = ex.Message;
                }

                completed++;
                Dispatcher.Invoke(() =>
                {
                    var pct = (double)completed / toExtract.Count * 100;
                    TotalProgress.Value = pct;
                    TotalProgressText.Text = $"{pct:F0}%";
                    UpdateStatus();
                });
            }
        }
        finally
        {
            _extracting = false;
            ExtractButton.Content = "Extract All";
            _cts?.Dispose();
            _cts = null;
            UpdateStatus();
        }
    }

    private void ExtractArchive(ArchiveItem item, string outputDir, string password, int overwriteMode, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var readerOptions = new ReaderOptions
        {
            Password = string.IsNullOrEmpty(password) ? null : password
        };

        // Try archive-based extraction first (supports random access, multi-volume)
        try
        {
            using var archive = ArchiveFactory.OpenArchive(item.FilePath, readerOptions);

            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Dispatcher.Invoke(() => item.EntryCount = entries.Count);

            var safeDirPath = Path.GetFullPath(outputDir);
            var processed = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Key == null) continue;

                // Path traversal protection
                var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Skipping path traversal entry: {Key}", entry.Key);
                    continue;
                }

                // Overwrite handling
                if (File.Exists(fullPath))
                {
                    switch (overwriteMode)
                    {
                        case 1: // Skip
                            processed++;
                            continue;
                        case 2: // Rename
                            fullPath = GetUniqueFileName(fullPath);
                            break;
                        // case 0: Overwrite — just extract normally
                    }
                }

                var entryDir = Path.GetDirectoryName(fullPath);
                if (entryDir != null) Directory.CreateDirectory(entryDir);

                // Extract with buffered write for network drive performance
                using (var entryStream = entry.OpenEntryStream())
                using (var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 256 * 1024))
                {
                    entryStream.CopyTo(outStream, 256 * 1024);
                }

                processed++;
                var pct = entries.Count > 0 ? (double)processed / entries.Count * 100 : 100;
                Dispatcher.Invoke(() => item.Progress = pct);
            }
        }
        catch (InvalidOperationException)
        {
            // Fallback to reader-based (streaming) extraction for formats that don't support random access
            using var fileStream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);
            ExtractWithReader(item, fileStream, outputDir, readerOptions, overwriteMode, ct);
        }
    }

    private void ExtractWithReader(ArchiveItem item, Stream stream, string outputDir,
        ReaderOptions options, int overwriteMode, CancellationToken ct)
    {
        var safeDirPath = Path.GetFullPath(outputDir);
        var processed = 0;

        using var reader = ReaderFactory.OpenReader(stream, options);
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.Entry.IsDirectory) continue;
            var key = reader.Entry.Key;
            if (key == null) continue;

            var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, key));
            if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(fullPath))
            {
                switch (overwriteMode)
                {
                    case 1: processed++; continue;
                    case 2: fullPath = GetUniqueFileName(fullPath); break;
                }
            }

            var entryDir = Path.GetDirectoryName(fullPath);
            if (entryDir != null) Directory.CreateDirectory(entryDir);

            using var entryStream = reader.OpenEntryStream();
            using var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 256 * 1024);
            entryStream.CopyTo(outStream, 256 * 1024);

            processed++;
            Dispatcher.Invoke(() =>
            {
                item.EntryCount = processed;
                item.Progress = -1; // Indeterminate for streaming
            });
        }

        Dispatcher.Invoke(() => item.Progress = 100);
    }

    private static string GetUniqueFileName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private static void DeleteSourceFiles(string archivePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(archivePath);
            if (dir == null) return;

            File.Delete(archivePath);

            // Also delete volume files (.r00, .r01, .s00, etc.)
            var baseName = Path.GetFileNameWithoutExtension(archivePath);
            foreach (var file in Directory.GetFiles(dir, $"{baseName}.*"))
            {
                var ext = Path.GetExtension(file);
                if (VolumeExtensions.Contains(ext) ||
                    ext.Equals(".sfv", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to delete {File}", file); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete source archive: {Path}", archivePath);
        }
    }

    // ── Helpers ──

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
}

// ── View Model ──

public class ArchiveItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private double _progress;
    private int _entryCount;
    private string? _errorMessage;

    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ArchiveType { get; init; } = "";
    public long Size { get; init; }
    public string SizeText { get; init; } = "";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public int EntryCount
    {
        get => _entryCount;
        set { _entryCount = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
