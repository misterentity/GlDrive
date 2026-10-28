namespace GlDrive.Services;

internal sealed class StatsProbeCache
{
    internal sealed record State(int Generation, string? WorkingCommand = null,
        bool ViaListTrailer = false, DateTime UnavailableUntil = default);

    private readonly object _gate = new();
    private State _state = new(0);

    internal State Read() { lock (_gate) return _state; }
    internal void Reset() { lock (_gate) _state = new(_state.Generation + 1); }
    internal bool TryPublish(State state)
    {
        lock (_gate)
        {
            if (state.Generation != _state.Generation) return false;
            _state = state;
            return true;
        }
    }
}
