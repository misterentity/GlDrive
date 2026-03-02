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

    private List<NotificationItem> _items = [];

    public IReadOnlyList<NotificationItem> Items => _items;

    public void Load()
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save notifications");
        }
    }

    public void Add(NotificationItem item)
    {
        _items.Insert(0, item); // newest first
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }
}
