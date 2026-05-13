using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Writes a heartbeat JSON file every 30 seconds containing process state
/// (pid, time, version, thread count, working set, GC counts). On startup,
/// the previous instance's last heartbeat can be inspected via
/// <see cref="CheckStaleHeartbeat"/> to distinguish instant native crashes
/// (recent heartbeat) from hangs that crashed later (stale heartbeat).
///
/// Native crashes in GnuTLS / WinFsp / CPSV bypass managed exception handlers
/// (Dispatcher / AppDomain / Unobserved) and skip the v1.65 crashdump writer,
/// so this is the only diagnostic that survives such crashes.
///
/// All file I/O is best-effort — this class must NEVER throw.
/// </summary>
public sealed class HeartbeatMonitor : IDisposable
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlDrive", "logs", "last-heartbeat.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly Timer _timer;
    private int _disposed;

    public HeartbeatMonitor()
    {
        // Fire immediately, then every 30 seconds.
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private static void Tick()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            var snapshot = new HeartbeatSnapshot(
                Pid: proc.Id,
                TimeUtc: DateTime.UtcNow.ToString("O"),
                Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                ThreadCount: proc.Threads.Count,
                WorkingSetMb: proc.WorkingSet64 / (1024 * 1024),
                GcGen0: GC.CollectionCount(0),
                GcGen1: GC.CollectionCount(1),
                GcGen2: GC.CollectionCount(2));

            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort — never let the heartbeat writer crash the process.
        }
    }

    /// <summary>
    /// Reads the heartbeat file written by the previous app instance.
    /// MUST be called BEFORE constructing a new <see cref="HeartbeatMonitor"/>,
    /// otherwise the previous-instance data will already be overwritten.
    /// </summary>
    public static HeartbeatCheckResult CheckStaleHeartbeat()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new HeartbeatCheckResult(false, null, null);

            var raw = File.ReadAllText(FilePath);
            HeartbeatSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<HeartbeatSnapshot>(raw, JsonOptions);
            }
            catch
            {
                return new HeartbeatCheckResult(false, null, null);
            }

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TimeUtc))
                return new HeartbeatCheckResult(false, null, null);

            if (!DateTime.TryParse(
                    snapshot.TimeUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var heartbeatTime))
            {
                return new HeartbeatCheckResult(false, null, null);
            }

            var age = DateTime.UtcNow - heartbeatTime.ToUniversalTime();
            return new HeartbeatCheckResult(true, age, raw);
        }
        catch
        {
            return new HeartbeatCheckResult(false, null, null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _timer.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "HeartbeatMonitor timer dispose failed"); }
    }
}

public sealed record HeartbeatSnapshot(
    int Pid,
    string TimeUtc,
    string? Version,
    int ThreadCount,
    long WorkingSetMb,
    int GcGen0,
    int GcGen1,
    int GcGen2);

public sealed record HeartbeatCheckResult(
    bool HadHeartbeat,
    TimeSpan? AgeAtStartup,
    string? RawJson);
