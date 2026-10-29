using System.IO;
using System.Text.Json;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads;

public sealed record FeedView(string Id, string Name, string Text, string Category, string ServerName,
    DateTime SavedAtUtc, IReadOnlyList<string> ReadIds);
public sealed record FeedCategoryMute(string ServerId, string Category, DateTime CreatedAtUtc);
public sealed record FeedViewCount(FeedView View, int Unread, int Visible);

/// <summary>User-owned presentation preferences. Never consulted by acquisition or alert admission.</summary>
public sealed class FeedViewStore
{
    internal const int MaximumViews = 100;
    internal const int MaximumMutes = 512;
    internal const int MaximumReadIds = 5000;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private readonly Lock _sync = new();
    private readonly Action<string, bool> _write;
    private GuardedJsonFile? _guardedFile;
    private bool _loadTrimmed;
    private bool _warned;
    private List<FeedView> _views = [];
    private List<FeedCategoryMute> _mutes = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Saved(List<FeedView> Views, List<FeedCategoryMute> MutedCategories);

    public static FeedViewStore ForFile(string path, Func<bool>? guardEnabled = null)
    {
        var guarded = new GuardedJsonFile(path, json =>
        {
            var saved = Parse(json);
            return saved.Views.Count + saved.MutedCategories.Count;
        }, MaximumBytes, guardEnabled);
        return new FeedViewStore(guarded.Read, guarded.Write) { _guardedFile = guarded };
    }

    public Task FlushPendingWritesAsync() => _guardedFile?.FlushPendingWritesAsync() ?? Task.CompletedTask;

    internal FeedViewStore(Func<string?> read, Action<string> write, Action? quarantine = null)
        : this(read, (json, _) => write(json), quarantine) { }

    private FeedViewStore(Func<string?> read, Action<string, bool> write, Action? quarantine = null)
    {
        _write = write;
        try
        {
            var json = read();
            if (json == null) return;
            var saved = Parse(json);
            _views = saved.Views.OrderByDescending(v => v.SavedAtUtc).DistinctBy(v => v.Id)
                .Take(MaximumViews).Select(v => v with { ReadIds = v.ReadIds.Distinct(StringComparer.Ordinal).Take(MaximumReadIds).ToArray() }).ToList();
            _mutes = saved.MutedCategories.OrderByDescending(m => m.CreatedAtUtc)
                .DistinctBy(m => (m.ServerId, m.Category.ToUpperInvariant())).Take(MaximumMutes).ToList();
            _loadTrimmed = saved.Views.Count != _views.Count || saved.MutedCategories.Count != _mutes.Count;
        }
        catch (Exception ex)
        {
            try { quarantine?.Invoke(); } catch { }
            _views = []; _mutes = [];
            Log.Warning(ex, "Feed preferences unreadable; preserved a corrupt copy and starting empty");
        }
    }

    public event Action? Changed;
    public IReadOnlyList<FeedView> Views { get { lock (_sync) return _views.Select(Clone).ToArray(); } }
    public IReadOnlyList<FeedCategoryMute> MutedCategories { get { lock (_sync) return _mutes.ToArray(); } }

