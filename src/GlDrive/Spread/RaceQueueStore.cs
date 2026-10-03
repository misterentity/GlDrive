using System.IO;
using System.Text.Json;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// A race request that survives a restart: either waiting in the queue or in flight
/// (<see cref="WasActive"/>) when the snapshot was written.
/// </summary>
public sealed record PersistedRace(
    string Section,
    string ReleaseName,
    List<string> ServerIds,
    SpreadMode Mode,
    string? KnownSourceServerId,
    string? KnownSourcePath,
    DateTime QueuedAtUtc,
    int Resumes = 0,
    bool WasActive = false);

public readonly record struct RaceQueueRestore(
    List<PersistedRace> Races, int Resumed, int DroppedStale, int DroppedResumeCap);

/// <summary>
/// Persists the spread race queue plus in-flight races. The queue used to live only in
/// memory: at maxConcurrentRaces=1 it ran 6h+ deep (362 queued vs 191 started on
/// 2026-10-02), so every restart — deploy, auto-update, reboot — silently dropped
/// up to ~150 pending races.
/// </summary>
public sealed class RaceQueueStore
{
    /// <summary>A request older than this is no longer worth resuming.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// How many restarts an in-flight race may survive. Bounds a crash loop: a race that
    /// takes the process down would otherwise be resumed straight back into the crash.
    /// </summary>
    internal const int MaxResumes = 2;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _filePath;

    internal RaceQueueStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public List<PersistedRace> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<PersistedRace>>(json, Options) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for post-mortem; the next save replaces it with
            // live state, which is the right outcome for a queue (nothing else reads it).
            Log.Warning(ex, "Failed to load race queue — starting empty ({Path}.corrupt kept)", _filePath);
            try { File.Copy(_filePath, _filePath + ".corrupt", overwrite: true); }
            catch { /* best effort */ }
            return new();
        }
    }

    /// <summary>
    /// Writes the snapshot returned by <paramref name="capture"/>, taken under the
    /// per-path lock so concurrent saves land in order. A null snapshot skips the write.
    /// </summary>
    public void Save(Func<IReadOnlyList<PersistedRace>?> capture)
    {
        try
        {
            SecureFile.WithPathLock(_filePath, () =>
            {
                var races = capture();
                if (races == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                SecureFile.WriteAllTextRestricted(_filePath, JsonSerializer.Serialize(races, Options));
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save race queue");
        }
    }

    /// <summary>
    /// Decides what to resume after a restart. In-flight races come first (they had
    /// already won their turn) with their resume count bumped; stale requests and
    /// races that already survived <see cref="MaxResumes"/> restarts are dropped;
    /// duplicate releases keep their first occurrence.
    /// </summary>
    internal static RaceQueueRestore Restore(IEnumerable<PersistedRace> persisted, DateTime nowUtc)
    {
        var kept = new List<PersistedRace>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int resumed = 0, stale = 0, capped = 0;

        foreach (var race in persisted.Where(r => r.WasActive).Concat(persisted.Where(r => !r.WasActive)))
        {
            if (string.IsNullOrWhiteSpace(race.ReleaseName) || !seen.Add(race.ReleaseName)) continue;
            if (nowUtc - race.QueuedAtUtc > MaxAge) { stale++; continue; }

            var next = race with { WasActive = false };
            if (race.WasActive)
            {
                if (race.Resumes >= MaxResumes) { capped++; continue; }
                next = next with { Resumes = race.Resumes + 1 };
                resumed++;
            }
            kept.Add(next);
        }
        return new RaceQueueRestore(kept, resumed, stale, capped);
    }
}
