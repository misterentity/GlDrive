using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadStore
{
    private readonly string _filePath;
    private volatile bool _savePending;
    private readonly Timer _debounceTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<DownloadItem> _items = [];

    public IReadOnlyList<DownloadItem> Items => _items;

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
                item.Status = DownloadStatus.Completed;
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
        _savePending = true;
        _debounceTimer.Change(2000, Timeout.Infinite);
    }

    /// <summary>Immediate save — used for critical state changes (add, remove, complete).</summary>
    public void Save()
    {
        _savePending = false;
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        FlushSave();
    }

    private void FlushSave()
    {
        _savePending = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save downloads");
        }
    }

    public void Add(DownloadItem item)
    {
        _items.Add(item);
        Save(); // Immediate — new item must persist
    }

    public void Update(DownloadItem item)
    {
        var idx = _items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0)
        {
            _items[idx] = item;
            // Debounce progress updates; immediate save for terminal states
            if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
                Save();
            else
                ScheduleSave();
        }
    }

    public void Remove(string id)
    {
        _items.RemoveAll(i => i.Id == id);
        Save(); // Immediate — deletion must persist
    }

    public DownloadItem? GetById(string id) => _items.FirstOrDefault(i => i.Id == id);

    public void RemoveCompleted()
    {
        _items.RemoveAll(i => i.Status == DownloadStatus.Completed);
        Save();
    }

    public void RemoveFailed()
    {
        _items.RemoveAll(i => i.Status == DownloadStatus.Failed);
        Save();
    }

    public void RemoveCancelled()
    {
        _items.RemoveAll(i => i.Status == DownloadStatus.Cancelled);
        Save();
    }

    public void RemoveFinished()
    {
        _items.RemoveAll(i => i.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
        Save();
    }

    /// <summary>Flush any pending save before shutdown.</summary>
    public void Flush()
    {
        if (_savePending) FlushSave();
        _debounceTimer.Dispose();
    }
}
