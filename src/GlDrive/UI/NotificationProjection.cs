using System.Collections.ObjectModel;

namespace GlDrive.UI;

/// <summary>Dispatcher-owned bounded history and its filtered, incrementally updated rows.</summary>
internal sealed class NotificationProjection(int capacity = 5000)
{
    private readonly LinkedList<NotificationItemVm> _all = new();
    private readonly Dictionary<string, int> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _servers = new(StringComparer.Ordinal);
    private string _text = "", _category = "All", _server = "All";

    internal ObservableCollection<NotificationItemVm> Items { get; } = new();
    internal ObservableCollection<string> Categories { get; } = new() { "All" };
    internal ObservableCollection<string> Servers { get; } = new() { "All" };

    internal NotificationItemVm? Add(NotificationItemVm item)
    {
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

    private bool Matches(NotificationItemVm item) =>
        (string.IsNullOrEmpty(_text) || item.ReleaseName.Contains(_text, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(_category) || _category == "All" || item.Category == _category) &&
        (string.IsNullOrEmpty(_server) || _server == "All" || item.ServerName == _server);

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
