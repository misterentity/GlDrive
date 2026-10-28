namespace GlDrive.Spread;

public sealed record SitebotReleaseFacts
{
    public string ServerId { get; init; } = "";
    public string Release { get; init; } = "";
    public SitebotEvent? Latest { get; init; }
    public SitebotEvent? Complete { get; init; }
    public SitebotEvent? Metadata { get; init; }
    public bool ConflictingWinner { get; init; }
    public long? AnnouncedBytes => Complete?.Bytes;
    public int? AnnouncedFiles => Complete?.Files;
}

/// <summary>Bounded passive evidence from already authenticated channel events. Never performs I/O.</summary>
public sealed class SitebotFactStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, SitebotReleaseFacts> _facts = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public SitebotFactStore() : this(() => DateTime.UtcNow, 4096, TimeSpan.FromHours(6)) { }
    internal SitebotFactStore(Func<DateTime> utcNow, int capacity, TimeSpan ttl)
    {
        _utcNow = utcNow; _capacity = Math.Max(1, capacity); _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromHours(6);
    }

    public bool Observe(SitebotEvent observation)
    {
        var now = _utcNow();
        if (string.IsNullOrWhiteSpace(observation.ServerId) || string.IsNullOrWhiteSpace(observation.Release)
            || observation.ServerId.Length > 128 || observation.Release.Length > 1024
            || !Fresh(observation, now) || observation.ObservedAtUtc > now.AddMinutes(1)) return false;
        lock (_sync)
        {
            Prune(now);
            observation = observation with
            {
                Genres = Array.AsReadOnly(observation.Genres.ToArray()),
                Nukees = Array.AsReadOnly(observation.Nukees.ToArray())
            };
            var key = Key(observation.ServerId, observation.Release);
            _facts.TryGetValue(key, out var previous);
            previous ??= new() { ServerId = observation.ServerId, Release = observation.Release };
            if (previous.Latest is { } latest && latest.ObservedAtUtc > observation.ObservedAtUtc) return false;
            var next = previous with { Latest = observation };
            if (observation.Kind == SitebotEventKind.Metadata) next = next with { Metadata = observation };
            if (observation.Kind == SitebotEventKind.Complete)
            {
                var priorComplete = previous.Complete is { } prior && Fresh(prior, now) ? prior : null;
                var conflict = priorComplete != null && previous.ConflictingWinner || priorComplete?.Winner is { Length: > 0 } oldWinner &&
                    observation.Winner is { Length: > 0 } newWinner && !string.Equals(oldWinner, newWinner, StringComparison.OrdinalIgnoreCase);
                next = next with
                {
                    Complete = observation with { Bytes = observation.Bytes ?? priorComplete?.Bytes, Files = observation.Files ?? priorComplete?.Files },
                    ConflictingWinner = conflict
                };
            }
            else if (observation.Kind is SitebotEventKind.New or SitebotEventKind.Nuke or SitebotEventKind.Unnuke
                or SitebotEventKind.Progress or SitebotEventKind.Halfway or SitebotEventKind.Race)
                next = next with { Complete = null, ConflictingWinner = false };
            _facts[key] = next;
            while (_facts.Count > _capacity)
            {
                var oldest = _facts.MinBy(pair => pair.Value.Latest!.ObservedAtUtc).Key;
                _facts.Remove(oldest);
            }
            return true;
        }
    }

    public SitebotReleaseFacts? TryGet(string serverId, string release)
    {
        lock (_sync)
        {
            var now = _utcNow(); Prune(now);
            return _facts.TryGetValue(Key(serverId, release), out var facts) ? Current(facts, now) : null;
        }
    }

    public IReadOnlyList<SitebotReleaseFacts> Snapshot()
    {
        lock (_sync)
        {
            var now = _utcNow(); Prune(now);
            return _facts.Values.Select(f => Current(f, now)).ToArray();
        }
    }

    private SitebotReleaseFacts Current(SitebotReleaseFacts facts, DateTime now) => facts with
    {
        Complete = facts.Complete is { } complete && Fresh(complete, now) ? complete : null,
        Metadata = facts.Metadata is { } metadata && Fresh(metadata, now) ? metadata : null
    };
    private void Prune(DateTime now)
    {
        foreach (var key in _facts.Where(p => p.Value.Latest is not { } latest || !Fresh(latest, now)).Select(p => p.Key).ToArray())
            _facts.Remove(key);
    }
    private bool Fresh(SitebotEvent observation, DateTime now) => observation.ObservedAtUtc >= now - _ttl;
    private static string Key(string server, string release) => server + "\0" + release.ToUpperInvariant();
}
