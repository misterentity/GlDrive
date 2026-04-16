using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Persistent (serverId, section) blacklist. A destination site that permanently
/// rejects MKD (550 path-filter, permission denied) gets added here so future
/// races skip it at the selection phase instead of wasting five retry cycles
/// per race discovering the same NO.
///
/// Stored at %AppData%\GlDrive\section-blacklist.json. Shared by SpreadManager
/// (filters in TryAutoRaceInternalAsync) and SpreadJob (filters in destination
/// selection + writes entries when MKD hits a permanent failure).
/// </summary>
public sealed class SectionBlacklistStore
{
    public sealed class Entry
    {
        [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
        [JsonPropertyName("serverName")] public string ServerName { get; set; } = "";
        [JsonPropertyName("section")] public string Section { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("firstFailedAt")] public DateTime FirstFailedAt { get; set; }
        [JsonPropertyName("lastFailedAt")] public DateTime LastFailedAt { get; set; }
        [JsonPropertyName("failureCount")] public int FailureCount { get; set; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<(string serverId, string section), Entry> _entries = new(KeyComparer.Instance);

    public SectionBlacklistStore()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = Path.Combine(dir, "GlDrive", "section-blacklist.json");
    }

    public void Load()
    {
        lock (_lock)
        {
            _entries.Clear();
            if (!File.Exists(_path)) return;
            try
            {
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<Entry>>(json) ?? new();
                foreach (var e in list)
                {
                    if (string.IsNullOrWhiteSpace(e.ServerId) || string.IsNullOrWhiteSpace(e.Section))
                        continue;
                    _entries[(e.ServerId, Normalize(e.Section))] = e;
                }
                Log.Information("Section blacklist loaded: {Count} entries", _entries.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load section blacklist from {Path}", _path);
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.ServerName).ThenBy(e => e.Section), opts);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save section blacklist to {Path}", _path);
        }
    }

    public bool IsBlacklisted(string serverId, string section)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(section)) return false;
        lock (_lock) return _entries.ContainsKey((serverId, Normalize(section)));
    }

    public Entry? Get(string serverId, string section)
    {
        lock (_lock) return _entries.TryGetValue((serverId, Normalize(section)), out var e) ? e : null;
    }

    /// <summary>
    /// Record a permanent MKD failure. Creates the blacklist entry if absent,
    /// bumps count + timestamp if present. Persists immediately so a crash
    /// doesn't lose the lesson.
    /// </summary>
    public void RecordPermanentFailure(string serverId, string serverName, string section,
        string path, string reason)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(section)) return;
        var key = (serverId, Normalize(section));
        bool newlyAdded;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.FailureCount++;
                existing.LastFailedAt = DateTime.UtcNow;
                existing.ServerName = serverName;
                existing.Reason = reason;
                existing.Path = path;
                newlyAdded = false;
            }
            else
            {
                _entries[key] = new Entry
                {
                    ServerId = serverId,
                    ServerName = serverName,
                    Section = section,
                    Path = path,
                    Reason = reason,
                    FirstFailedAt = DateTime.UtcNow,
                    LastFailedAt = DateTime.UtcNow,
                    FailureCount = 1
                };
                newlyAdded = true;
            }
            SaveLocked();
        }

        if (newlyAdded)
            Log.Warning("Section blacklist: {Server} permanently excluded from [{Section}] — {Reason} " +
                "(delete entry from section-blacklist.json to retry)",
                serverName, section, reason);
    }

    public IReadOnlyList<Entry> GetAll()
    {
        lock (_lock) return _entries.Values.ToList();
    }

    public bool Remove(string serverId, string section)
    {
        lock (_lock)
        {
            var removed = _entries.Remove((serverId, Normalize(section)));
            if (removed) SaveLocked();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    private static string Normalize(string section) => section.Trim().ToLowerInvariant();

    private sealed class KeyComparer : IEqualityComparer<(string serverId, string section)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((string serverId, string section) x, (string serverId, string section) y) =>
            string.Equals(x.serverId, y.serverId, StringComparison.Ordinal) &&
            string.Equals(x.section, y.section, StringComparison.Ordinal);
        public int GetHashCode((string serverId, string section) obj) =>
            HashCode.Combine(obj.serverId, obj.section);
    }
}

/// <summary>
/// Classifies an FTP 550 reply as transient (retry worth attempting) or permanent
/// (site will never allow MKD here — add to blacklist). Kept here so SpreadJob
/// and any future consumer share one definition.
/// </summary>
public static class MkdFailureClassifier
{
    public static bool IsPermanent(string code, string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (code != "550") return false;
        var m = message;
        // glftpd path-filter, user has no write access in this tree
        if (m.Contains("Not allowed to make directories", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path-filter", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path filter", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("not a member", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("You cannot create", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
