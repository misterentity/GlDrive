using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Spread;

public class RaceHistoryItem
{
    public string Id { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string Section { get; set; } = "";
    public SpreadMode Mode { get; set; }
    public SpreadJobState Result { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public int SiteCount { get; set; }
    public int FilesTransferred { get; set; }
    public long BytesTransferred { get; set; }
    public string SiteNames { get; set; } = "";
    public string SkiplistResult { get; set; } = ""; // "Allowed" or "Denied by: *PATTERN*"
    public List<SkiplistTraceEntry>? SkiplistTrace { get; set; }

    // PRD R1 — race outcome metrics. FilesTotal is the release's file count;
    // FilesDelivered is how many reached at least one viable destination;
    // CleanComplete is true when every non-source/non-download-only dest got
    // the full set (0 undelivered).
    public int FilesTotal { get; set; }
    public int FilesDelivered { get; set; }
    public bool CleanComplete { get; set; }

    // Final 0-65535 race score (points) captured at completion — same value shown on the
    // retained race card. Persisted so a finished race's points survive a restart.
    public int Score { get; set; }

    // PRD O3 — failure taxonomy. One of: "", "config", "upload-denied",
    // "bnc-pressure", "transport", "not-found", "nuked", "no-activity".
    public string FailureCategory { get; set; } = "";

    // Per-destination completion summary captured at race end, e.g. "2 complete · 1 timeout".
    // Empty for legacy/file-count completion or when no dest state was tracked.
    public string DestinationState { get; set; } = "";
}

/// <summary>PRD R1/O3 aggregate. CleanRate = Clean / Finished (0 when none).</summary>
public readonly record struct RaceSummary(
    int Finished, int Clean, int Failed, IReadOnlyDictionary<string, int> FailureCounts)
{
    public double CleanRate => Finished > 0 ? (double)Clean / Finished : 0.0;
}

/// <summary>PRD O2 — per-server spread-pool health for the diagnostics readout.</summary>
public readonly record struct PoolHealthSnapshot(
    string ServerId,
    string ServerName,
    int MaxSize,
    int Active,
    int Created,
    int Quarantined,
    int? ObservedBncCap,
    bool InCooldown);

public class RaceHistoryStore
{
    private static readonly string FilePath = Path.Combine(ConfigManager.AppDataPath, "race-history.json");
    private readonly List<RaceHistoryItem> _items = new();
    private readonly Lock _lock = new();
    private const int MaxItems = 500;

    // Set true when Load() threw (corrupt/locked file). While true, Save() refuses
    // to overwrite the on-disk file so a single failed load can't silently truncate
    // hundreds of persisted races to whatever accumulated this session. Reset to
    // false on the next successful Load().
    private bool _loadFailed;

    public IReadOnlyList<RaceHistoryItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    /// <summary>
    /// PRD R1/O3 — aggregate outcome summary across recorded races. CleanRate is
    /// clean completions as a fraction of finished (non-running) races.
    /// FailureCounts groups failed races by category for the taxonomy view.
    /// </summary>
    public RaceSummary Summarize()
    {
        lock (_lock)
        {
            var finished = _items.Where(i => i.Result != SpreadJobState.Running).ToList();
            var clean = finished.Count(i => i.CleanComplete);
            var failed = finished.Count(i => i.Result == SpreadJobState.Failed);
            var failureCounts = finished
                .Where(i => i.Result == SpreadJobState.Failed && !string.IsNullOrEmpty(i.FailureCategory))
                .GroupBy(i => i.FailureCategory)
                .ToDictionary(g => g.Key, g => g.Count());
            return new RaceSummary(finished.Count, clean, failed, failureCounts);
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { _loadFailed = false; return; }
            var json = File.ReadAllText(FilePath);
            var items = JsonSerializer.Deserialize<List<RaceHistoryItem>>(json);
            if (items != null)
            {
                // Reclaim legacy full-trace bloat immediately: pre-slim entries stored
                // ~19KB of per-rule skiplist trace each (98% of a 9.9MB file). Keep only
                // the matched (denying) rule; the next Save() persists the slimmed shape.
                foreach (var it in items)
                {
                    if (it.SkiplistTrace is { Count: > 1 } tr)
                    {
                        var m = tr.Where(t => t.IsMatch).Take(1).ToList();
                        it.SkiplistTrace = m.Count > 0 ? m : null;
                    }
                }
                lock (_lock) _items.AddRange(items);
            }
            _loadFailed = false;
        }
        catch (Exception ex)
        {
            // The file exists but couldn't be read/parsed (corrupt, mid-write, locked
            // by OneDrive/AV). Set the guard so the next Add()->Save() does NOT blow
            // away the on-disk data, and snapshot the bad file for post-mortem.
            _loadFailed = true;
            Log.Warning(ex, "Failed to load race history — Save() suppressed to avoid truncating persisted races");
            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Read persisted history straight from disk without touching the in-memory store.
    /// Used by the dashboard as a fallback when the SpreadManager isn't ready yet at
    /// open time, so the History tab is never blank while races sit on disk.
    /// Returns an empty list on any failure (never throws).
    /// </summary>
    public static List<RaceHistoryItem> ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<RaceHistoryItem>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void Add(RaceHistoryItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
            while (_items.Count > MaxItems)
                _items.RemoveAt(_items.Count - 1);
        }
        Save();
    }

    private void Save()
    {
        // If the last Load() failed, the on-disk file holds races we never read into
        // _items. Overwriting it now would discard them. Skip persistence until a
        // future successful Load() clears the guard (or the process restarts and
        // re-loads cleanly).
        if (_loadFailed)
        {
            Log.Warning("Race history Save() skipped — prior load failed; not overwriting on-disk history");
            return;
        }
        try
        {
            List<RaceHistoryItem> snapshot;
            lock (_lock) snapshot = _items.ToList();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            SecureFile.WriteAllTextRestricted(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save race history");
        }
    }
}
