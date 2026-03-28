using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
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

    // Watch folder monitoring
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _watchFolders = new();
    private bool _watchEnabled;
    private readonly HashSet<string> _watchProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchLock = new();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".lz", ".lzma", ".iso", ".cab",
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lz",
    };

    // Old-style volume extensions (.r00-.r99, .s00-.s99) and split archives (.001-.999)
    private static readonly Regex VolumeExtRegex = new(@"^\.[rs]\d{2,3}$|^\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Modern RAR multi-part: name.part02.rar, name.part003.rar (part01 is the first volume, keep it)
    private static readonly Regex RarPartNonFirstRegex = new(@"\.part(?!0*1\.rar)(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Match any volume file belonging to a RAR set (for size calculation)
    private static readonly Regex RarVolumeFileRegex = new(@"\.[rs]\d{2,3}$|\.part\d+\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string SettingsPath =
        Path.Combine(ConfigManager.AppDataPath, "extractor-settings.json");

    public ExtractorWindow()
    {
        InitializeComponent();
        ArchiveGrid.ItemsSource = Archives;
        Archives.CollectionChanged += (_, _) => UpdateDropHint();
        UpdateDropHint();
        LoadSettings();
    }

    private void Settings_Changed(object sender, RoutedEventArgs e) => SaveSettings();

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
                ScanDirectory(path, recursive, existing);
            }
            else if (File.Exists(path) && IsArchiveFile(path) && existing.Add(path))
            {
                Archives.Add(CreateItem(path));
            }
        }
        UpdateStatus();
    }

    /// <summary>
    /// Manually recurse directories so a single permission error doesn't kill the whole scan.
    /// Directory.EnumerateFiles with AllDirectories throws on the first inaccessible dir.
    /// </summary>
    private void ScanDirectory(string dir, bool recursive, HashSet<string> existing)
    {
        // Scan files in this directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsArchiveFile(file) && existing.Add(file))
                    Archives.Add(CreateItem(file));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Access denied scanning files in {Dir}", dir);
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "I/O error scanning files in {Dir}", dir);
        }

        if (!recursive) return;

        // Recurse into subdirectories one at a time
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Access denied listing subdirs of {Dir}", dir);
            return;
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "I/O error listing subdirs of {Dir}", dir);
            return;
        }

        foreach (var subdir in subdirs)
        {
            try
            {
                ScanDirectory(subdir, true, existing);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug(ex, "Access denied entering {Dir}", subdir);
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "I/O error entering {Dir}", subdir);
            }
        }
    }

    private static bool IsArchiveFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;

        // Skip old-style volume files (.r00, .s01, .001, etc.)
        if (VolumeExtRegex.IsMatch(ext)) return false;

        // Skip modern multi-part RAR volumes that aren't the first part
        // e.g. name.part02.rar, name.part03.rar (but keep name.part01.rar and plain .rar)
        var name = Path.GetFileName(path);
        if (RarPartNonFirstRegex.IsMatch(name)) return false;

        // Check compound extensions like .tar.gz
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

        // Calculate total size across all volumes for RAR sets
        var totalSize = info.Exists ? info.Length : 0L;
        var volumeCount = 1;
        if (ext == "RAR" && info.Directory != null)
        {
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(path);

                // Strip .partNN for modern multi-part naming
                var partMatch = Regex.Match(baseName, @"^(.+)\.part\d+$", RegexOptions.IgnoreCase);
                var setBase = partMatch.Success ? partMatch.Groups[1].Value : baseName;

                foreach (var file in info.Directory.EnumerateFiles())
                {
                    if (file.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fn = file.Name;

                    // Old-style: baseName.r00, baseName.r01, baseName.s00, ...
                    if (fn.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                        VolumeExtRegex.IsMatch(Path.GetExtension(fn)))
                    {
                        totalSize += file.Length;
                        volumeCount++;
                    }
                    // Modern multi-part: setBase.partNN.rar
                    else if (partMatch.Success &&
                             fn.StartsWith(setBase, StringComparison.OrdinalIgnoreCase) &&
                             RarVolumeFileRegex.IsMatch(fn) &&
                             fn.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        totalSize += file.Length;
                        volumeCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to enumerate volume files for {Path}", path);
            }
        }

        var typeLabel = volumeCount > 1 ? $"RAR×{volumeCount}" : ext;

        return new ArchiveItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            ArchiveType = typeLabel,
            Size = totalSize,
            SizeText = FormatSize(totalSize),
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

            var baseName = Path.GetFileNameWithoutExtension(archivePath);
            // Strip .partNN for modern multi-part naming
            var pm = Regex.Match(baseName, @"^(.+)\.part\d+$", RegexOptions.IgnoreCase);
            var setBase = pm.Success ? pm.Groups[1].Value : baseName;

            Log.Information("Cleaning up archive set: base={Base}, dir={Dir}", setBase, dir);

            var toDelete = new List<string>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fn = Path.GetFileName(file);

                    // Must start with the set base name (case-insensitive)
                    if (!fn.StartsWith(setBase, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Character after base name must be a dot or end of name
                    // (prevents "moviename" matching "moviename-sample.rar")
                    if (fn.Length > setBase.Length && fn[setBase.Length] != '.')
                        continue;

                    var ext = Path.GetExtension(fn);

                    // .rar (main or .partNN.rar)
                    if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        toDelete.Add(file);
                        continue;
                    }

                    // Old-style volumes: .r00-.r999, .s00-.s999, .001-.999
                    if (VolumeExtRegex.IsMatch(ext))
                    {
                        toDelete.Add(file);
                        continue;
                    }

                    // SFV checksum files
                    if (ext.Equals(".sfv", StringComparison.OrdinalIgnoreCase))
                    {
                        toDelete.Add(file);
                        continue;
                    }

                    // Split archives: .001, .002, etc
                    if (Regex.IsMatch(ext, @"^\.\d{3,}$"))
                    {
                        toDelete.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error enumerating files for cleanup in {Dir}", dir);
            }

            if (toDelete.Count == 0)
            {
                Log.Warning("No archive files found to delete for set {Base} in {Dir}", setBase, dir);
                return;
            }

            Log.Information("Deleting {Count} archive files for set {Base}: {Files}",
                toDelete.Count, setBase, string.Join(", ", toDelete.Select(Path.GetFileName)));

            foreach (var file in toDelete)
            {
                try
                {
                    File.Delete(file);
                    Log.Debug("Deleted: {File}", Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete source archive: {Path}", archivePath);
        }
    }

    // ── Watch Folders ──

    private void AddWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder to watch for archives" };
        if (dlg.ShowDialog() != true) return;

        var folder = dlg.FolderName;
        if (_watchFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;

        _watchFolders.Add(folder);
        WatchFoldersText.Text = string.Join("; ", _watchFolders);
        SaveSettings();

        if (_watchEnabled)
            StartWatcherFor(folder);
    }

    private void LoadSettings()
    {
        try
        {
            // Migrate old watch-folders.json if present
            var oldPath = Path.Combine(ConfigManager.AppDataPath, "watch-folders.json");
            if (File.Exists(oldPath) && !File.Exists(SettingsPath))
            {
                var oldFolders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(oldPath));
                if (oldFolders != null)
                {
                    var settings = new ExtractorSettings { WatchFolders = oldFolders };
                    File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, _jsonOptions));
                    try { File.Delete(oldPath); } catch { }
                }
            }

            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<ExtractorSettings>(json, _jsonOptions);
            if (s == null) return;

            foreach (var f in s.WatchFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
                _watchFolders.Add(f);

            if (_watchFolders.Count > 0)
                WatchFoldersText.Text = string.Join("; ", _watchFolders);

            ChkDeleteAfter.IsChecked = s.DeleteAfterExtract;
            ChkRecursive.IsChecked = s.ScanSubfolders;
            ChkCreateSubfolder.IsChecked = s.CreateSubfolder;
            OutputMode.SelectedIndex = s.OutputMode;
            OverwriteMode.SelectedIndex = s.OverwriteMode;
            if (!string.IsNullOrEmpty(s.CustomOutputPath))
                CustomOutputPath.Text = s.CustomOutputPath;

            // Auto-start watchers if they were enabled
            if (s.WatchEnabled && _watchFolders.Count > 0)
            {
                _watchEnabled = true;
                WatchToggle.Content = "On";
                WatchToggle.IsChecked = true;
                foreach (var folder in _watchFolders)
                {
                    ScanAndAutoExtract(folder);
                    StartWatcherFor(folder);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load extractor settings");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new ExtractorSettings
            {
                WatchFolders = _watchFolders,
                WatchEnabled = _watchEnabled,
                DeleteAfterExtract = ChkDeleteAfter.IsChecked == true,
                ScanSubfolders = ChkRecursive.IsChecked == true,
                CreateSubfolder = ChkCreateSubfolder.IsChecked == true,
                OutputMode = OutputMode.SelectedIndex,
                OverwriteMode = OverwriteMode.SelectedIndex,
                CustomOutputPath = CustomOutputPath.Text
            };
            Directory.CreateDirectory(ConfigManager.AppDataPath);
            var json = JsonSerializer.Serialize(s, _jsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save extractor settings");
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void WatchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_watchEnabled)
        {
            StopAllWatchers();
            _watchEnabled = false;
            WatchToggle.Content = "Off";
            WatchToggle.IsChecked = false;
        }
        else
        {
            if (_watchFolders.Count == 0)
            {
                MessageBox.Show("Add at least one folder to watch first.", "Watch",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                WatchToggle.IsChecked = false;
                return;
            }

            _watchEnabled = true;
            WatchToggle.Content = "On";
            WatchToggle.IsChecked = true;

            // Scan existing archives in watch folders
            foreach (var folder in _watchFolders)
            {
                ScanAndAutoExtract(folder);
                StartWatcherFor(folder);
            }
        }
        SaveSettings();
    }

    private void StartWatcherFor(string folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = ChkRecursive.IsChecked == true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += OnWatchedFileCreated;
            watcher.Renamed += OnWatchedFileRenamed;
            watcher.Error += (_, args) => Log.Warning(args.GetException(), "FileSystemWatcher error on {Dir}", folder);

            _watchers.Add(watcher);
            Log.Information("Watching folder: {Dir}", folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start watcher for {Dir}", folder);
        }
    }

    private void StopAllWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnWatchedFileCreated(object sender, FileSystemEventArgs e) =>
        HandleWatchedFile(e.FullPath);

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs e) =>
        HandleWatchedFile(e.FullPath);

    private async void HandleWatchedFile(string path)
    {
        if (!IsArchiveFile(path)) return;

        lock (_watchLock)
        {
            if (!_watchProcessed.Add(path)) return;
        }

        // Wait for the file to finish being written (e.g. download completing)
        await WaitForFileReady(path);

        if (!File.Exists(path)) return;

        Dispatcher.Invoke(() =>
        {
            var existing = Archives.Any(a => a.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing) return;

            var item = CreateItem(path);
            Archives.Add(item);
            UpdateStatus();

            // Auto-extract immediately
            _ = AutoExtractItem(item);
        });
    }

    private void ScanAndAutoExtract(string folder)
    {
        var existing = new HashSet<string>(Archives.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);
        var found = new List<ArchiveItem>();

        ScanDirectory(folder, ChkRecursive.IsChecked == true, existing);

        // Auto-extract any newly added queued items
        foreach (var item in Archives.Where(a => a.Status == "Queued").ToList())
        {
            _ = AutoExtractItem(item);
        }
    }

    private async Task AutoExtractItem(ArchiveItem item)
    {
        if (item.Status != "Queued") return;

        item.Status = "Extracting";
        item.Progress = 0;

        var outputDir = GetOutputDir(item);
        var password = "";
        var overwriteIdx = 0;
        var deleteAfter = false;

        Dispatcher.Invoke(() =>
        {
            password = ArchivePassword.Password;
            overwriteIdx = OverwriteMode.SelectedIndex;
            deleteAfter = ChkDeleteAfter.IsChecked == true;
        });

        try
        {
            await Task.Run(() => ExtractArchive(item, outputDir, password, overwriteIdx, CancellationToken.None));
            item.Status = "Done";
            item.Progress = 100;

            if (deleteAfter)
                DeleteSourceFiles(item.FilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-extract failed: {File}", item.FileName);
            item.Status = "Error";
            item.ErrorMessage = ex.Message;
        }

        Dispatcher.Invoke(UpdateStatus);
    }

    /// <summary>
    /// Wait until a file is no longer being written to. Checks every 2 seconds for up to 5 minutes.
    /// Essential for network drives where files appear before the transfer completes.
    /// </summary>
    private static async Task WaitForFileReady(string path, int maxWaitMs = 300_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSize = -1;
        int stableCount = 0;

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            await Task.Delay(2000);

            try
            {
                if (!File.Exists(path)) return;

                var info = new FileInfo(path);
                var currentSize = info.Length;

                if (currentSize == lastSize && currentSize > 0)
                {
                    stableCount++;
                    if (stableCount >= 2)
                    {
                        // Size stable for 4+ seconds, try opening exclusively to confirm
                        try
                        {
                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                            return; // File is ready
                        }
                        catch (IOException)
                        {
                            stableCount = 0; // Still locked, keep waiting
                        }
                    }
                }
                else
                {
                    stableCount = 0;
                }
                lastSize = currentSize;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Log.Warning("Timeout waiting for file to be ready: {Path}", path);
    }

    // ── Helpers ──

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        StopAllWatchers();
        base.OnClosed(e);
    }
}

public class ExtractorSettings
{
    public List<string> WatchFolders { get; set; } = [];
    public bool WatchEnabled { get; set; }
    public bool DeleteAfterExtract { get; set; }
    public bool ScanSubfolders { get; set; } = true;
    public bool CreateSubfolder { get; set; }
    public int OutputMode { get; set; }
    public int OverwriteMode { get; set; }
    public string CustomOutputPath { get; set; } = "";
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
