using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadStore
{
    private readonly string _filePath;
    private volatile bool _savePending;
    private readonly Timer _debounceTimer;
    private readonly object _saveLock = new();
    private bool _closed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<DownloadItem> _items = [];
    private readonly object _lock = new();

    public IReadOnlyList<DownloadItem> Items { get { lock (_lock) return _items.ToList(); } }

    public DownloadStore(string serverId)
    {
        _filePath = Path.Combine(ConfigManager.AppDataPath, $"downloads-{serverId}.json");
        // Debounced save: coalesces rapid updates into a single disk write
        _debounceTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _items = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _items = JsonSerializer.Deserialize<List<DownloadItem>>(json, JsonOptions) ?? [];

            // Reset any items that were in-flight when app closed
            foreach (var item in _items.Where(i => i.Status == DownloadStatus.Downloading))
                item.Status = DownloadStatus.Queued;
            foreach (var item in _items.Where(i => i.Status == DownloadStatus.Extracting))
                item.Status = DownloadStatus.Queued;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load downloads, starting empty");
            _items = [];
        }
    }

    /// <summary>Schedule a debounced save (writes to disk after 2s of inactivity).</summary>
    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            if (_closed) return;
            _savePending = true;
            _debounceTimer.Change(2000, Timeout.Infinite);
        }
    }

    /// <summary>Immediate save — used for critical state changes (add, remove, complete).</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            if (_closed) return;
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            FlushSave();
        }
    }

    private void FlushSave()
    {
        // Order snapshot creation AND persistence together. Locking just the final
        // file write allowed an older timer snapshot to overwrite a newer Save().
        lock (_saveLock)
        {
            if (_closed) return;
            try
            {
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_items, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                SecureFile.WriteAllTextRestricted(_filePath, json);
                _savePending = false;
            }
            catch (Exception ex)
            {
                _savePending = true;
                Log.Error(ex, "Failed to save downloads");
            }
        }
    }

    public void Add(DownloadItem item)
    {
        lock (_lock) _items.Add(item);
        Save(); // Immediate — new item must persist
    }

    public void Update(DownloadItem item)
    {
        bool terminal;
        lock (_lock)
        {
            var idx = _items.FindIndex(i => i.Id == item.Id);
            if (idx < 0) return;
            _items[idx] = item;
            terminal = item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;
        }
        // Debounce progress updates; immediate save for terminal states
        if (terminal) Save(); else ScheduleSave();
    }

    public void Remove(string id)
    {
        lock (_lock) _items.RemoveAll(i => i.Id == id);
        Save(); // Immediate — deletion must persist
    }

    public DownloadItem? GetById(string id) { lock (_lock) return _items.FirstOrDefault(i => i.Id == id); }

    public void RemoveCompleted()
    {
        lock (_lock) _items.RemoveAll(i => i.Status == DownloadStatus.Completed);
        Save();
    }

    public void RemoveFailed()
    {
        lock (_lock) _items.RemoveAll(i => i.Status == DownloadStatus.Failed);
        Save();
    }

    public void RemoveCancelled()
    {
        lock (_lock) _items.RemoveAll(i => i.Status == DownloadStatus.Cancelled);
        Save();
    }

    public void RemoveFinished()
    {
        lock (_lock) _items.RemoveAll(i => i.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
        Save();
    }

    /// <summary>Flush any pending save before shutdown.</summary>
    public void Flush()
    {
        lock (_saveLock)
        {
            if (_closed) return;
            if (_savePending) FlushSave();
            _closed = true;
            _debounceTimer.Dispose();
        }
    }
}
