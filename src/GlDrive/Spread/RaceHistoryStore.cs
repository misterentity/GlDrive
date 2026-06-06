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
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var items = JsonSerializer.Deserialize<List<RaceHistoryItem>>(json);
            if (items != null)
            {
                lock (_lock) _items.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load race history");
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
