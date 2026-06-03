using System.Collections.Concurrent;
using System.IO;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>
/// Process-wide disk-space reservation keyed by drive root (v3.6 Phase 2b). The old
/// per-download check compared one release's size against AvailableFreeSpace in
/// isolation, so N concurrent downloads each saw "enough room" and collectively
/// overran the disk. This tracks bytes promised-but-not-yet-written per drive so a
/// reservation accounts for what other in-flight downloads already claimed. A small
/// headroom is held back so the volume never fills to 0.
/// </summary>
public sealed class DiskReservation
{
    private readonly long _headroomBytes;
    private readonly Func<string, long> _freeSpaceProvider;
    private readonly ConcurrentDictionary<string, long> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DiskReservation(long headroomBytes = 64L * 1024 * 1024, Func<string, long>? freeSpaceProvider = null)
    {
        _headroomBytes = Math.Max(0, headroomBytes);
        _freeSpaceProvider = freeSpaceProvider ?? DefaultFreeSpace;
    }

    private static long DefaultFreeSpace(string root)
    {
        try
        {
            var di = new DriveInfo(root);
            return di.IsReady ? di.AvailableFreeSpace : long.MaxValue; // unknown → don't block
        }
        catch { return long.MaxValue; }
    }

    private static string RootOf(string path)
        => Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

    /// <summary>
    /// Try to reserve <paramref name="bytes"/> on the drive holding
    /// <paramref name="localPath"/>, accounting for other live reservations and
    /// headroom. Returns false (and reserves nothing) if it wouldn't fit. Pass the
    /// reservation to <see cref="Release"/> exactly once when the download finishes
    /// (success OR failure).
    /// </summary>
    public bool TryReserve(string localPath, long bytes, out string root)
    {
        root = RootOf(localPath);
        if (bytes <= 0) { return true; } // nothing to reserve (single-file unknown size)

        lock (_lock)
        {
            var free = _freeSpaceProvider(root);
            if (free == long.MaxValue) { Add(root, bytes); return true; } // unknown free → allow
            var alreadyReserved = _reserved.GetValueOrDefault(root);
            if (free - alreadyReserved - bytes < _headroomBytes)
            {
                Log.Information("Disk reservation denied on {Root}: need {Need}, free {Free}, reserved {Res}, headroom {Head}",
                    root, bytes, free, alreadyReserved, _headroomBytes);
                return false;
            }
            Add(root, bytes);
            return true;
        }
    }

    private void Add(string root, long bytes) =>
        _reserved.AddOrUpdate(root, bytes, (_, cur) => cur + bytes);

    /// <summary>Release a previously-granted reservation. Safe to call with 0.</summary>
    public void Release(string root, long bytes)
    {
        if (bytes <= 0 || string.IsNullOrEmpty(root)) return;
        _reserved.AddOrUpdate(root, 0, (_, cur) => Math.Max(0, cur - bytes));
    }

    /// <summary>Bytes currently reserved on a drive root (diagnostic/test).</summary>
    public long ReservedOn(string root) => _reserved.GetValueOrDefault(RootOf(root));
}
