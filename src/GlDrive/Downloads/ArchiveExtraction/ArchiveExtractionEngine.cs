using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;
using SharpCompress.Common;

namespace GlDrive.Downloads.ArchiveExtraction;

internal sealed class ExtractorEngineOptions
{
    /// <summary>In-memory only: never loads saved settings or the abandon store, never saves.</summary>
    public bool DemoMode { get; init; }

    /// <summary>
    /// False lets automation inspect saved settings without starting saved automation; the
    /// saved WatchEnabled preference is preserved until something explicitly changes it.
    /// </summary>
    public bool StartWatchers { get; init; } = true;

    /// <summary>Holds extractor-settings.json (and the legacy watch-folders.json it migrates).</summary>
    public string SettingsDirectory { get; init; } = ConfigManager.AppDataPath;

    public ExtractAbandonStore? AbandonStore { get; init; }
    public DownloadExtractionGuard? DownloadGuard { get; init; }
    public IExternalArchiveTool? ExternalTool { get; init; }
}

/// <summary>
/// The archive extractor: the queue, manual batch extraction, watch-folder auto-extraction
/// (readiness gates, retries, abandon records, space parking) and the persisted settings.
/// UI-free — <c>ExtractorWindow</c> is a view over one instance and MCP automation drives
/// the same instance through <see cref="ExtractorEngineHost"/>.
///
/// Queue mutations happen on the <see cref="EngineDispatcher"/> context, so a window bound
/// to <see cref="Archives"/> only ever sees changes on its own thread. Events are raised on
/// that context too.
/// </summary>
internal sealed partial class ArchiveExtractionEngine : IDisposable
{
    public ObservableCollection<ArchiveItem> Archives { get; } = new();

    private readonly EngineDispatcher _ui;
    private readonly ArchiveUnpacker _unpacker;
    private readonly bool _demoMode;
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;

    private CancellationTokenSource? _cts;
    private bool _extracting;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _extractionGate = new(1, 1);

    /// <summary>Queue status worth re-rendering (counts of done/failed).</summary>
    public event Action? StatusChanged;

    /// <summary>A manual batch started (true) or ended (false).</summary>
    public event Action<bool>? ExtractingChanged;

    /// <summary>Manual batch progress, 0–100.</summary>
    public event Action<double>? BatchProgressChanged;

    /// <summary>Settings, watch state or password changed by something other than the bound view.</summary>
    public event Action? SettingsChanged;

    // Options. The bound window pushes its controls here as they change; automation writes
    // them through ApplySettings. These are what SaveSettings persists.
    public bool DeleteAfterExtract { get; set; }
    public bool ScanSubfolders { get; set; } = true;
    public bool CreateSubfolder { get; set; }
    public int OutputMode { get; set; }
    public int OverwriteMode { get; set; }
    public string CustomOutputPath { get; set; } = "";
    public string Password { get; set; } = "";

    public bool IsExtracting => _extracting;
    public bool IsDemo => _demoMode;

    internal ArchiveExtractionEngine(SynchronizationContext? ui, ExtractorEngineOptions? options = null)
    {
        options ??= new ExtractorEngineOptions();
        _ui = new EngineDispatcher(ui);
        _unpacker = new ArchiveUnpacker(options.ExternalTool ?? ExternalArchiveTool.Default);
        _demoMode = options.DemoMode;
        _settingsDirectory = options.SettingsDirectory;
        _settingsPath = Path.Combine(_settingsDirectory, "extractor-settings.json");
        _abandonStore = options.AbandonStore ?? new ExtractAbandonStore();
        _downloadGuard = options.DownloadGuard ?? new DownloadExtractionGuard(ConfigManager.AppDataPath);

        if (!_demoMode)
        {
            LoadSettings(options.StartWatchers);
            _abandonStore.Load();
        }
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke();

    // ── Queue ──

    /// <summary>Queue archives and/or every archive under the given folders (ScanSubfolders applies).</summary>
    internal void AddPaths(IEnumerable<string> paths)
    {
        var recursive = ScanSubfolders;
        var existing = new HashSet<string>(Archives.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                ArchiveVolumes.ScanDirectory(path, recursive, file =>
                {
                    if (existing.Add(file)) Archives.Add(ArchiveVolumes.CreateItem(file));
                });
            }
            else if (File.Exists(path) && ArchiveVolumes.IsFirstVolume(path) && existing.Add(path))
            {
                Archives.Add(ArchiveVolumes.CreateItem(path));
            }
        }
        RaiseStatusChanged();
    }

    internal void Remove(ArchiveItem item) => Archives.Remove(item);

    internal void Clear() => Archives.Clear();

