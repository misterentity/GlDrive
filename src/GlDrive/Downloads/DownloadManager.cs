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
    private readonly Channel<DownloadItem> _queue;
    private readonly SemaphoreSlim _concurrency;
    private CancellationTokenSource? _cts;
    private Task? _processorTask;
    private readonly Dictionary<string, CancellationTokenSource> _activeCts = new();

    public event Action<DownloadItem, DownloadProgress>? DownloadProgressChanged;
    public event Action<DownloadItem>? DownloadStatusChanged;

    public DownloadStore Store => _store;

    public DownloadManager(DownloadStore store, FtpOperations ftp, StreamingDownloader downloader, DownloadConfig config)
    {
        _store = store;
        _ftp = ftp;
        _downloader = downloader;
        _config = config;
        _queue = Channel.CreateUnbounded<DownloadItem>();
        _concurrency = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Re-enqueue any queued items from store
        foreach (var item in _store.Items.Where(i => i.Status == DownloadStatus.Queued))
            _queue.Writer.TryWrite(item);

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

    public void Enqueue(DownloadItem item)
    {
        _store.Add(item);
        _queue.Writer.TryWrite(item);
        DownloadStatusChanged?.Invoke(item);
        Log.Information("Enqueued download: {Release}", item.ReleaseName);
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
        item.DownloadedBytes = 0;
        item.ErrorMessage = null;
        _store.Update(item);
        _queue.Writer.TryWrite(item);
        DownloadStatusChanged?.Invoke(item);
    }

    public void RemoveCompleted()
    {
        _store.RemoveCompleted();
    }

    private async Task ProcessLoop(CancellationToken ct)
    {
        var tasks = new List<Task>();

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
            {
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
                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.DownloadedBytes = p.DownloadedBytes;
                    item.TotalBytes = p.TotalBytes;
                    DownloadProgressChanged?.Invoke(item, p);
                });

                await _downloader.DownloadToFile(item.RemotePath, item.LocalPath, progress, itemCts.Token);
            }
            else
            {
                // Directory download — download each file
                item.TotalBytes = dataFiles.Sum(f => f.Size);
                long completedBytes = 0;

                foreach (var file in dataFiles)
                {
                    itemCts.Token.ThrowIfCancellationRequested();

                    var localFilePath = Path.Combine(item.LocalPath, file.Name);
                    long fileCompleted = 0;

                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        fileCompleted = p.DownloadedBytes;
                        item.DownloadedBytes = completedBytes + fileCompleted;
                        DownloadProgressChanged?.Invoke(item, new DownloadProgress(
                            item.DownloadedBytes, item.TotalBytes, p.BytesPerSecond));
                    });

                    await _downloader.DownloadToFile(file.FullName, localFilePath, progress, itemCts.Token);
                    completedBytes += file.Size;
                }
            }

            item.Status = DownloadStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
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
            Log.Error(ex, "Download failed: {Release}", item.ReleaseName);
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            _store.Update(item);
            DownloadStatusChanged?.Invoke(item);
        }
        finally
        {
            lock (_activeCts) _activeCts.Remove(item.Id);
            itemCts.Dispose();
            _concurrency.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _concurrency.Dispose();
        GC.SuppressFinalize(this);
    }
}
