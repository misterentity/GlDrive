using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadStore
{
    private static readonly object RegistryLock = new();
    private static readonly List<WeakReference<DownloadStore>> OpenStores = [];
    private readonly string _filePath;
    private volatile bool _savePending;
    private readonly Timer _debounceTimer;
    private readonly object _saveLock = new();
    private volatile bool _closed;
    private volatile bool _loaded;
    private bool _saveWarning;
    private readonly Action<string>? _writeOverride;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<DownloadItem> _items = [];
    private readonly object _lock = new();

    public IReadOnlyList<DownloadItem> Items { get { lock (_lock) return _items.ToList(); } }

    internal (IReadOnlyList<GlDrive.Services.Operations.DossierEvent> Events, bool Partial) ReadDossierEvents(string release)
    {
        lock (_lock)
        {
            var rows = _items.Take(10_000).Where(item => string.Equals(item.ReleaseName, release, StringComparison.OrdinalIgnoreCase))
                .Take(128).Select(item => new GlDrive.Services.Operations.DossierEvent(
                    new DateTimeOffset(item.CompletedAt ?? item.StartedAt ?? item.QueuedAt), "download-current", item.ServerId,
                    $"{item.Status}; {item.DownloadedBytes} of {item.TotalBytes} bytes; retries {item.RetryCount}" )).ToArray();
            return (rows, !_loaded || _items.Count > 10_000 || rows.Length == 128);
        }
    }

    public DownloadStore(string serverId)
        : this(new FileInfo(Path.Combine(ConfigManager.AppDataPath, $"downloads-{serverId}.json")))
    {
    }

    /// <summary>Open an explicitly located queue without consulting the application-state directory.</summary>
    public static DownloadStore ForFile(string filePath) => new(new FileInfo(filePath));

    internal DownloadStore(FileInfo storageFile, Action<string>? writeOverride = null)
    {
        _filePath = storageFile.FullName;
        _writeOverride = writeOverride;
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        // Debounced save: coalesces rapid updates into a single disk write
        _debounceTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
        lock (RegistryLock) OpenStores.Add(new WeakReference<DownloadStore>(this));
    }

    internal static IReadOnlyList<(string Path, IReadOnlyList<DownloadItem> Items)> OpenSnapshots()
    {
        var snapshots = new List<(string, IReadOnlyList<DownloadItem>)>();
        lock (RegistryLock)
        {
            for (var i = OpenStores.Count - 1; i >= 0; i--)
            {
                if (!OpenStores[i].TryGetTarget(out var store) || store._closed)
                {
                    OpenStores.RemoveAt(i);
                    continue;
                }
                // Before Load finishes, the persisted queue remains authoritative.
                if (store._loaded) snapshots.Add((store._filePath, store.Items));
            }
        }
        return snapshots;
    }

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _items = [];
            _loaded = true;
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
            try { File.Copy(_filePath, _filePath + ".corrupt", overwrite: true); }
            catch (Exception copyError) { Log.Debug(copyError, "Could not preserve unreadable download queue"); }
            _items = [];
        }
        _loaded = true;
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
    public void Save() => TrySave();

    /// <summary>Durability acknowledgement for a destructive staged repair; normal saves remain fail-open.</summary>
    internal bool TrySave()
    {
        lock (_saveLock)
        {
            if (_closed) return false;
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return FlushSave();
        }
    }

    private bool FlushSave()
    {
        // Order snapshot creation AND persistence together. Locking just the final
        // file write allowed an older timer snapshot to overwrite a newer Save().
        lock (_saveLock)
        {
            if (_closed) return false;
            try
            {
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_items, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                if (_writeOverride != null) _writeOverride(json);
                else SecureFile.WriteAllTextRestricted(_filePath, json);
                _savePending = false;
                _saveWarning = false;
                return true;
            }
            catch (Exception ex)
            {
                _savePending = true;
                if (!_saveWarning) Log.Warning(ex, "Failed to save downloads; retaining queue state in memory");
                _saveWarning = true;
                return false;
            }
        }
    }

    public void Add(DownloadItem item)
    {
        AddManyInMemory([item]);
        Save(); // Immediate — new item must persist
    }

    // The manager commits the admitted batch while holding its queue lock, then
    // saves once after releasing admission/worker locks.
    internal void AddManyInMemory(IReadOnlyList<DownloadItem> items)
    {
        lock (_lock) _items.AddRange(items);
        _loaded = true;
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

    internal int RemoveTerminal(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        int removed;
        lock (_lock) removed = _items.RemoveAll(item => selected.Contains(item.Id) &&
            item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
        if (removed > 0) Save();
        return removed;
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
