using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
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
    private int _saveQueued;
    private readonly bool _persistToDisk;
    private readonly Lazy<FeedViewStore> _feedViews;
    public FeedViewStore FeedViews => _feedViews.Value;
    public event Action<NotificationItem?>? Changed;

    /// <summary>
    /// Construct a notification store. <paramref name="persistToDisk"/> defaults
    /// to true (normal app behavior — Load/Save against %AppData%). Pass false
    /// from screenshot / test code that wants to seed in-memory demo entries
    /// without overwriting the user's real notifications.json.
    /// </summary>
    public NotificationStore(bool persistToDisk = true)
    {
        _persistToDisk = persistToDisk;
        _feedViews = new(() => persistToDisk
            ? FeedViewStore.ForFile(Path.Combine(ConfigManager.AppDataPath, "feed-views.json"))
            : new FeedViewStore(() => null, _ => { }));
    }

    public IReadOnlyList<NotificationItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    internal (IReadOnlyList<GlDrive.Services.Operations.DossierEvent> Events, bool Partial) ReadDossierEvents(string release)
    {
        lock (_lock)
        {
            var matches = _items.Take(1000).Where(item => string.Equals(item.ReleaseName, release, StringComparison.OrdinalIgnoreCase)).Take(129).ToArray();
            var partial = _items.Count > 1000 || matches.Length > 128;
            string Text(string? value)
            { if (value is { Length: <= 1024 }) return value; partial = true; return "[oversized or unavailable field omitted]"; }
            var rows = matches.Take(128).Select(item => new GlDrive.Services.Operations.DossierEvent(new DateTimeOffset(item.Timestamp),
                "recent-sighting", Text(item.ServerId), "Observed in category " + Text(item.Category))).ToArray();
            return (rows, partial);
        }
    }

    public void Load()
    {
        List<NotificationItem> loaded;
        if (!File.Exists(FilePath))
        {
            loaded = [];
        }
        else
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                loaded = JsonSerializer.Deserialize<List<NotificationItem>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load notifications, starting empty");
                loaded = [];
            }
        }

        lock (_lock)
        {
            _items = loaded;
        }
    }

    public void Save()
    {
        if (!_persistToDisk) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            SecureFile.WriteAllTextRestricted(FilePath, () =>
            {
                lock (_lock) return JsonSerializer.Serialize(_items, JsonOptions);
            });
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
        Notify(item);
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
        Save();
        Notify(null);
    }

    public int RemoveMany(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        int removed;
        lock (_lock) removed = _items.RemoveAll(item => selected.Contains(item.Id));
        if (removed > 0) { Save(); Notify(null); }
        return removed;
    }

    private void Notify(NotificationItem? item)
    {
        if (Changed == null) return;
        foreach (Action<NotificationItem?> handler in Changed.GetInvocationList())
            try { handler(item); } catch (Exception ex) { Log.Warning(ex, "Notification view observer failed"); }
    }

    private void ScheduleSave()
    {
        if (!_persistToDisk) return;
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // debounce rapid adds
            Interlocked.Exchange(ref _saveQueued, 0);
            Save();
        });
    }
}
