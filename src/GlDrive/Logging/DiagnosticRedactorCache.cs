namespace GlDrive.Logging;

/// <summary>Versioned diagnostic inventory; event producers only increment a counter.</summary>
internal sealed class DiagnosticRedactorCache(Func<Func<string, string>> build,
    Func<long>? revision = null, Func<DateTimeOffset>? now = null)
{
    private static long _inventoryRevision;
    private static int _inputChanges;
    internal static long InventoryRevision => Interlocked.Read(ref _inventoryRevision);
    internal static void Invalidate() => Interlocked.Increment(ref _inventoryRevision);
    internal static InputChange BeginInputChange() => new();
    internal readonly struct InputChange : IDisposable
    {
        public InputChange() { Interlocked.Increment(ref _inputChanges); Invalidate(); }
        public void Dispose() { Invalidate(); Interlocked.Decrement(ref _inputChanges); }
    }
    internal static readonly Func<string, string> Unavailable = _ => "[redaction unavailable]";
    private readonly object _gate = new();
    private readonly Func<long> _revision = revision ?? (() => InventoryRevision);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private Func<string, string>? _lastGood;
    private long _builtRevision = -1, _attemptedRevision = -1;
    private DateTimeOffset _retryAt;

    internal Func<string, string> Get()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _inputChanges) != 0) return Unavailable;
            var version = _revision();
            var at = _now();
            if (version != _attemptedRevision || at >= _retryAt)
            {
                _attemptedRevision = version;
                _retryAt = at.AddMinutes(1); // Also notices external edits without polling credentials every second.
                try
                {
                    var candidate = build();
                    if (_revision() != version || Volatile.Read(ref _inputChanges) != 0) return Unavailable;
                    // A worker/export may hold this delegate across a settings write.
                    // Check both sides of redaction so an old snapshot cannot leak a new value.
                    _lastGood = text =>
                    {
                        if (_revision() != version || Volatile.Read(ref _inputChanges) != 0) return Unavailable(text);
                        var safe = candidate(text);
                        return _revision() == version && Volatile.Read(ref _inputChanges) == 0 ? safe : Unavailable(text);
                    };
                    _builtRevision = version;
                }
                catch { /* Keep a good inventory only if no known secret input changed. */ }
            }
            return _builtRevision == _revision() ? _lastGood ?? Unavailable : Unavailable;
        }
    }
}
