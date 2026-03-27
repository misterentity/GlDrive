using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads;

public class WishlistStore
{
    private static readonly string FilePath =
        Path.Combine(ConfigManager.AppDataPath, "wishlist.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lock _lock = new();
    private List<WishlistItem> _items = [];

    public IReadOnlyList<WishlistItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            lock (_lock) _items = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var items = JsonSerializer.Deserialize<List<WishlistItem>>(json, JsonOptions) ?? [];
            lock (_lock) _items = items;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load wishlist, starting empty");
            lock (_lock) _items = [];
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_items, JsonOptions);

            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wishlist");
        }
    }

    public void Add(WishlistItem item)
    {
        lock (_lock) _items.Add(item);
        Save();
    }

    public void Remove(string id)
    {
        lock (_lock) _items.RemoveAll(i => i.Id == id);
        Save();
    }

    public void Update(WishlistItem item)
    {
        lock (_lock)
        {
            var idx = _items.FindIndex(i => i.Id == item.Id);
            if (idx >= 0)
                _items[idx] = item;
            else
                return;
        }
        Save();
    }

    public WishlistItem? GetById(string id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }
}
