using System.Diagnostics;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Token-bucket pacing for one total bucket plus one bucket per network. Limits are re-read on
/// every call, so toggling slow mode or editing a network cap takes effect on the next chunk.
/// Each bucket holds about one second of burst. Bytes are debited first (they have already been
/// received) and the caller then waits until every bucket it touched is back to non-negative.
/// </summary>
public sealed class TransferPacer : ITransferPacer
{
    private static readonly TimeSpan MaxSlice = TimeSpan.FromSeconds(1);

    private readonly Func<long> _totalBytesPerSecond;
    private readonly Func<string?, long> _networkBytesPerSecond;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private readonly Bucket _total = new();
    private readonly Dictionary<string, Bucket> _networks = new();

    public TransferPacer(Func<long> totalBytesPerSecond, Func<string?, long> networkBytesPerSecond)
        : this(totalBytesPerSecond, networkBytesPerSecond, null, null) { }

    internal TransferPacer(Func<long> totalBytesPerSecond, Func<string?, long> networkBytesPerSecond,
        Func<TimeSpan>? clock, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _totalBytesPerSecond = totalBytesPerSecond;
        _networkBytesPerSecond = networkBytesPerSecond;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed);
        _delay = delay ?? Task.Delay;
    }

    public async ValueTask WaitAsync(string? networkId, int bytes, CancellationToken ct)
    {
        var key = networkId ?? NetworkPath.DefaultId;
        var wait = Settle(key, bytes);
        while (wait > TimeSpan.Zero)
        {
            await _delay(wait < MaxSlice ? wait : MaxSlice, ct);
            ct.ThrowIfCancellationRequested();
            wait = Settle(key, 0);
        }
    }

    /// <summary>Refill, debit <paramref name="bytes"/>, and return how long until all buckets are non-negative.</summary>
    private TimeSpan Settle(string networkId, int bytes)
    {
        var now = _clock();
        var totalRate = _totalBytesPerSecond();
        var netRate = _networkBytesPerSecond(networkId);
        lock (_lock)
        {
            if (!_networks.TryGetValue(networkId, out var net))
                _networks[networkId] = net = new Bucket();
            var wait = _total.Settle(now, totalRate, bytes);
            var netWait = net.Settle(now, netRate, bytes);
            return netWait > wait ? netWait : wait;
        }
    }

    private sealed class Bucket
    {
        private double _tokens;
        private TimeSpan _last;
        private long _rate;

        public TimeSpan Settle(TimeSpan now, long rate, int bytes)
        {
            if (rate <= 0)
            {
                _rate = 0;
                return TimeSpan.Zero;
            }
            if (_rate <= 0) _tokens = rate; // newly limited: start with a full burst
            else _tokens = Math.Min(rate, _tokens + (now - _last).TotalSeconds * rate);
            _rate = rate;
            _last = now;
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }
    }
}

/// <summary>Applies several pacers in turn, e.g. the app-wide pacer and a per-server limit.</summary>
public sealed class CompositePacer(params ITransferPacer?[] pacers) : ITransferPacer
{
    public async ValueTask WaitAsync(string? networkId, int bytes, CancellationToken ct)
    {
        foreach (var p in pacers)
            if (p != null) await p.WaitAsync(networkId, bytes, ct);
    }
}