    public FeedView Save(string? id, string name, string text = "", string category = "All", string serverName = "All")
    {
        Validate(name, 80); Validate(text, 512, true); Validate(category, 128, true); Validate(serverName, 256, true);
        if (id != null) Validate(id, 128);
        FeedView result;
        lock (_sync)
        {
            var previous = id == null ? null : _views.FirstOrDefault(v => v.Id == id)
                ?? throw new ArgumentException("Saved view was not found");
            result = new(id ?? Guid.NewGuid().ToString("N"), name.Trim(), text, category, serverName,
                DateTime.UtcNow, previous?.ReadIds.ToArray() ?? []);
            _views.RemoveAll(v => v.Id == result.Id);
            _views.Add(result);
            while (_views.Count > MaximumViews) _views.Remove(_views.Where(v => v.Id != result.Id).MinBy(v => v.SavedAtUtc)!);
            PersistNoLock(result.Id);
        }
        Notify();
        return Clone(result);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_sync) { removed = _views.RemoveAll(v => v.Id == id) > 0; if (removed) PersistNoLock(allowShrink: true); }
        if (removed) Notify();
        return removed;
    }

    public bool MarkRead(string id, IEnumerable<NotificationItem> retainedItems, IEnumerable<string>? displayedIds = null)
    {
        var retained = retainedItems.Take(MaximumReadIds).ToArray();
        var displayed = displayedIds?.ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            var index = _views.FindIndex(v => v.Id == id);
            if (index < 0) return false;
            var view = _views[index];
            var retainedIds = retained.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var read = view.ReadIds.Where(retainedIds.Contains).ToHashSet(StringComparer.Ordinal);
            foreach (var item in retained.Where(n => (displayed == null || displayed.Contains(n.Id)) &&
                         Matches(view, n) && !IsMuted(_mutes, n.ServerId, n.Category))) read.Add(item.Id);
            _views[index] = view with { ReadIds = read.ToArray() };
            PersistNoLock(id);
        }
        Notify();
        return true;
    }

    public void SetMuted(string serverId, string category, bool muted)
    {
        Validate(serverId, 128); Validate(category, 128);
        lock (_sync)
        {
            _mutes.RemoveAll(m => m.ServerId == serverId && m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            var added = muted ? new FeedCategoryMute(serverId, category, DateTime.UtcNow) : null;
            if (added != null) _mutes.Add(added);
            while (_mutes.Count > MaximumMutes) _mutes.Remove(_mutes.Where(m => !ReferenceEquals(m, added)).MinBy(m => m.CreatedAtUtc)!);
            PersistNoLock(protectedMute: added, allowShrink: !muted);
        }
        Notify();
    }

    public void ClearMutes()
    {
        lock (_sync) { _mutes.Clear(); PersistNoLock(allowShrink: true); }
        Notify();
    }

    public IReadOnlyList<FeedViewCount> Counts(IEnumerable<NotificationItem> retainedItems)
    {
        var retained = retainedItems.ToArray();
        lock (_sync)
            return _views.Select(view =>
            {
                var visible = retained.Where(n => Matches(view, n) && !IsMuted(_mutes, n.ServerId, n.Category)).ToArray();
                var read = view.ReadIds.ToHashSet(StringComparer.Ordinal);
                return new FeedViewCount(Clone(view), visible.Count(n => !read.Contains(n.Id)), visible.Length);
            }).ToArray();
    }

    public static bool Matches(FeedView view, NotificationItem item) => Matches(view.Text, view.Category, view.ServerName,
        item.ReleaseName, item.Category, item.ServerName);
    internal static bool Matches(string text, string category, string serverName, string release, string itemCategory, string itemServer) =>
        (text.Length == 0 || release.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
        (category.Length == 0 || category == "All" || itemCategory == category) &&
        (serverName.Length == 0 || serverName == "All" || itemServer == serverName);
    public static bool IsMuted(IEnumerable<FeedCategoryMute> mutes, string serverId, string category) =>
        mutes.Any(m => m.ServerId == serverId && m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    private static FeedView Clone(FeedView view) => view with { ReadIds = view.ReadIds.ToArray() };
    private static Saved Parse(string json)
    {
        var saved = JsonSerializer.Deserialize<Saved>(json, Json) ?? throw new JsonException("Missing feed preferences");
        if (saved.Views == null || saved.MutedCategories == null) throw new JsonException("Invalid feed preferences");
        foreach (var view in saved.Views)
        {
            if (view == null) throw new JsonException("Invalid feed view");
            Validate(view.Id, 128); Validate(view.Name, 80); Validate(view.Text, 512, allowEmpty: true);
            Validate(view.Category, 128, allowEmpty: true); Validate(view.ServerName, 256, allowEmpty: true);
            if (view.ReadIds == null || view.ReadIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
                throw new JsonException("Invalid feed read markers");
        }
        foreach (var mute in saved.MutedCategories)
        {
            if (mute == null) throw new JsonException("Invalid feed mute");
            Validate(mute.ServerId, 128); Validate(mute.Category, 128);
        }
        return saved;
    }
    private static void Validate(string value, int length, bool allowEmpty = false)
    {
        if (value == null || value.Length > length || !allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Feed preference value is missing or too long");
    }
    private void PersistNoLock(string? protectedViewId = null, FeedCategoryMute? protectedMute = null, bool allowShrink = false)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Saved(_views, _mutes), Json);
            // The byte bound matches the reader. Evict old preferences, never reject a
            // new view or let a valid saved file be quarantined on the next launch.
            while (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            {
                var view = _views.Where(v => v.Id != protectedViewId).MinBy(v => v.SavedAtUtc);
                var mute = _mutes.Where(m => !ReferenceEquals(m, protectedMute)).MinBy(m => m.CreatedAtUtc);
                if (view != null && (mute == null || view.SavedAtUtc <= mute.CreatedAtUtc)) _views.Remove(view);
                else if (mute != null) _mutes.Remove(mute);
                else break;
                allowShrink = true; // The established capacity policy deliberately evicts older preferences.
                json = JsonSerializer.Serialize(new Saved(_views, _mutes), Json);
            }
            _write(json, allowShrink || _loadTrimmed);
            _loadTrimmed = false;
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; Log.Warning(ex, "Feed preferences could not be saved; keeping changes in memory"); }
        }
    }
    private void Notify()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
            try { handler(); } catch (Exception ex) { Log.Warning(ex, "Feed view observer failed"); }
    }
}