    internal string GetOutputDir(ArchiveItem item)
    {
        var archiveDir = Path.GetDirectoryName(item.FilePath) ?? "";
        var archiveName = Path.GetFileNameWithoutExtension(item.FilePath);

        // Strip compound extension (.tar from file.tar.gz)
        if (archiveName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            archiveName = archiveName[..^4];

        return OutputMode switch
        {
            1 => Path.Combine(archiveDir, archiveName),                          // Archive subfolder
            2 when !string.IsNullOrWhiteSpace(CustomOutputPath) =>
                CreateSubfolder
                    ? Path.Combine(CustomOutputPath, archiveName)
                    : CustomOutputPath,
            _ => CreateSubfolder                                                 // Same as archive
                    ? Path.Combine(archiveDir, archiveName)
                    : archiveDir,
        };
    }

    // ── Extraction ──

    /// <summary>Cancel the running manual batch, if any. Safe to call at any time.</summary>
    internal void CancelExtraction()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Extract every queued archive that is not Done, one at a time. Called while a batch is
    /// running it cancels that batch instead (the window's Extract/Cancel button).
    /// </summary>
    internal async Task ExtractAllAsync()
    {
        if (_extracting)
        {
            // Cancel/dispose race guard: the in-flight run's finally block may be
            // disposing _cts at this exact moment. Snapshot the field to a local so
            // we don't read a freshly-nulled field, and swallow ObjectDisposedException
            // in case it was already disposed — Cancel() on a disposed CTS throws, and
            // this is reached from an async void handler so an escape would crash the whole app.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }

        var toExtract = Archives.Where(a => a.Status != "Done").ToList();
        if (toExtract.Count == 0) return;
        // Keep the approved output destinations fixed throughout an asynchronous batch.
        var outputDirectories = toExtract.ToDictionary(item => item, GetOutputDir);

        _extracting = true;
        ExtractingChanged?.Invoke(true);
        // Hold a LOCAL reference to the CTS used by this run. The finally block nulls
        // the _cts field before disposing this local, so a concurrent cancel-click
        // reads null (no-op) rather than touching a half-disposed source. The token is
        // captured once up front and used throughout — IsCancellationRequested is safe
        // to read even after the source is disposed, so the loop never needs the field.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _cts = cts;
        var ct = cts.Token;
        var password = Password;
        var deleteAfter = DeleteAfterExtract;
        var overwriteIdx = OverwriteMode;

        var completed = 0;

        try
        {
            foreach (var item in toExtract)
            {
                if (ct.IsCancellationRequested) break;

                item.Status = "Extracting";
                item.Progress = 0;

                var outputDir = outputDirectories[item];

                try
                {
                    await _extractionGate.WaitAsync(ct);
                    try
                    {
                        await _unpacker.ExtractArchiveAsync(item.FilePath, outputDir, password, overwriteIdx, ItemProgress(item), ct);
                        item.Status = "Done";
                        item.Progress = 100;

                        // A success retires any durable verdict for this path, so a set
                        // that was repaired and extracted never carries a stale record.
                        _abandonStore.Forget(item.FilePath);

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
                // Entered on the caller's context (the UI thread for the window and for
                // automation), so this continuation is back on it after extraction completes.
                BatchProgressChanged?.Invoke(totalPct);
                RaiseStatusChanged();
            }
        }
        finally
        {
            _extracting = false;
            ExtractingChanged?.Invoke(false);
            // Null the field BEFORE disposing the local, so a cancel-click racing this
            // teardown sees null (no-op) instead of calling Cancel() on a disposed CTS.
            // Only clear the field if it still points at our CTS — never clobber a CTS
            // a newer run may have installed.
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            RaiseStatusChanged();
        }
    }

    /// <summary>Progress from the extraction thread, applied to the item on the engine's context.</summary>
    private IProgress<ArchiveProgress> ItemProgress(ArchiveItem item) => new ItemProgressSink(item, _ui);

    private sealed class ItemProgressSink(ArchiveItem item, EngineDispatcher ui) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => ui.Post(() =>
        {
            if (value.EntryCount is { } entries) item.EntryCount = entries;
            if (value.Percent is { } percent) item.Progress = percent;
        });
    }

    /// <summary>
    /// Check if every file entry exists in the output directory at its expected size.
    /// Partial output must never suppress a later retry.
    /// </summary>
    private bool IsAlreadyExtracted(ArchiveItem item)
    {
        try
        {
            return ArchiveUnpacker.AllEntriesPresent(item.FilePath, GetOutputDir(item));
        }
        catch (Exception ex) { Log.Debug(ex, "Unable to verify prior extraction for {File}", item.FileName); }
        return false;
    }

    private static bool DeleteSourceFiles(string archivePath)
    {
        return ArchiveExtractor.DeleteArchiveSet(archivePath);
    }

    // ── Settings ──

    public IReadOnlyList<string> WatchFolders => _watchFolders;
    public bool WatchEnabled => _watchEnabled;
    public bool WatchResumeDeferred => _watchResumeDeferred;

    /// <summary>The settings as they would be saved, except WatchEnabled reports live state.</summary>
    internal ExtractorSettings CurrentSettings() => new()
    {
        WatchFolders = _watchFolders.ToList(), WatchEnabled = _watchEnabled,
        DeleteAfterExtract = DeleteAfterExtract, ScanSubfolders = ScanSubfolders,
        CreateSubfolder = CreateSubfolder, OutputMode = OutputMode,
        OverwriteMode = OverwriteMode, CustomOutputPath = CustomOutputPath
    };

    /// <summary>Replace the password from outside the bound view (automation sets, then clears it).</summary>
    internal void ReplacePassword(string password)
    {
        Password = password;
        RaiseSettingsChanged();
    }

    /// <summary>
    /// Replace every setting (automation's extractor.settings). Watchers stop first; if the new
    /// settings enable watching, it starts exactly as a toggle would. Throws if saving fails.
    /// </summary>
    internal void ApplySettings(ExtractorSettings settings)
    {
        StopAllWatchers();
        _watchResumeDeferred = false;
        _watchEnabled = false;
        _watchFolders.Clear();
        _watchFolders.AddRange(settings.WatchFolders);
        DeleteAfterExtract = settings.DeleteAfterExtract;
        ScanSubfolders = settings.ScanSubfolders;
        CreateSubfolder = settings.CreateSubfolder;
        OutputMode = settings.OutputMode;
        OverwriteMode = settings.OverwriteMode;
        CustomOutputPath = settings.CustomOutputPath;
        RaiseSettingsChanged();
        if (settings.WatchEnabled) ToggleWatch();
        SaveSettings(throwOnError: true);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void LoadSettings(bool startWatchers)
    {
        try
        {
            // Migrate old watch-folders.json if present
            var oldPath = Path.Combine(_settingsDirectory, "watch-folders.json");
            if (File.Exists(oldPath) && !File.Exists(_settingsPath))
            {
                var oldFolders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(oldPath));
                if (oldFolders != null)
                {
                    var settings = new ExtractorSettings { WatchFolders = oldFolders };
                    File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
                    try { File.Delete(oldPath); } catch { }
                }
            }

            if (!File.Exists(_settingsPath))
            {
                Log.Debug("ExtractorWindow: no settings file at {Path}", _settingsPath);
                return;
            }
            var json = File.ReadAllText(_settingsPath);
            var s = JsonSerializer.Deserialize<ExtractorSettings>(json, JsonOptions);
            if (s == null)
            {
                Log.Warning("ExtractorWindow: settings file parsed to null — {Path}", _settingsPath);
                return;
            }

            foreach (var f in s.WatchFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
                _watchFolders.Add(f);

            _watchResumeDeferred = !startWatchers && s.WatchEnabled && _watchFolders.Count > 0;
            _watchEnabled = startWatchers && s.WatchEnabled && _watchFolders.Count > 0;

            DeleteAfterExtract = s.DeleteAfterExtract;
            ScanSubfolders = s.ScanSubfolders;
            CreateSubfolder = s.CreateSubfolder;
            OutputMode = s.OutputMode;
            OverwriteMode = s.OverwriteMode;
            if (!string.IsNullOrEmpty(s.CustomOutputPath))
                CustomOutputPath = s.CustomOutputPath;

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
                StartAbandonSweep();
                var recursive = ScanSubfolders;
                var foldersToScan = _watchFolders.ToList();
                foreach (var folder in foldersToScan)
                    StartWatcherFor(folder);

                _ = Task.Run(() =>
                {
                    try
                    {
                        // Filesystem walk on background thread — no queue touches
                        var discovered = new Dictionary<string, List<string>>();
                        foreach (var folder in foldersToScan)
                        {
                            var paths = new List<string>();
                            ArchiveVolumes.CollectArchivePaths(folder, recursive, paths);
                            discovered[folder] = paths;
                        }

                        // Hop to the engine's context briefly to add items + fire extracts
                        _ui.Send(() =>
                        {
                            var existing = new HashSet<string>(
                                Archives.Select(a => a.FilePath),
                                StringComparer.OrdinalIgnoreCase);

                            foreach (var (folder, paths) in discovered)
                            {
                                foreach (var p in paths)
                                    if (existing.Add(p))
                                        Archives.Add(ArchiveVolumes.CreateItem(p));
                            }

                            foreach (var item in Archives.Where(a => a.Status == "Queued").ToList())
                            {
                                if (ParkForManagedDownload(item.FilePath, item)) continue;
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
                            RaiseStatusChanged();
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

    internal void SaveSettings(bool throwOnError = false)
    {
        if (_demoMode) return;
        try
        {
            var s = new ExtractorSettings
            {
                WatchFolders = _watchFolders,
                WatchEnabled = _watchEnabled || _watchResumeDeferred,
                DeleteAfterExtract = DeleteAfterExtract,
                ScanSubfolders = ScanSubfolders,
                CreateSubfolder = CreateSubfolder,
                OutputMode = OutputMode,
                OverwriteMode = OverwriteMode,
                CustomOutputPath = CustomOutputPath
            };
            Directory.CreateDirectory(_settingsDirectory);
            var json = JsonSerializer.Serialize(s, JsonOptions);
            var tempPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save extractor settings");
            if (throwOnError) throw;
        }
    }

    /// <summary>Save, then stop everything: running extraction, watchers, sweep and retries.</summary>
    public void Dispose()
    {
        if (!_demoMode) SaveSettings();
        _lifetimeCts.Cancel();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        StopAllWatchers();
    }
}
