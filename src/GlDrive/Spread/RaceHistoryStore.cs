using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Spread;

public class RaceHistoryItem
{
    public string Id { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string Section { get; set; } = "";
    public SpreadMode Mode { get; set; }
    public SpreadJobState Result { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public int SiteCount { get; set; }
    public int FilesTransferred { get; set; }
    public long BytesTransferred { get; set; }
    public string SiteNames { get; set; } = "";
    public string SkiplistResult { get; set; } = ""; // "Allowed" or "Denied by: *PATTERN*"
    public List<SkiplistTraceEntry>? SkiplistTrace { get; set; }
}

public class RaceHistoryStore
{
    private static readonly string FilePath = Path.Combine(ConfigManager.AppDataPath, "race-history.json");
    private readonly List<RaceHistoryItem> _items = new();
    private readonly Lock _lock = new();
    private const int MaxItems = 500;

    public IReadOnlyList<RaceHistoryItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var items = JsonSerializer.Deserialize<List<RaceHistoryItem>>(json);
            if (items != null)
            {
                lock (_lock) _items.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load race history");
        }
    }

    public void Add(RaceHistoryItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
            while (_items.Count > MaxItems)
                _items.RemoveAt(_items.Count - 1);
        }
        Save();
    }

    private void Save()
    {
        try
        {
            List<RaceHistoryItem> snapshot;
            lock (_lock) snapshot = _items.ToList();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save race history");
        }
    }
}
