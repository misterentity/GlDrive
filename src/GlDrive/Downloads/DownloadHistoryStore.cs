using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ReleaseName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Category { get; set; } = "";
    public long TotalBytes { get; set; }
    public string LocalPath { get; set; } = "";
    public string FinalStatus { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public class DownloadHistoryStore
{
    private const int MaxItems = 500;

    private static readonly string FilePath =
        Path.Combine(ConfigManager.AppDataPath, "download-history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<DownloadHistoryItem> _items = [];

    public IReadOnlyList<DownloadHistoryItem> Items => _items;

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
            _items = JsonSerializer.Deserialize<List<DownloadHistoryItem>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load download history, starting empty");
            _items = [];
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save download history");
        }
    }

    public void Add(DownloadHistoryItem item)
    {
        _items.Insert(0, item);
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
