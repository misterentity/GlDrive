using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads;

public class NotificationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class NotificationStore
{
    private const int MaxItems = 1000;

    private static readonly string FilePath =
        Path.Combine(ConfigManager.AppDataPath, "notifications.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _lock = new();
    private List<NotificationItem> _items = [];
    private int _pendingSaves;

    public IReadOnlyList<NotificationItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                _items = [];
                return;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                _items = JsonSerializer.Deserialize<List<NotificationItem>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load notifications, starting empty");
                _items = [];
            }
        }
    }

    public void Save()
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_items, JsonOptions);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save notifications");
        }
    }

    public void Add(NotificationItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item); // newest first
            if (_items.Count > MaxItems)
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        }
        ScheduleSave();
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
        Save();
    }

    private void ScheduleSave()
    {
        if (Interlocked.Increment(ref _pendingSaves) > 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // debounce rapid adds
            Interlocked.Exchange(ref _pendingSaves, 0);
            Save();
        });
    }
}
