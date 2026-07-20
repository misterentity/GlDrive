namespace GlDrive.Irc;

/// <summary>
/// Service-lifetime per-target message ring buffer. IrcService appends every
/// user-visible message here (incoming post-decrypt, own-send echoes, system
/// lines) before raising MessageReceived, so a reopened Dashboard can hydrate
/// full typed history — including PMs — instead of losing everything with the
/// disposed view-model. Never cleared on reconnect; dies with the IrcService.
/// </summary>
public class IrcScrollbackBuffer
{
    public const int DefaultMaxPerTarget = 500;

    private readonly Dictionary<string, List<IrcMessageItem>> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly int _maxPerTarget;

    public IrcScrollbackBuffer(int maxPerTarget = DefaultMaxPerTarget)
    {
        _maxPerTarget = maxPerTarget;
    }

    /// <summary>Empty targets map to "*" (the per-server status window) so no message lands in a phantom bucket.</summary>
    public static string NormalizeTarget(string? target) =>
        string.IsNullOrEmpty(target) ? "*" : target;

    public void Append(string? target, IrcMessageItem item)
    {
        var key = NormalizeTarget(target);
        lock (_lock)
        {
            if (!_buffers.TryGetValue(key, out var buffer))
                _buffers[key] = buffer = [];
            buffer.Add(item);
            if (buffer.Count > _maxPerTarget)
                buffer.RemoveAt(0);
        }
    }

    public IReadOnlyList<string> Targets
    {
        get
        {
            lock (_lock)
                return _buffers.Keys.ToList();
        }
    }

    public IReadOnlyList<IrcMessageItem> Snapshot(string target)
    {
        lock (_lock)
        {
            return _buffers.TryGetValue(NormalizeTarget(target), out var buffer)
                ? buffer.ToList()
                : [];
        }
    }
}
