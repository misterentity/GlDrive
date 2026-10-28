using System.Collections.ObjectModel;
using GlDrive.Downloads;

namespace GlDrive.UI;

/// <summary>Dispatcher-owned bounded history and its filtered, incrementally updated rows.</summary>
internal sealed class NotificationProjection(int capacity = 5000)
{
    private readonly LinkedList<NotificationItemVm> _all = new();
    private readonly Dictionary<string, int> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _servers = new(StringComparer.Ordinal);
    private string _text = "", _category = "All", _server = "All";
    private IReadOnlyList<FeedCategoryMute> _mutes = [];

    internal ObservableCollection<NotificationItemVm> Items { get; } = new();
    internal ObservableCollection<string> Categories { get; } = new() { "All" };
    internal ObservableCollection<string> Servers { get; } = new() { "All" };

    internal NotificationItemVm? Add(NotificationItemVm item)
    {
        if (item.Id.Length > 0 && _all.Any(existing => existing.Id == item.Id)) return null;
        _all.AddFirst(item);
        ChangeOption(_categories, Categories, item.Category, 1, _category);
        ChangeOption(_servers, Servers, item.ServerName, 1, _server);
        if (Matches(item)) Items.Insert(0, item);
        if (_all.Count <= capacity) return null;

        var removed = _all.Last!.Value;
        _all.RemoveLast();
        if (Items.Count > 0 && ReferenceEquals(Items[^1], removed)) Items.RemoveAt(Items.Count - 1);
        ChangeOption(_categories, Categories, removed.Category, -1, _category);
        ChangeOption(_servers, Servers, removed.ServerName, -1, _server);
        return removed;
    }

    internal void Reset(IEnumerable<NotificationItemVm> newestFirst)
    {
        _all.Clear();
        _categories.Clear();
        _servers.Clear();
        foreach (var item in newestFirst.Take(capacity))
        {
            _all.AddLast(item);
            ChangeOption(_categories, Categories, item.Category, 1, _category);
            ChangeOption(_servers, Servers, item.ServerName, 1, _server);
        }
        KeepSelectedOption(Categories, _category);
        KeepSelectedOption(Servers, _server);
        RemoveUnusedOptions(Categories, _categories, _category);
        RemoveUnusedOptions(Servers, _servers, _server);
        RebuildItems();
    }

    /// <summary>Reconcile an authoritative store snapshot while retaining row instances and selection.</summary>
    internal void Synchronize(IEnumerable<NotificationItemVm> newestFirst)
    {
        var existing = _all.Where(i => i.Id.Length > 0).DistinctBy(i => i.Id).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var desired = newestFirst.Take(capacity).Select(i => existing.GetValueOrDefault(i.Id) ?? i).ToArray();
        _all.Clear(); _categories.Clear(); _servers.Clear();
        foreach (var item in desired)
        {
            _all.AddLast(item);
            ChangeOption(_categories, Categories, item.Category, 1, _category);
            ChangeOption(_servers, Servers, item.ServerName, 1, _server);
        }
        RemoveUnusedOptions(Categories, _categories, _category);
        RemoveUnusedOptions(Servers, _servers, _server);
        var visible = desired.Where(Matches).ToArray();
        var retained = visible.ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!retained.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < visible.Length; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], visible[i])) continue;
            var old = Items.IndexOf(visible[i]);
            if (old >= 0) Items.Move(old, i); else Items.Insert(i, visible[i]);
        }
    }

    internal void SetFilter(string? text, string? category, string? server)
    {
        text ??= ""; category ??= "All"; server ??= "All";
        if (_text == text && _category == category && _server == server) return;
        var previousCategory = _category;
        var previousServer = _server;
        _text = text; _category = category; _server = server;
        KeepSelectedOption(Categories, _category);
        KeepSelectedOption(Servers, _server);
        if (previousCategory != _category && !_categories.ContainsKey(previousCategory) && previousCategory != "All")
            Categories.Remove(previousCategory);
        if (previousServer != _server && !_servers.ContainsKey(previousServer) && previousServer != "All")
            Servers.Remove(previousServer);
        RebuildItems();
    }

    internal void SetMutedCategories(IReadOnlyList<FeedCategoryMute> mutes)
    {
        if (_mutes.SequenceEqual(mutes)) return;
        _mutes = mutes.ToArray();
        RebuildItems();
    }

    private bool Matches(NotificationItemVm item) =>
        FeedViewStore.Matches(_text, _category, _server, item.ReleaseName, item.Category, item.ServerName) &&
        !FeedViewStore.IsMuted(_mutes, item.ServerId, item.Category);

    private void RebuildItems()
    {
        Items.Clear();
        foreach (var item in _all)
            if (Matches(item)) Items.Add(item);
    }

    private static void ChangeOption(Dictionary<string, int> counts, ObservableCollection<string> options,
        string value, int delta, string selected)
    {
        if (string.IsNullOrEmpty(value)) return;
        counts.TryGetValue(value, out var count);
        count += delta;
        if (count > 0)
        {
            counts[value] = count;
            if (count == 1 && delta > 0) KeepSelectedOption(options, value);
        }
        else
        {
            counts.Remove(value);
            // Removing the selected ComboBox option would silently change the user's filter.
            if (value != selected && value != "All") options.Remove(value);
        }
    }

    private static void KeepSelectedOption(ObservableCollection<string> options, string value)
    {
        if (string.IsNullOrEmpty(value) || options.Contains(value)) return;
        var index = 1; // "All" always stays first; match the old current-culture ordering.
        while (index < options.Count && StringComparer.CurrentCulture.Compare(options[index], value) <= 0) index++;
        options.Insert(index, value);
    }

    private static void RemoveUnusedOptions(ObservableCollection<string> options,
        Dictionary<string, int> counts, string selected)
    {
        for (var i = options.Count - 1; i > 0; i--)
            if (options[i] != selected && !counts.ContainsKey(options[i])) options.RemoveAt(i);
    }
}
