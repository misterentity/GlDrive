using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>
/// Durable memory of watch-folder archives that <see cref="ExtractFailureClassifier"/>
/// ruled <see cref="ExtractFailureKind.Permanent"/>.
///
/// Root cause this exists for (observed 2026-08-12): the classifier was right — an
/// incomplete volume set / CRC-failed archive can never extract — but the give-up set
/// was an in-memory ExtractorWindow field. GlDrive restarts often (auto-update,
/// watchdog, sleep/resume), and each restart replayed the whole extraction against the
/// same hopeless input: 21 abandon events across just 3 distinct archives in three
/// days, each re-reading GBs and emitting WRN + a full stack trace.
///
/// The verdict is a property of the VOLUME SET, not of the run, so entries are keyed to
/// a fingerprint of that set — volume count plus total bytes. When the missing volume
/// finally downloads (or a truncated part is replaced) the fingerprint changes and the
/// path revives on its own. That keeps this from becoming the kind of never-expiring
/// latch that has bitten this codebase before (see v3.10.41/.47).
///
/// Stored at %AppData%\GlDrive\extract-abandoned.json.
/// </summary>
public sealed class ExtractAbandonStore
{
    public sealed class Entry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("volumeCount")] public int VolumeCount { get; set; }
        [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
        [JsonPropertyName("abandonedAt")] public DateTime AbandonedAt { get; set; }
    }

    /// <summary>
    /// Backstop expiry. The fingerprint check is the real revival mechanism; this only
    /// covers the case where a set changes in a way the fingerprint cannot see, so that
    /// no path can be frozen out permanently by a stale verdict.
    /// </summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromDays(30);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public ExtractAbandonStore()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = System.IO.Path.Combine(dir, "GlDrive", "extract-abandoned.json");
    }

    /// <summary>Path-override ctor for tests, so they never touch the live store.</summary>
    public ExtractAbandonStore(string path) => _path = path;

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<Entry>>(json);
            if (list == null) return;

            lock (_lock)
            {
                _entries.Clear();
                foreach (var e in list)
                {
                    if (string.IsNullOrWhiteSpace(e.Path)) continue;
                    if (DateTime.UtcNow - e.AbandonedAt > EntryTtl) continue;
                    _entries[e.Path] = e;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ExtractAbandonStore: failed to load {Path}", _path);
        }
    }

    /// <summary>
    /// True if this exact volume set was already ruled unextractable. A changed
    /// fingerprint or an expired entry drops the record and returns false, so the
    /// caller retries exactly once per genuine change.
    /// </summary>
    public bool ShouldSkip(string path, int volumeCount, long totalBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var e)) return false;

            if (DateTime.UtcNow - e.AbandonedAt > EntryTtl ||
                e.VolumeCount != volumeCount || e.TotalBytes != totalBytes)
            {
                _entries.Remove(path);
                SaveLocked();
                return false;
            }

            return true;
        }
    }

    /// <summary>Record a permanent verdict. Returns true the first time for this set.</summary>
    public bool Record(string path, int volumeCount, long totalBytes, string reason)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (_lock)
        {
            var isNew = !_entries.TryGetValue(path, out var existing)
                        || existing.VolumeCount != volumeCount
                        || existing.TotalBytes != totalBytes;

            _entries[path] = new Entry
            {
                Path = path,
                Reason = reason ?? "",
                VolumeCount = volumeCount,
                TotalBytes = totalBytes,
                AbandonedAt = isNew ? DateTime.UtcNow : existing!.AbandonedAt,
            };

            SaveLocked();
            return isNew;
        }
    }

    /// <summary>Drop a path — used when it extracts successfully or is removed.</summary>
    public void Forget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_lock)
        {
            if (_entries.Remove(path)) SaveLocked();
        }
    }

    /// <summary>Test seam for TTL expiry without sleeping.</summary>
    internal void AgeEntryForTest(string path, TimeSpan by)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var e))
                e.AbandonedAt -= by;
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ExtractAbandonStore: failed to save {Path}", _path);
        }
    }
}
