using System.IO;
using System.Threading.Channels;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadManager : IDisposable
{
    private readonly DownloadStore _store;
    private readonly FtpOperations _ftp;
    private readonly StreamingDownloader _downloader;
    private readonly DownloadConfig _config;
    private readonly DownloadHistoryStore? _historyStore;
    private readonly List<DownloadItem> _pendingQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _concurrency;
    private CancellationTokenSource? _cts;
    private Task? _processorTask;
    private readonly Dictionary<string, CancellationTokenSource> _activeCts = new();

    public event Action<DownloadItem, DownloadProgress>? DownloadProgressChanged;
    public event Action<DownloadItem>? DownloadStatusChanged;

    public DownloadStore Store => _store;

    public DownloadManager(DownloadStore store, FtpOperations ftp, StreamingDownloader downloader,
        DownloadConfig config, DownloadHistoryStore? historyStore = null)
    {
        _store = store;
        _ftp = ftp;
        _downloader = downloader;
        _config = config;
        _historyStore = historyStore;
        _concurrency = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Re-enqueue any queued items from store
        foreach (var item in _store.Items.Where(i => i.Status == DownloadStatus.Queued))
        {
            lock (_queueLock) _pendingQueue.Add(item);
            _queueSignal.Release();
        }

        _processorTask = ProcessLoop(_cts.Token);
        Log.Information("DownloadManager started (max concurrent: {Max})", _config.MaxConcurrentDownloads);
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _cts?.Cancel();
        lock (_activeCts)
        {
            foreach (var cts in _activeCts.Values)
                cts.Cancel();
            _activeCts.Clear();
        }
        if (_processorTask != null)
        {
            try
            {
                await _processorTask.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warning("DownloadManager stop timed out — abandoning background task");
            }
            catch { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        lock (_activeCts)
        {
            foreach (var cts in _activeCts.Values)
                cts.Cancel();
            _activeCts.Clear();
        }
        _cts?.Dispose();
        _cts = null;
    }

    public bool Enqueue(DownloadItem item)
    {
        // Duplicate detection: check for matching RemotePath with active status
        var existing = _store.Items.FirstOrDefault(i =>
            i.RemotePath == item.RemotePath &&
            i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting);
        if (existing != null)
        {
            Log.Information("Skipped duplicate download: {Release} (already {Status})", item.ReleaseName, existing.Status);
            return false;
        }

        // Check if local path already exists
        if (Directory.Exists(item.LocalPath))
        {
            Log.Information("Skipped download: {Release} (local path already exists)", item.ReleaseName);
            return false;
        }

        _store.Add(item);
        lock (_queueLock) _pendingQueue.Add(item);
        _queueSignal.Release();
        DownloadStatusChanged?.Invoke(item);
        Log.Information("Enqueued download: {Release}", item.ReleaseName);
        return true;
    }

    public void Cancel(string id)
    {
        var item = _store.GetById(id);
        if (item == null) return;

        lock (_activeCts)
        {
            if (_activeCts.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                _activeCts.Remove(id);
            }
        }

        item.Status = DownloadStatus.Cancelled;
        _store.Update(item);
        DownloadStatusChanged?.Invoke(item);
    }

    public void Retry(string id)
    {
        var item = _store.GetById(id);
        if (item == null || (item.Status != DownloadStatus.Failed && item.Status != DownloadStatus.Cancelled)) return;

        item.Status = DownloadStatus.Queued;
        // Don't reset DownloadedBytes — resume will pick up from partial
        item.ErrorMessage = null;
        _store.Update(item);
        lock (_queueLock) _pendingQueue.Add(item);
        _queueSignal.Release();
        DownloadStatusChanged?.Invoke(item);
    }

    public void RemoveCompleted()
    {
        _store.RemoveCompleted();
    }

    public void MoveUp(string id)
    {
        lock (_queueLock)
        {
            var idx = _pendingQueue.FindIndex(i => i.Id == id);
            if (idx > 0)
            {
                (_pendingQueue[idx - 1], _pendingQueue[idx]) = (_pendingQueue[idx], _pendingQueue[idx - 1]);
            }
        }
    }

    public void MoveDown(string id)
    {
        lock (_queueLock)
        {
            var idx = _pendingQueue.FindIndex(i => i.Id == id);
            if (idx >= 0 && idx < _pendingQueue.Count - 1)
            {
                (_pendingQueue[idx], _pendingQueue[idx + 1]) = (_pendingQueue[idx + 1], _pendingQueue[idx]);
            }
        }
    }

    private async Task ProcessLoop(CancellationToken ct)
    {
        var tasks = new List<Task>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(ct);

                // Schedule check: if scheduling enabled and outside window, delay
                if (_config.ScheduleEnabled)
                {
                    var hour = DateTime.Now.Hour;
                    bool inWindow = _config.ScheduleStartHour <= _config.ScheduleEndHour
                        ? hour >= _config.ScheduleStartHour && hour < _config.ScheduleEndHour
                        : hour >= _config.ScheduleStartHour || hour < _config.ScheduleEndHour;
                    if (!inWindow)
                    {
                        // Re-signal so we check again later
                        _queueSignal.Release();
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                        continue;
                    }
                }

                DownloadItem? item;
                lock (_queueLock)
                {
                    if (_pendingQueue.Count == 0) continue;
                    item = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                }

                // Skip if no longer queued (cancelled, etc.)
                var fresh = _store.GetById(item.Id);
                if (fresh == null || fresh.Status != DownloadStatus.Queued) continue;

                await _concurrency.WaitAsync(ct);
                tasks.Add(ProcessItem(fresh, ct));
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) { }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessItem(DownloadItem item, CancellationToken globalCt)
    {
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
        lock (_activeCts) _activeCts[item.Id] = itemCts;

        try
        {
            item.Status = DownloadStatus.Downloading;
            item.StartedAt = DateTime.UtcNow;
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);

            // List files in the release directory
            var files = await _ftp.ListDirectory(item.RemotePath, itemCts.Token);
            var dataFiles = files.Where(f => f.Type == FluentFTP.FtpObjectType.File).ToList();

            if (dataFiles.Count == 0)
            {
                // Single file download
                var fileName = Path.GetFileName(item.RemotePath);

                // Check for existing partial file for resume
                long resumeOffset = 0;
                var localFile = new FileInfo(item.LocalPath);
                if (localFile.Exists && localFile.Length > 0)
                    resumeOffset = localFile.Length;

                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.DownloadedBytes = p.DownloadedBytes;
                    item.TotalBytes = p.TotalBytes;
                    DownloadProgressChanged?.Invoke(item, p with { CurrentFileName = fileName });
                });

                await _downloader.DownloadToFile(item.RemotePath, item.LocalPath, resumeOffset, progress, itemCts.Token);
            }
            else
            {
                // NFO pre-check
                if (_config.SkipIncompleteReleases)
                {
                    var hasNfo = dataFiles.Any(f => f.Name.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));
                    if (!hasNfo)
                    {
                        item.Status = DownloadStatus.Failed;
                        item.ErrorMessage = "Release appears incomplete (no .nfo file found)";
                        _store.Update(item);
                        DownloadStatusChanged?.Invoke(item);
                        AddHistoryEntry(item);
                        return;
                    }
                }

                // Directory download — calculate total
                item.TotalBytes = dataFiles.Sum(f => f.Size);

                // Disk space check
                var targetRoot = Path.GetPathRoot(item.LocalPath);
                if (!string.IsNullOrEmpty(targetRoot))
                {
                    try
                    {
                        var driveInfo = new DriveInfo(targetRoot);
                        if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < item.TotalBytes)
                        {
                            item.Status = DownloadStatus.Failed;
                            item.ErrorMessage = $"Insufficient disk space: need {FormatSize(item.TotalBytes)}, have {FormatSize(driveInfo.AvailableFreeSpace)}";
                            _store.Update(item);
                            DownloadStatusChanged?.Invoke(item);
                            AddHistoryEntry(item);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Could not check disk space for {Path}", targetRoot);
                    }
                }

                long completedBytes = 0;

                foreach (var file in dataFiles)
                {
                    itemCts.Token.ThrowIfCancellationRequested();

                    var localFilePath = Path.Combine(item.LocalPath, file.Name);

                    // Resume: check existing file
                    long resumeOffset = 0;
                    var existingFile = new FileInfo(localFilePath);
                    if (existingFile.Exists)
                    {
                        if (existingFile.Length >= file.Size)
                        {
                            // File already fully downloaded
                            completedBytes += file.Size;
                            item.DownloadedBytes = completedBytes;
                            continue;
                        }
                        resumeOffset = existingFile.Length;
                    }

                    long fileCompleted = resumeOffset;

                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        fileCompleted = p.DownloadedBytes;
                        item.DownloadedBytes = completedBytes + fileCompleted;
                        DownloadProgressChanged?.Invoke(item, new DownloadProgress(
                            item.DownloadedBytes, item.TotalBytes, p.BytesPerSecond, file.Name));
                    });

                    await _downloader.DownloadToFile(file.FullName, localFilePath, resumeOffset, progress, itemCts.Token);
                    completedBytes += file.Size;
                }
            }

            // SFV verification if enabled
            if (_config.VerifySfv && Directory.Exists(item.LocalPath))
            {
                try
                {
                    var failures = await SfvVerifier.VerifyAsync(item.LocalPath, itemCts.Token);
                    if (failures.Count > 0)
                    {
                        foreach (var f in failures)
                            Log.Warning("SFV verification failed: {File}", f);
                    }
                    else
                    {
                        Log.Information("SFV verification passed for {Release}", item.ReleaseName);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SFV verification error for {Release}", item.ReleaseName);
                }
            }

            // Auto-extract RAR archives if enabled
            if (_config.AutoExtract && Directory.Exists(item.LocalPath))
            {
                try
                {
                    item.Status = DownloadStatus.Extracting;
                    _store.Update(item);
                    DownloadStatusChanged?.Invoke(item);

                    var extracted = await ArchiveExtractor.ExtractIfNeeded(item.LocalPath, itemCts.Token);
                    if (extracted && _config.DeleteArchivesAfterExtract)
                        ArchiveExtractor.DeleteArchives(item.LocalPath);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Extraction failed for {Release} — marking completed anyway", item.ReleaseName);
                }
            }

            item.Status = DownloadStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
            AddHistoryEntry(item);
            Log.Information("Download completed: {Release}", item.ReleaseName);
        }
        catch (OperationCanceledException)
        {
            if (item.Status == DownloadStatus.Downloading)
            {
                item.Status = DownloadStatus.Cancelled;
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
            }
        }
        catch (Exception ex)
        {
            // Auto-retry with backoff
            if (item.RetryCount < _config.MaxRetries)
            {
                item.RetryCount++;
                var delay = _config.RetryDelaySeconds * item.RetryCount;
                Log.Warning(ex, "Download failed: {Release} — retry {Attempt}/{Max} in {Delay}s",
                    item.ReleaseName, item.RetryCount, _config.MaxRetries, delay);
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = $"Retry {item.RetryCount}/{_config.MaxRetries}: {ex.Message}";
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), _cts?.Token ?? CancellationToken.None);
                        lock (_queueLock) _pendingQueue.Add(item);
                        _queueSignal.Release();
                    }
                    catch (OperationCanceledException) { }
                });
            }
            else
            {
                Log.Error(ex, "Download failed: {Release} (no retries left)", item.ReleaseName);
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                _store.Update(item);
                DownloadStatusChanged?.Invoke(item);
                AddHistoryEntry(item);
            }
        }
        finally
        {
            lock (_activeCts) _activeCts.Remove(item.Id);
            itemCts.Dispose();
            _concurrency.Release();
        }
    }

    private void AddHistoryEntry(DownloadItem item)
    {
        if (_historyStore == null) return;
        if (item.Status != DownloadStatus.Completed && item.Status != DownloadStatus.Failed) return;

        _historyStore.Add(new DownloadHistoryItem
        {
            ReleaseName = item.ReleaseName,
            ServerName = item.ServerName,
            Category = item.Category,
            TotalBytes = item.TotalBytes,
            LocalPath = item.LocalPath,
            FinalStatus = item.Status.ToString(),
            ErrorMessage = item.ErrorMessage,
            CompletedAt = DateTime.UtcNow
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    public void Dispose()
    {
        Stop();
        _concurrency.Dispose();
        _queueSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}
