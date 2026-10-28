namespace GlDrive.Downloads;

/// <summary>Bounded, shared evaluation memory for IRC and listing detections.</summary>
internal sealed class WishlistReleaseDeduplicator(int capacity = 8192)
{
    private readonly Dictionary<string, LinkedListNode<string>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _invalidated = new(StringComparer.OrdinalIgnoreCase);

    // The owning WishlistStore serializes access with its shared state lock.
    internal bool TryBegin(string itemId, string release)
    {
        var key = Key(itemId, release);
        return !_seen.ContainsKey(key) && _active.Add(key);
    }

    internal void Complete(string itemId, string release)
    {
        var key = Key(itemId, release);
        if (!_active.Remove(key) || _invalidated.Remove(key)) return;
        _seen.Add(key, _order.AddLast(key));
        while (_seen.Count > Math.Max(1, capacity))
        {
            _seen.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
    }

    internal void Forget(string itemId, string release)
    {
        var key = Key(itemId, release);
        _active.Remove(key);
        _invalidated.Remove(key);
        if (_seen.Remove(key, out var node)) _order.Remove(node);
    }

    internal void ForgetItem(string itemId)
    {
        foreach (var key in _active.Where(k => k.StartsWith(itemId + "\0", StringComparison.OrdinalIgnoreCase)))
            _invalidated.Add(key);
        foreach (var key in _seen.Keys.Where(k => k.StartsWith(itemId + "\0", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _order.Remove(_seen[key]);
            _seen.Remove(key);
        }
    }

    private static string Key(string itemId, string release) => itemId + "\0" + release;
}
