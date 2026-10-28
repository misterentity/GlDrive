using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Services.Alerts;

public enum AlertSeverity { Information, Warning, Error }
public enum AlertKind { General, SiteOutage, SiteRecovered, AnnounceSilent, AnnounceRecovered, OwnNuke, RaceComplete, DownloadComplete, DownloadFailed, Digest, Heartbeat, Hook, OwnedReleaseNuke }

public sealed record AlertEvent(AlertKind Kind, AlertSeverity Severity, string Title, string Message,
    string ServerId = "", string Release = "", string Incident = "", string HookEvent = "", string HookStatus = "");

public sealed record AlertEntry(string Id, AlertEvent Alert, DateTimeOffset FirstUtc, DateTimeOffset LastUtc,
    int Count = 1, bool Acknowledged = false, DateTimeOffset? SnoozedUntilUtc = null);

/// <summary>Bookkeeping only. Disk failures never reject an alert or affect engine admission.</summary>
public sealed class AlertStore
{
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _path;
    private readonly Func<int> _capacity;
    private readonly Action<string, string> _write;
    private List<AlertEntry> _entries = [];
    private bool _warned;
    private long _revision;
    private long _savedRevision;
    public AlertStore(string path, Func<int> capacity, Action<string, string>? write = null)
    {
        _path = path;
        _capacity = capacity;
        _write = write ?? SecureFile.WriteAllTextRestricted;
        try
        {
            if (File.Exists(path))
                _entries = JsonSerializer.Deserialize<List<AlertEntry>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException();
            if (_entries.Any(e => e == null || e.Alert == null || e.Count < 1)) throw new InvalidDataException();
            Trim();
        }
        catch
        {
            try { File.Copy(path, path + ".corrupt", true); } catch { }
            _entries = [];
            Warn();
        }
    }

    public AlertEntry[] Snapshot() { lock (_gate) return _entries.ToArray(); }

    internal bool IsSnoozed(string id, DateTimeOffset now)
    {
        lock (_gate) return _entries.Any(entry => entry.Id == id && entry.SnoozedUntilUtc > now);
    }

    internal (AlertEntry Entry, bool Repeated) Add(AlertEvent alert, DateTimeOffset now, AlertConfig config)
    {
        lock (_gate)
        {
            var index = config.Coalesce ? _entries.FindLastIndex(e => e.Alert.Kind == alert.Kind &&
                e.Alert.ServerId == alert.ServerId && e.Alert.Incident == alert.Incident &&
                e.Alert.Release == alert.Release && e.Alert.Title == alert.Title && e.Alert.Message == alert.Message &&
                now - e.LastUtc <= TimeSpan.FromMinutes(config.CoalesceMinutes)) : -1;
            AlertEntry entry;
            if (index >= 0)
            {
                entry = _entries[index] with { LastUtc = now, Count = _entries[index].Count + 1 };
                _entries[index] = entry;
            }
            else
            {
                entry = new(Guid.NewGuid().ToString("N"), alert, now, now);
                _entries.Add(entry);
            }
            Trim();
            _revision++;
            return (entry, index >= 0);
        }
    }

    public bool Update(string id, bool? acknowledged = null, DateTimeOffset? snoozedUntil = null, bool updateSnooze = false)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return false;
            _entries[index] = _entries[index] with
            {
                Acknowledged = acknowledged ?? _entries[index].Acknowledged,
                SnoozedUntilUtc = updateSnooze || snoozedUntil.HasValue ? snoozedUntil : _entries[index].SnoozedUntilUtc
            };
            _revision++;
            return true;
        }
    }

    public void Save()
    {
        // Do not hold the inbox lock during serialization or a durable disk write.
        lock (_saveGate)
        {
            AlertEntry[] entries;
            long revision;
            lock (_gate)
            {
                revision = _revision;
                if (revision == _savedRevision) return;
                entries = _entries.ToArray();
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                _write(_path, JsonSerializer.Serialize(entries));
                lock (_gate) _savedRevision = revision;
            }
            catch { Warn(); }
        }
    }

    private void Trim()
    {
        var remove = _entries.Count - Math.Max(1, _capacity());
        if (remove > 0) _entries = _entries.OrderBy(e => e.LastUtc).Skip(remove).ToList();
    }
    private void Warn()
    {
        if (_warned) return;
        _warned = true;
        Log.Warning("Alert history unavailable on disk; continuing with in-memory history");
    }
}
