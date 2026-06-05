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

    /// <summary>
    /// How long a blacklist entry stays in effect since its last failure.
    /// Permissions on glftpd sites change (group reassignment, path-filter
    /// edits, the user fixing the section path) and a permanent blacklist
    /// silently freezes those sites out of every future race. After this
    /// window IsBlacklisted returns false so the next race retries; if MKD
    /// still 550s, the entry gets refreshed by RecordPermanentFailure and
    /// the clock resets.
    /// </summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromDays(14);

    // PRD v3.5.2 — entries whose reason is *transient* (disk-full, etc.) get a
    // much shorter TTL than perms/path-filter denials. Siteops free disk in
    // hours, not days, and a 14-day freeze on tv-hd because the dest was full
    // for one afternoon is wrong. Also excluded from DistinctActiveSectionCount
    // so they don't trip the >=3-section auto-download-only blanket.
    public static readonly TimeSpan TransientEntryTtl = TimeSpan.FromHours(2);

    /// <summary>True if the recorded reason indicates a transient site condition that
    /// will clear without a config change — disk-full and friends.</summary>
    public static bool IsTransientReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        var r = reason;
        return r.Contains("out of disk space", StringComparison.OrdinalIgnoreCase)
            || r.Contains("disk full", StringComparison.OrdinalIgnoreCase)
            || r.Contains("no space", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan TtlFor(Entry e) =>
        IsTransientReason(e.Reason) ? TransientEntryTtl : EntryTtl;

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
                var now = DateTime.UtcNow;
                var expired = _entries.Values.Where(e => now - e.LastFailedAt > EntryTtl).ToList();
                if (expired.Count > 0)
                {
                    Log.Information("Section blacklist loaded: {Count} entries ({Expired} aged past {Ttl}-day TTL, will be retried next race)",
                        _entries.Count, expired.Count, (int)EntryTtl.TotalDays);
                    foreach (var e in expired)
                        Log.Information("Section blacklist: {Server}/[{Section}] entry from {When:u} eligible for retry",
                            e.ServerName, e.Section, e.LastFailedAt);
                }
                else
                {
                    Log.Information("Section blacklist loaded: {Count} entries", _entries.Count);
                }
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
        lock (_lock)
        {
            if (!_entries.TryGetValue((serverId, Normalize(section)), out var entry))
                return false;
            // Auto-expire: if the last failure was longer ago than the TTL,
            // we let the race retry. Transient reasons (disk-full) use a much
            // shorter TTL than permission denials.
            if (DateTime.UtcNow - entry.LastFailedAt > TtlFor(entry))
                return false;
            return true;
        }
    }

    public Entry? Get(string serverId, string section)
    {
        lock (_lock) return _entries.TryGetValue((serverId, Normalize(section)), out var e) ? e : null;
    }

    /// <summary>
    /// Count of distinct still-active (non-expired) blacklisted sections for a
    /// server. SpreadManager uses this for self-healing auto-download-only: a
    /// server that has permanently failed uploads across several distinct
    /// sections almost certainly can't receive uploads at all (leech-only BNC),
    /// so it should be excluded as a destination entirely rather than discovered
    /// section-by-section. (PRD R2.)
    /// </summary>
    public int DistinctActiveSectionCount(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return 0;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // Only PERSISTENT-reason entries count toward auto-download-only.
            // Transient (disk-full) entries make the engine skip THAT section
            // while the condition persists, but a few transient strikes should
            // never blanket-flag the whole server as leech-only — observed
            // 2026-05-27: disk-full failures on May 25 blacklisted zephyr for
            // 3 sections, triggering auto-DL-only, so no race reached zephyr
            // for two days even though the disk had long since been cleared.
            return _entries.Count(kv =>
                kv.Key.serverId == serverId &&
                now - kv.Value.LastFailedAt <= TtlFor(kv.Value) &&
                !IsTransientReason(kv.Value.Reason));
        }
    }

    /// <summary>
    /// True when an entry exists but has aged past its TTL — useful for
    /// UI / error messages to distinguish "still blocked" from "retrying".
    /// </summary>
    public bool IsExpired(string serverId, string section)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue((serverId, Normalize(section)), out var entry)) return false;
            return DateTime.UtcNow - entry.LastFailedAt > TtlFor(entry);
        }
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
        // Most permanent denials are 550 (path/permission). But disk-full and a
        // few other deterministic failures come back as 553 (file action not
        // taken). Accept either code; the message text gates the verdict.
        if (code != "550" && code != "553") return false;
        var m = message;
        // glftpd 553 — site has no free space. Will not change until the siteop
        // frees space; retrying is purely wasted I/O. Observed 2026-05-25:
        // 24x MKD + 62x STOR on the same release for the same reason.
        if (m.Contains("out of disk space", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("disk full", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("no space", StringComparison.OrdinalIgnoreCase)) return true;
        // glftpd path-filter, user has no write access in this tree
        if (m.Contains("Not allowed to make directories", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path-filter", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path filter", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("not a member", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("You cannot create", StringComparison.OrdinalIgnoreCase)) return true;
        // dirscript denial — also deterministic; the script will reject the same
        // path every time. Previously only the in-race guard caught this, so it
        // never made it to the persistent blacklist.
        if (m.Contains("Denied by dirscript", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Permanent UPLOAD denials seen at STOR time (not MKD). These mean the
    /// account simply can't write to this destination tree — retrying never
    /// helps. Matched on the IOException message text (which embeds the FTP
    /// reply), not a separate code, because the transfer layer surfaces it as
    /// "STOR failed: 553 ...". Examples:
    ///   553 Error: you have no upload rights for this directory!
    ///   553 file: path-filter denied permission. (Filename deny)
    ///   553 Permission denied
    /// </summary>
    public static bool IsPermanentUploadDenial(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var m = errorMessage;
        // Must be a STOR/upload rejection, not a transient transport error.
        if (!m.Contains("STOR failed", StringComparison.OrdinalIgnoreCase)
            && !m.Contains("upload rights", StringComparison.OrdinalIgnoreCase)
            && !m.Contains("path-filter", StringComparison.OrdinalIgnoreCase)
            && !m.Contains("path filter", StringComparison.OrdinalIgnoreCase))
            return false;
        if (m.Contains("no upload rights", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path-filter denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("path filter denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("Not allowed", StringComparison.OrdinalIgnoreCase)) return true;
        // Disk-full also surfaces as STOR 553 on the way in.
        if (m.Contains("out of disk space", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("disk full", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("no space", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True when a transfer failed because the account is out of credits on the
    /// site (glftpd "550 Insufficient credits" / ratio enforcement). This is a
    /// SOURCE-side condition seen at RETR time — the account can't download
    /// because its credit balance is empty. Credits only replenish by uploading
    /// elsewhere, which won't happen mid-race, so the condition is effectively
    /// permanent for the race duration. Unlike a permission/path denial it is
    /// NOT persisted (credits come back across days), only parked per-race.
    /// Observed 2026-05-29: 65 SYN-&gt;zephyr RETR retries, all 550 Insufficient
    /// credits, each riding the per-dest backoff ladder to a 5-failure drop.
    /// </summary>
    public static bool IsCreditExhaustion(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var m = errorMessage;
        return m.Contains("insufficient credits", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not enough credits", StringComparison.OrdinalIgnoreCase)
            || m.Contains("out of credits", StringComparison.OrdinalIgnoreCase)
            || m.Contains("no credits", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a transfer failed because the SOURCE no longer has the file —
    /// glftpd "RETR ... 550 No such file" after the release was moved/deleted/
    /// archived off the source mid-race. Distinct from IsCreditExhaustion (also a
    /// RETR 550, but a credit balance issue) and from MKD missing-parent (a DEST
    /// 550 surfaced as "MKD failed"). Drives the alternate-source failover.
    /// </summary>
    public static bool IsSourceFileMissing(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var m = errorMessage;
        // Must be a download/RETR rejection, not an MKD or STOR failure.
        if (m.Contains("MKD failed", StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Contains("STOR failed", StringComparison.OrdinalIgnoreCase)) return false;
        // Credit exhaustion is its own (already-handled) source condition.
        if (IsCreditExhaustion(m)) return false;
        var notFound =
            m.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("File not found", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Cannot find the file", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        if (!notFound) return false;
        // Bias toward RETR/download context to avoid matching unrelated 550s.
        return m.Contains("RETR", StringComparison.OrdinalIgnoreCase)
            || m.Contains("550", StringComparison.Ordinal);
    }
}
