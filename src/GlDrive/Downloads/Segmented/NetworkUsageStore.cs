using System.IO;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Per-network download byte counts, persisted across restarts so a data allowance survives a
/// reboot. Bytes are kept in local-time daily buckets (last <see cref="KeepDays"/> days) and the
/// daily/weekly/monthly totals are sums over them. Weeks start Monday 00:00, months on the 1st.
/// </summary>
public sealed class NetworkUsageStore
{
    public static string DefaultPath => Path.Combine(ConfigManager.AppDataPath, "network-usage.json");

    private const int KeepDays = 40;
    private const long SaveIntervalMs = 5000;
    private const string DayFormat = "yyyy-MM-dd";

    private readonly string _filePath;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly Dictionary<string, Dictionary<DateOnly, long>> _days = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSaveTick = Environment.TickCount64;
    private bool _dirty;

    public NetworkUsageStore() : this(DefaultPath) { }

    public NetworkUsageStore(string filePath, Func<DateTime>? nowLocal = null)
    {
        _filePath = filePath;
        _now = nowLocal ?? (() => DateTime.Now);
        Load();
    }

    public void Add(string networkId, long bytes)
    {
        if (bytes <= 0) return;
        bool due;
        lock (_lock)
        {
            if (!_days.TryGetValue(networkId, out var days))
                _days[networkId] = days = new Dictionary<DateOnly, long>();
            var today = DateOnly.FromDateTime(_now());
            days[today] = days.GetValueOrDefault(today) + bytes;
            _dirty = true;
            due = Environment.TickCount64 - _lastSaveTick >= SaveIntervalMs;
            if (due) _lastSaveTick = Environment.TickCount64;
        }
        if (due) Save();
    }

    public long UsedThisPeriod(string networkId, AllowancePeriod period)
    {
        if (period == AllowancePeriod.None) return 0;
        lock (_lock)
        {
            if (!_days.TryGetValue(networkId, out var days)) return 0;
            var start = PeriodStart(DateOnly.FromDateTime(_now()), period);
            return days.Where(kv => kv.Key >= start).Sum(kv => kv.Value);
        }
    }

    /// <summary>True when the network has used its whole allowance for the current period.</summary>
    public bool IsOverAllowance(NetworkPath path)
    {
        var s = path.Settings;
        if (s == null || s.AllowanceGb <= 0 || s.Period == AllowancePeriod.None) return false;
        var allowanceBytes = (long)(s.AllowanceGb * 1024 * 1024 * 1024);
        return UsedThisPeriod(path.Id, s.Period) >= allowanceBytes;
    }

    public void Reset(string networkId)
    {
        lock (_lock)
        {
            if (!_days.Remove(networkId)) return;
            _dirty = true;
        }
        Save();
    }

    public void Flush() => Save();

    internal static DateOnly PeriodStart(DateOnly today, AllowancePeriod period) => period switch
    {
        AllowancePeriod.Weekly => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
        AllowancePeriod.Monthly => new DateOnly(today.Year, today.Month, 1),
        _ => today,
    };

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(_filePath));
            if (raw == null) return;
            foreach (var (id, days) in raw)
            {
                var parsed = new Dictionary<DateOnly, long>();
                foreach (var (day, bytes) in days)
                    if (DateOnly.TryParseExact(day, DayFormat, out var d)) parsed[d] = bytes;
                _days[id] = parsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NetworkUsageStore: could not read {Path} — starting with empty usage", _filePath);
            _days.Clear();
        }
    }

    private void Save()
    {
        // Snapshot and write under one lock so an older snapshot never lands after a newer one.
        lock (_saveLock)
        {
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                var cutoff = DateOnly.FromDateTime(_now()).AddDays(-KeepDays);
                var snapshot = new Dictionary<string, Dictionary<string, long>>();
                foreach (var (id, days) in _days)
                {
                    foreach (var old in days.Keys.Where(d => d < cutoff).ToList()) days.Remove(old);
                    snapshot[id] = days.ToDictionary(kv => kv.Key.ToString(DayFormat), kv => kv.Value);
                }
                json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                _dirty = false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
                GlDrive.Util.SecureFile.WriteAllTextRestricted(_filePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "NetworkUsageStore: could not save {Path}", _filePath);
                lock (_lock) _dirty = true;
            }
        }
    }
}
