using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
using GlDrive.Downloads;
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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _extractionGate = new(1, 1);

    // True once the constructor has finished initializing the window.
    // Guards Settings_Changed so that the Checked event handlers on the
    // checkboxes (wired via XAML) don't fire SaveSettings during
    // InitializeComponent and LoadSettings, when not all UI controls exist
    // yet OR when internal state like _watchEnabled hasn't been restored.
    private bool _initialized;

    // Watch folder monitoring
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _watchFolders = new();
    private bool _watchEnabled;
    private readonly HashSet<string> _watchProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _watchRetryCounts = new(StringComparer.OrdinalIgnoreCase);
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
        _initialized = true; // Enable Settings_Changed → SaveSettings from here on
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        // Ignore events fired during XAML parsing / LoadSettings — the UI
        // controls or internal state may not be fully set up yet, and any
        // save at that point would both crash (NullReferenceException) and
        // corrupt the on-disk settings with intermediate state.
        if (!_initialized) return;
        SaveSettings();
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
    /// Background-safe archive path collector. Does NOT touch any UI state.
    /// Used by the startup initial scan to avoid blocking the UI thread on
    /// slow/network-backed watch folders.
    /// </summary>
    private static void CollectArchivePaths(string dir, bool recursive, List<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                if (IsArchiveFile(file))
                    results.Add(file);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        if (!recursive) return;

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subdirs)
        {
            try
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                CollectArchivePaths(sub, true, results);
            }
            catch { }
        }
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
            subdirs = Directory.EnumerateDirectories(dir).ToList();
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
                if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0) continue;
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
            // Cancel/dispose race guard: the in-flight run's finally block may be
            // disposing _cts at this exact moment. Snapshot the field to a local so
            // we don't read a freshly-nulled field, and swallow ObjectDisposedException
            // in case it was already disposed — Cancel() on a disposed CTS throws, and
            // this is an async void handler so an escape would crash the whole app.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }

        var toExtract = Archives.Where(a => a.Status != "Done").ToList();
        if (toExtract.Count == 0) return;

        _extracting = true;
        ExtractButton.Content = "Cancel";
        // Hold a LOCAL reference to the CTS used by this run. The finally block nulls
        // the _cts field before disposing this local, so a concurrent cancel-click
        // reads null (no-op) rather than touching a half-disposed source. The token is
        // captured once up front and used throughout — IsCancellationRequested is safe
        // to read even after the source is disposed, so the loop never needs the field.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _cts = cts;
        var ct = cts.Token;
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
                    await _extractionGate.WaitAsync(ct);
                    try
                    {
                        await ExtractArchiveAsync(item, outputDir, password, overwriteIdx, ct);
                        item.Status = "Done";
                        item.Progress = 100;

                        if (deleteAfter && !await Task.Run(() => DeleteSourceFiles(item.FilePath), ct))
                            item.ErrorMessage = "Extracted, but source cleanup was incomplete";
                    }
                    finally
                    {
                        _extractionGate.Release();
                    }
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
                var totalPct = (double)completed / toExtract.Count * 100;
                Dispatcher.BeginInvoke(() =>
                {
                    TotalProgress.Value = totalPct;
                    TotalProgressText.Text = $"{totalPct:F0}%";
                    UpdateStatus();
                });
            }
        }
        finally
        {
            _extracting = false;
            ExtractButton.Content = "Extract All";
            // Null the field BEFORE disposing the local, so a cancel-click racing this
            // teardown sees null (no-op) instead of calling Cancel() on a disposed CTS.
            // Only clear the field if it still points at our CTS — never clobber a CTS
            // a newer run may have installed.
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            UpdateStatus();
        }
    }

    /// <summary>
    /// Extract archive on a dedicated BelowNormal priority thread to avoid UI lag.
    /// Uses BeginInvoke for progress updates and throttles to max 10 updates/sec.
    /// </summary>
    private Task ExtractArchiveAsync(ArchiveItem item, string outputDir, string password, int overwriteMode, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ExtractArchive(item, outputDir, password, overwriteMode, ct);
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = $"Extract-{item.FileName}"
        };
        thread.Start();
        return tcs.Task;
    }

    private void ExtractArchive(ArchiveItem item, string outputDir, string password, int overwriteMode, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var readerOptions = new ReaderOptions
        {
            Password = string.IsNullOrEmpty(password) ? null : password
        };

        var lastProgressUpdate = DateTime.MinValue;

        // Try archive-based extraction first (supports random access, multi-volume).
        //
        // For old-style scene multi-volume RARs (.rar + .r00 + .r01 + ... + .r99 [+ .s00...])
        // SharpCompress's single-path auto-detection can fail with
        // "Unknown Rar Header: <byte>" when it can't correctly walk to the next volume.
        // Explicitly enumerating the volumes in the same directory and passing the
        // full list to ArchiveFactory.OpenArchive(IReadOnlyList<FileInfo>, ...) bypasses
        // SharpCompress's discovery entirely and reads the volumes as given.
        try
        {
            IArchive archive;
            var volumes = TryDiscoverRarVolumes(item.FilePath);
            if (volumes != null && volumes.Count > 1)
            {
                Log.Debug(
                    "Extractor: opening {Count}-volume RAR set starting at {First}",
                    volumes.Count, Path.GetFileName(item.FilePath));
                archive = ArchiveFactory.OpenArchive(volumes, readerOptions);
            }
            else
            {
                archive = ArchiveFactory.OpenArchive(item.FilePath, readerOptions);
            }

            using var _ = archive;

            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            Dispatcher.BeginInvoke(() => item.EntryCount = entries.Count);

            var safeDirPath = Path.GetFullPath(outputDir);
            if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
                safeDirPath += Path.DirectorySeparatorChar;
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

                using (var entryStream = entry.OpenEntryStream())
                    ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct);

                processed++;

                // Throttle progress updates to max ~10/sec to avoid flooding the UI
                var now = DateTime.UtcNow;
                if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
                {
                    lastProgressUpdate = now;
                    var pct = entries.Count > 0 ? (double)processed / entries.Count * 100 : 100;
                    Dispatcher.BeginInvoke(() => item.Progress = pct);
                }
            }

            Dispatcher.BeginInvoke(() => item.Progress = 100);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidFormatException
                                      or SharpCompress.Common.MultiVolumeExtractionException)
        {
            // SharpCompress has known issues with scene-style multi-volume RAR sets
            // (.rar + .r00 + .r01 + ...). Both archive-mode and streaming-reader mode
            // fail for these — the streaming reader throws MultiVolumeExtractionException
            // explicitly, and archive-mode throws "Unknown Rar Header" garbage.
            //
            // When that happens, fall back to the external UnRAR.exe or 7z.exe binary
            // if one is installed on the system. WinRAR is the canonical reference
            // implementation and handles every edge case; 7-Zip covers most RAR5 files.
            Log.Warning(
                "Extractor: SharpCompress archive-mode failed ({Msg}), trying external tool: {File}",
                ex.Message, item.FileName);

            if (!TryExtractExternal(item, outputDir, password, overwriteMode, ct))
            {
                // No external tool available — fall through to the streaming reader as
                // a last-ditch attempt. Will still fail for multi-volume sets, but might
                // work for certain single-file archive formats.
                Log.Warning("Extractor: no external unrar/7z found, falling back to streaming reader");
                using var fileStream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);
                ExtractWithReader(item, fileStream, outputDir, readerOptions, overwriteMode, ct);
            }
        }
    }

    // Common install paths for WinRAR's UnRAR and 7-Zip. Checked in order.
    private static readonly string[] ExternalToolCandidates =
    {
        @"C:\Program Files\WinRAR\UnRAR.exe",
        @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
        @"C:\Program Files\WinRAR\Rar.exe",
        @"C:\Program Files (x86)\WinRAR\Rar.exe",
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
    };

    private static string? FindExternalTool()
    {
        foreach (var p in ExternalToolCandidates)
            if (File.Exists(p)) return p;
        // Last-chance: PATH lookup for unrar / 7z
        foreach (var name in new[] { "unrar.exe", "UnRAR.exe", "7z.exe" })
        {
            var path = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, name))
                .FirstOrDefault(File.Exists);
            if (path != null) return path;
        }
        return null;
    }

    /// <summary>
    /// Shell out to an external UnRAR or 7-Zip binary to extract the archive.
    /// Returns true on success, false if no tool was found (caller should then try
    /// the next fallback). Throws IOException on non-zero exit code.
    /// </summary>
    private bool TryExtractExternal(ArchiveItem item, string outputDir, string password, int overwriteMode, CancellationToken ct)
    {
        var tool = FindExternalTool();
        if (tool == null) return false;

        Directory.CreateDirectory(outputDir);
        using var staging = new ExtractionStagingDirectory(outputDir);

        var isUnrar = Path.GetFileName(tool).StartsWith("unrar", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(tool).StartsWith("rar.exe", StringComparison.OrdinalIgnoreCase);
        var isSevenZip = Path.GetFileName(tool).StartsWith("7z", StringComparison.OrdinalIgnoreCase);

        // Pipe-deadlock avoidance: do NOT redirect stdout or stderr. The earlier
        // v1.44.64 approach used BeginOutputReadLine/BeginErrorReadLine to drain
        // asynchronously, but UnRAR's output on corrupted RAR sets (one CRC error
        // line per volume × 80+ volumes) still managed to stall, and the cost of
        // losing the exact error text in the log is lower than the cost of an
        // occasional deadlock.
        //
        // Instead, silence the tool entirely with -inul (UnRAR) / combined flags
        // (7-Zip), and rely on the exit code to determine success or failure.
        // stdin IS redirected so we can close it immediately, preventing the tool
        // from blocking on any read-from-stdin that isn't covered by -y.
        //
        // CreateNoWindow + UseShellExecute=false means the tool runs detached with
        // its own hidden console; unredirected writes go to that console and are
        // discarded by the OS.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = staging.Path,
        };

        if (isUnrar)
        {
            // UnRAR syntax:
            //   UnRAR x -y -o+ -inul [-p-] [-p<password>] <archive> <destDir>\
            psi.ArgumentList.Add("x");                     // extract with full paths
            psi.ArgumentList.Add("-y");                    // yes to all prompts
            if (overwriteMode == 0) psi.ArgumentList.Add("-o+");      // overwrite
            else if (overwriteMode == 1) psi.ArgumentList.Add("-o-"); // skip existing
            else psi.ArgumentList.Add("-or");                         // rename auto
            psi.ArgumentList.Add("-inul");                 // disable ALL output
            if (string.IsNullOrEmpty(password))
                psi.ArgumentList.Add("-p-");               // prevent password prompt
            else
                psi.ArgumentList.Add($"-p{password}");
            psi.ArgumentList.Add(item.FilePath);
            psi.ArgumentList.Add(staging.Path + Path.DirectorySeparatorChar);
        }
        else if (isSevenZip)
        {
            // 7z syntax:
            //   7z x -y -aoa [-p<password>] -bso0 -bse0 -bsp0 -o<destDir> <archive>
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add("-y");
            if (overwriteMode == 0) psi.ArgumentList.Add("-aoa");     // overwrite all
            else if (overwriteMode == 1) psi.ArgumentList.Add("-aos"); // skip existing
            else psi.ArgumentList.Add("-aou");                         // rename auto
            psi.ArgumentList.Add("-bso0");                 // stdout: no messages
            psi.ArgumentList.Add("-bse0");                 // stderr: no messages
            psi.ArgumentList.Add("-bsp0");                 // progress: no output
            if (!string.IsNullOrEmpty(password)) psi.ArgumentList.Add($"-p{password}");
            psi.ArgumentList.Add($"-o{staging.Path}");
            psi.ArgumentList.Add(item.FilePath);
        }
        else
        {
            Log.Warning("Extractor: unknown external tool shape: {Tool}", tool);
            return false;
        }

        Log.Information("Extractor: using external tool {Tool} for {File}", Path.GetFileName(tool), item.FileName);

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return false;

        // Close stdin immediately so the child can't block reading from it.
        // UnRAR's -y covers most interactive prompts, but some edge cases (like
        // file-in-use warnings) would otherwise read from stdin and block forever.
        try { proc.StandardInput.Close(); } catch { }

        // Show indeterminate progress while the external tool runs.
        var spinCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var pct = 0.0;
            while (!spinCts.IsCancellationRequested)
            {
                pct = (pct + 2) % 95;
                Dispatcher.BeginInvoke(() => item.Progress = pct);
                try { await Task.Delay(500, spinCts.Token); } catch { break; }
            }
        }, spinCts.Token);

        // Hard timeout so a genuinely stuck tool can't hold up extraction forever.
        // 20 minutes covers extraction of very large multi-volume sets on slow disks,
        // while failing fast enough to unblock the queue when UnRAR can't cleanly exit
        // — observed in the wild on corrupt RAR sets where UnRAR extracts the data
        // correctly but then hangs in its finalize-with-CRC-errors code path.
        var startedAt = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(20);

        try
        {
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new OperationCanceledException(ct);
                }
                if (DateTime.UtcNow - startedAt > timeout)
                {
                    Log.Warning("Extractor: {Tool} timed out after {Minutes} min for {File}, killing",
                        Path.GetFileName(tool), timeout.TotalMinutes, item.FileName);
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new IOException($"{Path.GetFileName(tool)} timed out after {timeout.TotalMinutes} minutes");
                }
                proc.WaitForExit(500);
            }
        }
        finally
        {
            spinCts.Cancel();
            spinCts.Dispose();
        }

        if (proc.ExitCode != 0)
        {
            // UnRAR exit codes: 0=ok, 1=non-fatal warning, 2=fatal, 3=CRC error,
            // 4=locked, 5=write error, 6=open, 7=usage, 8=memory, 9=create, 10=no
            // files, 11=wrong password, 255=user stop. Any non-zero result is
            // incomplete and must retain the source archive for a later retry.
            var interpretedExit = proc.ExitCode;
            throw new IOException($"{Path.GetFileName(tool)} failed (exit {interpretedExit})");
        }

        CommitStagedOutput(staging.Path, outputDir, overwriteMode, ct);
        Dispatcher.BeginInvoke(() => item.Progress = 100);
        Log.Information("Extractor: external tool completed for {File} (exit {Code})",
            item.FileName, proc.ExitCode);
        return true;
    }

    private static void CommitStagedOutput(string stagingDir, string outputDir,
        int overwriteMode, CancellationToken ct)
    {
        var safeOutput = Path.GetFullPath(outputDir);
        if (!safeOutput.EndsWith(Path.DirectorySeparatorChar)) safeOutput += Path.DirectorySeparatorChar;

        foreach (var stagedFile in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stagingDir, stagedFile);
            var destination = Path.GetFullPath(Path.Combine(safeOutput, relative));
            if (!destination.StartsWith(safeOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"External extractor produced an unsafe path: {relative}");

            if (File.Exists(destination))
            {
                if (overwriteMode == 1) continue;
                if (overwriteMode == 2) destination = GetUniqueFileName(destination);
            }

            var parent = Path.GetDirectoryName(destination);
            if (parent != null) Directory.CreateDirectory(parent);
            File.Move(stagedFile, destination, overwrite: overwriteMode == 0);
        }
    }

    private sealed class ExtractionStagingDirectory : IDisposable
    {
        public string Path { get; }

        public ExtractionStagingDirectory(string outputDir)
        {
            Path = System.IO.Path.Combine(outputDir, $".gldrive-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            try { File.SetAttributes(Path, File.GetAttributes(Path) | FileAttributes.Hidden); } catch { }
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "Failed to remove extraction staging directory {Path}", Path); }
        }
    }

    /// <summary>
    /// For old-style scene multi-volume RARs (name.rar + name.r00 + name.r01 + ...
    /// + optionally name.s00 + ...) enumerate all volumes in order and return the
    /// complete file list. Returns null if this doesn't look like an old-style set
    /// (e.g. for .partNN.rar modern multi-volume, for single-file RARs, or for
    /// non-RAR archives).
    /// </summary>
    private static List<FileInfo>? TryDiscoverRarVolumes(string firstVolumePath)
    {
        try
        {
            var ext = Path.GetExtension(firstVolumePath);
            if (!ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)) return null;

            var dir = Path.GetDirectoryName(firstVolumePath);
            if (dir == null || !Directory.Exists(dir)) return null;

            var baseName = Path.GetFileNameWithoutExtension(firstVolumePath);
            // Skip modern .partNN.rar naming — SharpCompress handles that via
            // single-path auto-detection. This helper is only for old-style sets.
            if (Regex.IsMatch(baseName, @"\.part\d+$", RegexOptions.IgnoreCase))
                return null;

            var result = new List<FileInfo> { new(firstVolumePath) };

            // Enumerate all files in the directory once to avoid 2000+ File.Exists calls
            var candidates = new DirectoryInfo(dir)
                .EnumerateFiles($"{baseName}.*")
                .ToList();

            // .r00 – .r999 volumes, in numeric order
            var rVolumes = candidates
                .Select(f => new { File = f, Match = Regex.Match(f.Extension, @"^\.r(\d{2,3})$", RegexOptions.IgnoreCase) })
                .Where(x => x.Match.Success)
                .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.File);
            result.AddRange(rVolumes);

            // .s00 – .s999 continuation volumes (used by some releases after r99)
            var sVolumes = candidates
                .Select(f => new { File = f, Match = Regex.Match(f.Extension, @"^\.s(\d{2,3})$", RegexOptions.IgnoreCase) })
                .Where(x => x.Match.Success)
                .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.File);
            result.AddRange(sVolumes);

            return result.Count > 1 ? result : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Extractor: volume discovery failed for {File}", firstVolumePath);
            return null;
        }
    }

    private void ExtractWithReader(ArchiveItem item, Stream stream, string outputDir,
        ReaderOptions options, int overwriteMode, CancellationToken ct)
    {
        var safeDirPath = Path.GetFullPath(outputDir);
        if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
            safeDirPath += Path.DirectorySeparatorChar;
        var processed = 0;
        var lastProgressUpdate = DateTime.MinValue;

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

            using var entryStream = reader.OpenEntryStream();
            ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct);

            processed++;
            var now = DateTime.UtcNow;
            if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
            {
                lastProgressUpdate = now;
                Dispatcher.BeginInvoke(() =>
                {
                    item.EntryCount = processed;
                    item.Progress = -1; // Indeterminate for streaming
                });
            }
        }

        Dispatcher.BeginInvoke(() => item.Progress = 100);
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

    private static bool DeleteSourceFiles(string archivePath)
    {
        return ArchiveExtractor.DeleteArchiveSet(archivePath);
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

            if (!File.Exists(SettingsPath))
            {
                Log.Debug("ExtractorWindow: no settings file at {Path}", SettingsPath);
                return;
            }
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<ExtractorSettings>(json, _jsonOptions);
            if (s == null)
            {
                Log.Warning("ExtractorWindow: settings file parsed to null — {Path}", SettingsPath);
                return;
            }

            foreach (var f in s.WatchFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
                _watchFolders.Add(f);

            if (_watchFolders.Count > 0)
                WatchFoldersText.Text = string.Join("; ", _watchFolders);

            // Set _watchEnabled BEFORE programmatically assigning IsChecked on
            // the checkboxes. Those assignments raise Checked events that would
            // re-enter SaveSettings (via Settings_Changed) if _initialized were
            // somehow true here; defensive either way — the saved state will
            // have WatchEnabled set correctly.
            _watchEnabled = s.WatchEnabled && _watchFolders.Count > 0;

            ChkDeleteAfter.IsChecked = s.DeleteAfterExtract;
            ChkRecursive.IsChecked = s.ScanSubfolders;
            ChkCreateSubfolder.IsChecked = s.CreateSubfolder;
            OutputMode.SelectedIndex = s.OutputMode;
            OverwriteMode.SelectedIndex = s.OverwriteMode;
            if (!string.IsNullOrEmpty(s.CustomOutputPath))
                CustomOutputPath.Text = s.CustomOutputPath;

            Log.Information(
                "ExtractorWindow: loaded settings — watchEnabled={We}, folders={Nf}, deleteAfter={Da}, recursive={Rc}",
                _watchEnabled, _watchFolders.Count, s.DeleteAfterExtract, s.ScanSubfolders);

            // Auto-start watchers if they were enabled.
            // Start the FileSystemWatchers synchronously (fast) so new files
            // immediately get picked up, but offload the initial recursive
            // scan to a background thread. ScanDirectory is O(tree size) and
            // can take minutes on slow/network watch folders — running it
            // inline froze app startup (extractor blocked server mount + IRC
            // connect for ~60s on a network-backed watch folder).
            if (_watchEnabled)
            {
                WatchToggle.Content = "ON";
                WatchToggle.IsChecked = true;
                var recursive = ChkRecursive.IsChecked == true;
                var foldersToScan = _watchFolders.ToList();
                foreach (var folder in foldersToScan)
                    StartWatcherFor(folder);

                _ = Task.Run(() =>
                {
                    try
                    {
                        // Filesystem walk on background thread — no UI touches
                        var discovered = new Dictionary<string, List<string>>();
                        foreach (var folder in foldersToScan)
                        {
                            var paths = new List<string>();
                            CollectArchivePaths(folder, recursive, paths);
                            discovered[folder] = paths;
                        }

                        // Hop to UI thread briefly to add items + fire extracts
                        Dispatcher.Invoke(() =>
                        {
                            var existing = new HashSet<string>(
                                Archives.Select(a => a.FilePath),
                                StringComparer.OrdinalIgnoreCase);

                            foreach (var (folder, paths) in discovered)
                            {
                                foreach (var p in paths)
                                    if (existing.Add(p))
                                        Archives.Add(CreateItem(p));
                            }

                            foreach (var item in Archives.Where(a => a.Status == "Queued").ToList())
                            {
                                if (IsAlreadyExtracted(item))
                                {
                                    item.Status = "Done";
                                    item.Progress = 100;
                                    lock (_watchLock) _watchProcessed.Add(item.FilePath);
                                    continue;
                                }
                                lock (_watchLock) _watchProcessed.Add(item.FilePath);
                                _ = AutoExtractItem(item);
                            }
                            UpdateStatus();
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Initial watch-folder scan failed");
                    }
                });
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
            var tempPath = SettingsPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
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
            WatchToggle.Content = "OFF";
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
            WatchToggle.Content = "ON";
            WatchToggle.IsChecked = true;

            // Scan existing archives in watch folders
            foreach (var folder in _watchFolders)
            {
                StartWatcherFor(folder);
                _ = ScanAndAutoExtractAsync(folder);
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
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += OnWatchedFileCreated;
            watcher.Renamed += OnWatchedFileRenamed;
            watcher.Error += (_, args) =>
            {
                Log.Warning(args.GetException(), "FileSystemWatcher error on {Dir}; scheduling recovery scan", folder);
                _ = RecoverWatcherAsync(folder, watcher);
            };

            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
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

    private void HandleWatchedFile(string path) => _ = HandleWatchedFileAsync(path);

    private async Task HandleWatchedFileAsync(string eventPath)
    {
        var path = ResolveFirstVolumePath(eventPath);
        if (path == null || path.Contains(".gldrive-staging-", StringComparison.OrdinalIgnoreCase)) return;

        lock (_watchLock)
        {
            if (!_watchProcessed.Add(path)) return;
        }

        Log.Information("Extractor: watched archive detected — {Path}", path);

        try
        {
            if (!await WaitForFileReady(path, _lifetimeCts.Token))
            {
                Log.Warning("Extractor: archive was not ready before timeout — {Path}", path);
                ScheduleWatchRetry(path);
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var item = Archives.FirstOrDefault(a =>
                    a.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (item?.Status is "Done" or "Extracting") return;

                if (item == null)
                {
                    item = CreateItem(path);
                    Archives.Add(item);
                }
                else
                {
                    item.Status = "Queued";
                    item.ErrorMessage = null;
                }

                UpdateStatus();
                Log.Information("Extractor: queued for auto-extract — {File}", item.FileName);
                _ = AutoExtractItem(item);
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Extractor: failed to queue watched archive {Path}", path);
            ScheduleWatchRetry(path);
        }
    }

    private async Task ScanAndAutoExtractAsync(string folder)
    {
        try
        {
            var recursive = await Dispatcher.InvokeAsync(() => ChkRecursive.IsChecked == true);
            var paths = await Task.Run(() =>
            {
                var discovered = new List<string>();
                CollectArchivePaths(folder, recursive, discovered);
                return discovered;
            }, _lifetimeCts.Token);

            await Dispatcher.InvokeAsync(() =>
            {
                var existing = new HashSet<string>(Archives.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                    if (existing.Add(path)) Archives.Add(CreateItem(path));

                foreach (var item in Archives.Where(a => a.Status == "Queued" &&
                             paths.Contains(a.FilePath, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    if (IsAlreadyExtracted(item))
                    {
                        item.Status = "Done";
                        item.Progress = 100;
                        lock (_watchLock) _watchProcessed.Add(item.FilePath);
                        continue;
                    }

                    lock (_watchLock)
                    {
                        if (!_watchProcessed.Add(item.FilePath)) continue;
                    }
                    _ = AutoExtractItem(item);
                }
                UpdateStatus();
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Watch-folder recovery scan failed for {Dir}", folder); }
    }

    /// <summary>
    /// Check if every file entry exists in the output directory at its expected size.
    /// Partial output must never suppress a later retry.
    /// </summary>
    private bool IsAlreadyExtracted(ArchiveItem item)
    {
        try
        {
            var outputDir = GetOutputDir(item);
            if (!Directory.Exists(outputDir)) return false;

            var options = new ReaderOptions();
            IArchive archive;
            var volumes = TryDiscoverRarVolumes(item.FilePath);
            if (volumes != null && volumes.Count > 1)
                archive = ArchiveFactory.OpenArchive(volumes, options);
            else
                archive = ArchiveFactory.OpenArchive(item.FilePath, options);
            using var _ = archive;

            var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
            if (entries.Count == 0) return false;
            var safeDir = Path.GetFullPath(outputDir);
            if (!safeDir.EndsWith(Path.DirectorySeparatorChar)) safeDir += Path.DirectorySeparatorChar;
            foreach (var entry in entries)
            {
                var outPath = Path.GetFullPath(Path.Combine(safeDir, entry.Key!));
                if (!outPath.StartsWith(safeDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(outPath))
                    return false;
                if (entry.Size >= 0 && new FileInfo(outPath).Length != entry.Size)
                    return false;
            }
            return true;
        }
        catch (Exception ex) { Log.Debug(ex, "Unable to verify prior extraction for {File}", item.FileName); }
        return false;
    }

    private async Task AutoExtractItem(ArchiveItem item)
    {
        if (item.Status != "Queued") return;
        var gateHeld = false;

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

        Log.Information(
            "Extractor: auto-extract starting — {File} → {Dir} (deleteAfter={Delete})",
            item.FileName, outputDir, deleteAfter);

        try
        {
            await _extractionGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            item.Status = "Extracting";
            item.Progress = 0;
            await ExtractArchiveAsync(item, outputDir, password, overwriteIdx, _lifetimeCts.Token);
            item.Status = "Done";
            item.Progress = 100;

            Log.Information("Extractor: auto-extract completed — {File}", item.FileName);

            if (deleteAfter)
            {
                // Give SharpCompress and filesystem filters a brief settling period;
                // source cleanup also retries transient file-in-use failures.
                await Task.Delay(500, _lifetimeCts.Token);
                Log.Information("Extractor: deleting source archive set for {File}", item.FileName);
                if (!await Task.Run(() => DeleteSourceFiles(item.FilePath), _lifetimeCts.Token))
                    item.ErrorMessage = "Extracted, but source cleanup was incomplete";
            }
            lock (_watchLock) _watchRetryCounts.Remove(item.FilePath);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            item.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-extract failed: {File}", item.FileName);
            item.Status = "Error";
            item.ErrorMessage = ex.Message;
            ScheduleWatchRetry(item.FilePath);
        }
        finally
        {
            if (gateHeld) _extractionGate.Release();
        }

        if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(UpdateStatus);
    }

    /// <summary>
    /// Wait until a file is no longer being written to. Checks every 2 seconds for up to 5 minutes.
    /// Essential for network drives where files appear before the transfer completes.
    /// </summary>
    private static async Task<bool> WaitForFileReady(string path, CancellationToken ct, int maxWaitMs = 300_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSize = -1;
        int stableCount = 0;

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            await Task.Delay(2000, ct);

            try
            {
                if (!File.Exists(path)) return false;

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
                            return true; // File is ready
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
        return false;
    }

    private static string? ResolveFirstVolumePath(string path)
    {
        if (IsArchiveFile(path)) return path;
        var ext = Path.GetExtension(path);
        if (Regex.IsMatch(ext, @"^\.[rs]\d{2,3}$", RegexOptions.IgnoreCase))
            return Path.ChangeExtension(path, ".rar");

        var name = Path.GetFileName(path);
        var match = Regex.Match(name, @"^(?<base>.+\.part)(?<number>\d+)\.rar$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var first = 1.ToString($"D{match.Groups["number"].Value.Length}");
        return Path.Combine(Path.GetDirectoryName(path) ?? "", $"{match.Groups["base"].Value}{first}.rar");
    }

    private void ScheduleWatchRetry(string path)
    {
        int attempt;
        lock (_watchLock)
        {
            _watchProcessed.Remove(path);
            _watchRetryCounts.TryGetValue(path, out attempt);
            attempt++;
            _watchRetryCounts[path] = attempt;
        }
        if (attempt > 5)
        {
            Log.Error("Extractor: giving up after 5 retries for watched archive {Path}", path);
            return;
        }
        _ = RetryWatchedFileAsync(path, attempt);
    }

    private async Task RetryWatchedFileAsync(string path, int attempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30 * attempt), _lifetimeCts.Token);
            await HandleWatchedFileAsync(path);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
    }

    private async Task RecoverWatcherAsync(string folder, FileSystemWatcher failedWatcher)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                failedWatcher.EnableRaisingEvents = false;
                failedWatcher.Dispose();
                _watchers.Remove(failedWatcher);
                if (_watchEnabled && Directory.Exists(folder)) StartWatcherFor(folder);
            });
            if (_watchEnabled) await ScanAndAutoExtractAsync(folder);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Failed to recover watcher for {Dir}", folder); }
    }

    // ── Helpers ──

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    private void CleanFolders_Click(object sender, RoutedEventArgs e)
    {
        var win = new CleanupWindow { Owner = this };
        win.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _lifetimeCts.Cancel();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
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
