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

    private List<WishlistItem> _items = [];

    public IReadOnlyList<WishlistItem> Items => _items;

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
            _items = JsonSerializer.Deserialize<List<WishlistItem>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load wishlist, starting empty");
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
            Log.Error(ex, "Failed to save wishlist");
        }
    }

    public void Add(WishlistItem item)
    {
        _items.Add(item);
        Save();
    }

    public void Remove(string id)
    {
        _items.RemoveAll(i => i.Id == id);
        Save();
    }

    public void Update(WishlistItem item)
    {
        var idx = _items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0)
        {
            _items[idx] = item;
            Save();
        }
    }

    public WishlistItem? GetById(string id) => _items.FirstOrDefault(i => i.Id == id);
}
