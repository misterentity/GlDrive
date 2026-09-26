using System.IO;

namespace GlDrive.Downloads;

/// <summary>
/// Watched archives the extractor is holding because their output drive lacks room.
///
/// Root cause this exists for (2026-09-24/25, Dont.Be.Shy.2026.HDR.2160p.WEB.h265-EDITH): D:
/// had less free space than the 23 GB set needed. The first attempt wrote for 7.5 minutes,
/// drove the volume to ZERO bytes free — the same volume the local site uploads into — then
/// threw "There is not enough space on the disk". The failure was classified Transient, so it
/// burned all five watch retries in ~10 minutes and was abandoned in
/// <see cref="TransientAbandonLedger"/>, keyed on the volume-set fingerprint. Freeing space
/// does not change that fingerprint, so the promise "it will be retried after the folder
/// changes" could never be kept by the one action that actually fixes a full disk.
///
/// A full disk is not a property of the archive and no retry schedule moves it. The verdict is
/// keyed on the resource that is actually short: a parked path is re-offered exactly when its
/// drive has room for it again, and never consumes a retry.
/// </summary>
public sealed class ExtractSpaceParking
{
    /// <summary>Held back so an extraction never drives a shared volume to zero.</summary>
    public const long DefaultHeadroomBytes = 1L * 1024 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Root, long Required)> _parked =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, long?> _freeSpace;
    private readonly long _headroom;

    public ExtractSpaceParking(Func<string, long?>? freeSpace = null, long headroomBytes = DefaultHeadroomBytes)
    {
        _freeSpace = freeSpace ?? DefaultFreeSpace;
        _headroom = Math.Max(0, headroomBytes);
    }

    /// <summary>Free bytes on <paramref name="root"/>, or null when the drive cannot be measured.</summary>
    public static long? DefaultFreeSpace(string root)
    {
        try
        {
            var di = new DriveInfo(root);
            return di.IsReady ? di.AvailableFreeSpace : null;
        }
        catch { return null; }
    }

    public static string RootOf(string path) => Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

    public long Headroom => _headroom;

    /// <summary>
    /// Would <paramref name="requiredBytes"/> fit on the drive holding <paramref name="outputDir"/>?
    /// An unmeasurable drive answers yes: the preflight must never block on missing evidence —
    /// the extraction itself will report a real failure.
    /// </summary>
    public bool Fits(string outputDir, long requiredBytes, out long? freeBytes)
    {
        freeBytes = _freeSpace(RootOf(outputDir));
        return freeBytes is not { } free || free - _headroom >= requiredBytes;
    }

    /// <summary>Hold <paramref name="path"/> until its drive has room. Returns true the first time.</summary>
    public bool Park(string path, string outputDir, long requiredBytes)
    {
        lock (_lock)
        {
            var first = !_parked.ContainsKey(path);
            _parked[path] = (RootOf(outputDir), Math.Max(0, requiredBytes));
            return first;
        }
    }

    public bool IsParked(string path)
    {
        lock (_lock) return _parked.ContainsKey(path);
    }

    public void Remove(string path)
    {
        lock (_lock) _parked.Remove(path);
    }

    /// <summary>
    /// Release every parked path whose drive now has room, and return them. An unmeasurable
    /// drive keeps its paths parked — unlike the preflight, releasing here would restart a
    /// write we already have direct evidence cannot complete.
    /// </summary>
    public List<string> TakeReady()
    {
        lock (_lock)
        {
            var ready = new List<string>();
            foreach (var (path, (root, required)) in _parked)
                if (_freeSpace(root) is { } free && free - _headroom >= required)
                    ready.Add(path);

            foreach (var path in ready) _parked.Remove(path);
            return ready;
        }
    }

    private const int ErrorHandleDiskFull = 0x27; // ERROR_HANDLE_DISK_FULL (39)
    private const int ErrorDiskFull = 0x70;       // ERROR_DISK_FULL (112)

    /// <summary>
    /// Did this failure come from the output volume running out of space? Keyed on the Win32
    /// error code carried in HResult, not the localized message. UnRAR reports a full disk as a
    /// write error (exit 5), which reaches us only as text.
    /// </summary>
    public static bool IsDiskFull(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var code = e.HResult & 0xFFFF;
            if (e is IOException && (code == ErrorDiskFull || code == ErrorHandleDiskFull)) return true;
            if (e.Message.Contains("rar.exe failed (exit 5)", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
