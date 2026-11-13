namespace GlDrive.Spread;

public enum RequestState { Open, Filling, Filled, Closed }

public sealed record TrackedRequest(
    string Release, int? Number, string? Channel, DateTime FirstSeenUtc,
    RequestState State, int SearchAttempts, int FillAttempts, DateTime NextAttemptUtc);

public sealed record RequestTrackerOptions
{
    public int MaxPerHour { get; init; } = 10;
    public int CooldownSeconds { get; init; } = 60;
    public int MaxFillAttempts { get; init; } = 3;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(48);
    public TimeSpan ReconcileGrace { get; init; } = TimeSpan.FromMinutes(2);
    public int Capacity { get; init; } = 200;
}

public sealed class RequestTracker
{
    private readonly object _lock = new();
    private readonly RequestTrackerOptions _options;
    private readonly Dictionary<string, TrackedRequest> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTime> _fillStarts = new();
    private DateTime? _lastFillStartUtc;

    public RequestTracker(RequestTrackerOptions? options = null)
    {
        _options = options ?? new RequestTrackerOptions();
    }

    public bool Observe(string release, int? number, string? channel, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(release, out var request))
            {
                if (request.State is RequestState.Open or RequestState.Filling)
                {
                    _requests[release] = request with
                    {
                        Number = number ?? request.Number,
                        Channel = channel ?? request.Channel
                    };
                    return false;
                }

                _requests[release] = new TrackedRequest(request.Release, number, channel, nowUtc,
                    RequestState.Open, 0, 0, nowUtc);
                return true;
            }

            Add(release, number, channel, nowUtc);
            return true;
        }
    }

    public void MarkFilledElsewhere(string release)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(release, out var request)
                && request.State is RequestState.Open or RequestState.Filling)
                _requests[release] = request with { State = RequestState.Closed };
        }
    }

    public void Reconcile(IReadOnlyCollection<string> openReleases, DateTime nowUtc)
    {
        lock (_lock)
        {
            var open = new HashSet<string>(openReleases, StringComparer.OrdinalIgnoreCase);
            foreach (var request in _requests.Values.ToArray())
            {
                if (request.State == RequestState.Open && !open.Contains(request.Release)
                    && nowUtc - request.FirstSeenUtc > _options.ReconcileGrace)
                    _requests[request.Release] = request with { State = RequestState.Closed };
            }

            var newReleases = open.Where(release => !_requests.ContainsKey(release)).ToArray();
            foreach (var release in newReleases)
                Add(release, null, null, nowUtc);
        }
    }

    public IReadOnlyList<TrackedRequest> DueForSearch(DateTime nowUtc)
    {
        lock (_lock)
        {
            foreach (var request in _requests.Values.ToArray())
            {
                if (request.State == RequestState.Open && nowUtc - request.FirstSeenUtc > _options.MaxAge)
                    _requests[request.Release] = request with { State = RequestState.Closed };
            }

            return _requests.Values
                .Where(request => request.State == RequestState.Open && request.NextAttemptUtc <= nowUtc)
                .OrderBy(request => request.FirstSeenUtc)
                .ToArray();
        }
    }

    public void SearchMissed(string release, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_requests.TryGetValue(release, out var request) || request.State != RequestState.Open)
                return;

            var attempts = request.SearchAttempts + 1;
            var minutes = attempts switch
            {
                1 => 1,
                2 => 2,
                3 => 5,
                4 => 10,
                5 => 20,
                _ => 30
            };
            _requests[release] = request with
            {
                SearchAttempts = attempts,
                NextAttemptUtc = nowUtc.AddMinutes(minutes)
            };
        }
    }

    public bool TryBeginFill(string release, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_requests.TryGetValue(release, out var request) || request.State != RequestState.Open)
                return false;

            var nextAttemptUtc = nowUtc;
            if (_options.CooldownSeconds > 0 && _lastFillStartUtc is { } lastFillStartUtc)
            {
                var cooldownEndUtc = lastFillStartUtc.AddSeconds(_options.CooldownSeconds);
                if (cooldownEndUtc > nextAttemptUtc)
                    nextAttemptUtc = cooldownEndUtc;
            }

            if (_options.MaxPerHour > 0)
            {
                _fillStarts.RemoveAll(start => nowUtc - start >= TimeSpan.FromHours(1));
                if (_fillStarts.Count >= _options.MaxPerHour)
                {
                    var hourlyEndUtc = _fillStarts.Min().AddHours(1);
                    if (hourlyEndUtc > nextAttemptUtc)
                        nextAttemptUtc = hourlyEndUtc;
                }
            }

            if (nextAttemptUtc > nowUtc)
            {
                _requests[release] = request with { NextAttemptUtc = nextAttemptUtc };
                return false;
            }

            _lastFillStartUtc = nowUtc;
            if (_options.MaxPerHour > 0)
                _fillStarts.Add(nowUtc);
            _requests[release] = request with
            {
                State = RequestState.Filling,
                FillAttempts = request.FillAttempts + 1
            };
            return true;
        }
    }

    /// <summary>Admission did not start a fill. Retain the request without spending a transfer retry.</summary>
    public void FillDeferred(string release, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_requests.TryGetValue(release, out var request) || request.State != RequestState.Filling)
                return;
            _requests[release] = request with
            {
                State = RequestState.Open,
                FillAttempts = Math.Max(0, request.FillAttempts - 1),
                NextAttemptUtc = nowUtc.AddMinutes(2)
            };
            // Keep the admission-rate accounting: repeated deferrals must not
            // bypass the configured hourly limit or global cooldown.
        }
    }

    public void FillFailed(string release, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_requests.TryGetValue(release, out var request) || request.State != RequestState.Filling)
                return;

            if (request.FillAttempts >= _options.MaxFillAttempts)
            {
                _requests[release] = request with { State = RequestState.Closed };
                return;
            }

            var minutes = request.FillAttempts switch
            {
                1 => 2,
                2 => 5,
                _ => 15
            };
            _requests[release] = request with
            {
                State = RequestState.Open,
                NextAttemptUtc = nowUtc.AddMinutes(minutes)
            };
        }
    }

    public void FillCompleted(string release)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(release, out var request) && request.State == RequestState.Filling)
                _requests[release] = request with { State = RequestState.Filled };
        }
    }

    public void Nudge(string release, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(release, out var request) && request.State == RequestState.Open)
                _requests[release] = request with { NextAttemptUtc = nowUtc };
        }
    }

    public TrackedRequest? Get(string release)
    {
        lock (_lock)
        {
            return _requests.GetValueOrDefault(release);
        }
    }

    public IReadOnlyList<TrackedRequest> Snapshot()
    {
        lock (_lock)
        {
            return _requests.Values.OrderBy(request => request.FirstSeenUtc).ToArray();
        }
    }

    private void Add(string release, int? number, string? channel, DateTime nowUtc)
    {
        while (_requests.Count >= Math.Max(1, _options.Capacity))
        {
            var oldest = _requests.Values
                .Where(request => request.State != RequestState.Filling)
                .OrderBy(request => request.State is RequestState.Closed or RequestState.Filled ? 0 : 1)
                .ThenBy(request => request.FirstSeenUtc)
                .FirstOrDefault();
            if (oldest is null)
                break;

            _requests.Remove(oldest.Release);
        }

        _requests[release] = new TrackedRequest(release, number, channel, nowUtc,
            RequestState.Open, 0, 0, nowUtc);
    }
}
