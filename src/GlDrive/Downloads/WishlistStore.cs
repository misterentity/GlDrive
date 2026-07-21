using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
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

    private static readonly Lock Sync = new();
    private static List<WishlistItem> _items = [];
    private static volatile bool _loaded;

    public IReadOnlyList<WishlistItem> Items
    {
        get { EnsureLoaded(); lock (Sync) return _items.ToList(); }
    }

    public void Load()
    {
        lock (Sync)
        {
            if (_loaded) return;

            try
            {
                _items = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<WishlistItem>>(File.ReadAllText(FilePath), JsonOptions) ?? []
                    : [];
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load wishlist, starting empty");
                _items = [];
            }
            _loaded = true;
        }
    }

    public void Save()
    {
        EnsureLoaded();
        try
        {
            lock (Sync)
            {
                var json = JsonSerializer.Serialize(_items, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                SecureFile.WriteAllTextRestricted(FilePath, json);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wishlist");
        }
    }

    public void Add(WishlistItem item)
    {
        EnsureLoaded();
        lock (Sync) _items.Add(item);
        Save();
    }

    public void Remove(string id)
    {
        EnsureLoaded();
        lock (Sync) _items.RemoveAll(i => i.Id == id);
        Save();
    }

    public void Update(WishlistItem item)
    {
        EnsureLoaded();
        lock (Sync)
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
        EnsureLoaded();
        lock (Sync) return _items.FirstOrDefault(i => i.Id == id);
    }

    private static void EnsureLoaded()
    {
        if (!_loaded)
            new WishlistStore().Load();
    }
}
