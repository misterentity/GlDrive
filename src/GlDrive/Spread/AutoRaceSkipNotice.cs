namespace GlDrive.Spread;

/// <summary>
/// Decides which auto-race skips are worth a warning. A peer outage skips every announce,
/// so warn when the set of unavailable sites changes (or hourly while it persists) and let
/// the rest log at Debug.
/// </summary>
internal sealed class AutoRaceSkipNotice
{
    internal static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(1);

    private readonly Lock _sync = new();
    private string? _signature;
    private DateTime _lastWarningUtc;
    private int _suppressed;

    /// <summary>Returns the number of skips suppressed since the last warning when this one should warn; otherwise null.</summary>
    internal int? Record(IEnumerable<string> unavailableSites, DateTime nowUtc)
    {
        var signature = string.Join('\n', unavailableSites
            .Select(s => s.ToUpperInvariant()).Order(StringComparer.Ordinal));
        lock (_sync)
        {
            if (_signature == signature && nowUtc - _lastWarningUtc < RepeatInterval)
            {
                _suppressed++;
                return null;
            }
            var suppressed = _suppressed;
            _signature = signature;
            _lastWarningUtc = nowUtc;
            _suppressed = 0;
            return suppressed;
        }
    }

    /// <summary>Call once enough sites are connected again, so the next outage warns immediately.</summary>
    internal void Reset()
    {
        lock (_sync)
        {
            _signature = null;
            _suppressed = 0;
        }
    }
}
