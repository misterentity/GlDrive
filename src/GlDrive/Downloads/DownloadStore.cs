using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads;

public class DownloadStore
{
    private static readonly string FilePath =
        Path.Combine(ConfigManager.AppDataPath, "downloads.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<DownloadItem> _items = [];

    public IReadOnlyList<DownloadItem> Items => _items;

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
            _items = JsonSerializer.Deserialize<List<DownloadItem>>(json, JsonOptions) ?? [];

            // Reset any items that were downloading when app closed
            foreach (var item in _items.Where(i => i.Status == DownloadStatus.Downloading))
                item.Status = DownloadStatus.Queued;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load downloads, starting empty");
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
            Log.Error(ex, "Failed to save downloads");
        }
    }

    public void Add(DownloadItem item)
    {
        _items.Add(item);
        Save();
    }

    public void Update(DownloadItem item)
    {
        var idx = _items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0)
        {
            _items[idx] = item;
            Save();
        }
    }

    public void Remove(string id)
    {
        _items.RemoveAll(i => i.Id == id);
        Save();
    }

    public DownloadItem? GetById(string id) => _items.FirstOrDefault(i => i.Id == id);

    public void RemoveCompleted()
    {
        _items.RemoveAll(i => i.Status == DownloadStatus.Completed);
        Save();
    }
}
