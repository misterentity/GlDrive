using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Records and surfaces moving-average transfer speed per (source, destination)
/// route. cbftp builds its scoreboard around route-speed history (engine.cpp's
/// setSpeedScale) but loses it on restart; the research called that out as a
/// gap. We persist a compact JSON file under %AppData% so the very first race
/// of a session against a known pair already has a sensible score.
///
/// Format: { "srcId\tdstId": [bps, bps, ...], ... }   one entry per pair,
/// last <see cref="MaxSamples"/> measurements per pair. Persistence is
/// best-effort and never throws.
/// </summary>
public class SpeedTracker
{
    private readonly Dictionary<(string src, string dst), Queue<double>> _speeds = new();
    private readonly Lock _lock = new();
    private const int MaxSamples = 10;

    private string? _persistPath;
    private int _changesSinceSave;
    private const int SaveEveryN = 10;   // save after every Nth recorded transfer
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private static readonly TimeSpan MinSaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enable disk persistence — pass an absolute path. The file is loaded
    /// synchronously if it exists; subsequent <see cref="RecordTransfer"/>
    /// calls auto-save after every <c>SaveEveryN</c> samples (debounced by
    /// <c>MinSaveInterval</c>). Caller must still call <see cref="Save"/> on
    /// shutdown to flush any in-flight changes.
    /// </summary>
    public void EnablePersistence(string path)
    {
        _persistPath = path;
        Load();
    }

    public void RecordTransfer(string srcId, string dstId, long bytes, TimeSpan duration)
    {
        if (duration.TotalSeconds < 0.1 || bytes <= 0) return;

        var key = (srcId, dstId);
        var speed = bytes / duration.TotalSeconds;

        bool shouldSave;
        lock (_lock)
        {
            if (!_speeds.TryGetValue(key, out var queue))
            {
                queue = new Queue<double>();
                _speeds[key] = queue;
            }
            if (queue.Count >= MaxSamples)
                queue.Dequeue();
            queue.Enqueue(speed);

            _changesSinceSave++;
            shouldSave = _persistPath != null
                && _changesSinceSave >= SaveEveryN
                && (DateTime.UtcNow - _lastSaveUtc) >= MinSaveInterval;
            if (shouldSave)
            {
                _changesSinceSave = 0;
                _lastSaveUtc = DateTime.UtcNow;
            }
        }
        if (shouldSave) Save();
    }

    public double GetAverageSpeed(string srcId, string dstId)
    {
        var key = (srcId, dstId);
        lock (_lock)
        {
            if (!_speeds.TryGetValue(key, out var queue) || queue.Count == 0)
                return 0;
            return queue.Average();
        }
    }

    /// <summary>
    /// Flush all current samples to disk. No-op if persistence wasn't enabled.
    /// Safe to call from shutdown paths — swallows all I/O exceptions.
    /// </summary>
    public void Save()
    {
        if (_persistPath == null) return;
        try
        {
            Dictionary<string, double[]> snapshot;
            lock (_lock)
            {
                snapshot = _speeds.ToDictionary(
                    kv => $"{kv.Key.src}\t{kv.Key.dst}",
                    kv => kv.Value.ToArray());
            }

            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SpeedTracker: save failed (non-fatal)");
        }
    }

    private void Load()
    {
        if (_persistPath == null || !File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath);
            var snapshot = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);
            if (snapshot == null) return;

            lock (_lock)
            {
                _speeds.Clear();
                foreach (var (key, values) in snapshot)
                {
                    var parts = key.Split('\t', 2);
                    if (parts.Length != 2) continue;
                    var queue = new Queue<double>();
                    foreach (var v in values.TakeLast(MaxSamples))
                        if (v > 0 && !double.IsNaN(v) && !double.IsInfinity(v))
                            queue.Enqueue(v);
                    if (queue.Count > 0)
                        _speeds[(parts[0], parts[1])] = queue;
                }
            }
            Log.Information("SpeedTracker: loaded {Count} routes from {Path}",
                _speeds.Count, _persistPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SpeedTracker: load failed (non-fatal)");
        }
    }
}
