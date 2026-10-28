namespace GlDrive.Downloads;

public enum MediaType { Movie, TvShow }
public enum WishlistStatus { Watching, Completed, Paused }
public enum DownloadStatus { Queued, Downloading, Extracting, Completed, Failed, Cancelled, Paused }
public enum DownloadSourceKind { Ftp, Torrent }

public class WishlistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MediaType Type { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TvMazeId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public string Source { get; set; } = "";
    public string? PlexWatchlistId { get; set; }
    public bool WatchlistSeriesEnded { get; set; }
    public DateTimeOffset? WatchlistMetadataCheckedAtUtc { get; set; }
    public QualityProfile Quality { get; set; } = QualityProfile.Q1080p;
    public string? ProfileId { get; set; }
    /// <summary>Null uses the global floor or an explicitly configured profile's permission.</summary>
    public bool? AllowLowQualitySources { get; set; }
    public MovieReleaseDates? ReleaseDates { get; set; }
    public SeriesMonitorMode MonitorMode { get; set; } = SeriesMonitorMode.Legacy;
    public bool AutoPullCompletedRaces { get; set; }
    public DateTimeOffset? MonitorSinceUtc { get; set; }
    public List<SeriesEpisode> SeriesInventory { get; set; } = [];
    public DateTimeOffset? SeriesInventoryUpdatedAtUtc { get; set; }
    public DateTimeOffset? SeriesInventoryLastAttemptAtUtc { get; set; }
    /// <summary>Verified successful downloads, separately for each movie or episode.</summary>
    public List<WishlistCopy> BestOwned { get; set; } = [];
    /// <summary>Durable reservations shared by every source server.</summary>
    public List<WishlistCopy> PendingGrabs { get; set; } = [];
    public WishlistStatus Status { get; set; } = WishlistStatus.Watching;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public List<string> GrabbedReleases { get; set; } = [];
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
    /// <summary>Soft-deleted by AI agent pruning — excluded from auto-matching but retained for history.</summary>
    public bool Dead { get; set; }
}

public sealed class WishlistCopy
{
    public string Key { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string DownloadId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public long? SizeBytes { get; set; }
    public string? LocalPath { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class DownloadItem
{
    // Diagnostic only: stale persisted deadlines must never delay work after a restart.
    [System.Text.Json.Serialization.JsonIgnore]
    public GlDrive.Services.WaitingState? RuntimeWait { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DownloadSourceKind SourceKind { get; set; }
    // Keep tracker-bearing magnet URIs out of RemotePath, which is used in telemetry.
    public string? TorrentMagnet { get; set; }
    public bool SkipBlockedTorrentFiles { get; set; }
    public bool RequiresContentApproval { get; set; }
    public string? StatusDetail { get; set; }
    public string RemotePath { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? WishlistItemId { get; set; }
    /// <summary>Automatic correction provenance remains on every terminal queue row.</summary>
    public string? NukeCorrectionForRelease { get; set; }
    public string Category { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int RetryCount { get; set; }
    /// <summary>Positive incomplete-source evidence survives restarts until verified.</summary>
    public bool SourceCompletionPending { get; set; }
    /// <summary>Managed pulls yield their existing pool connection to racing.</summary>
    public bool BackgroundWantedPull { get; set; }
    public int RelocationAttempts { get; set; }
    public DateTime? LastRelocationLookupAt { get; set; }
    public string? SfvRepairRoot { get; set; }
    public string? SfvRepairOwner { get; set; }
    public List<SfvRepairFile> SfvRepairFiles { get; set; } = [];
    /// <summary>Positive CRC failure holds watch-folder extraction until verification succeeds.</summary>
    public bool SfvIntegrityHold { get; set; }
}

public sealed class SfvRepairFile
{
    public string LocalPath { get; set; } = "";
    public int Attempts { get; set; }
    public uint? LastBadCrc { get; set; }
    public bool Pending { get; set; }
    public string? QuarantinePath { get; set; }
    public long QuarantineLength { get; set; }
    public DateTime QuarantineLastWriteUtc { get; set; }
    public List<SfvQuarantineFile> PriorQuarantines { get; set; } = [];
}

public sealed record SfvQuarantineFile(string Path, long Length, DateTime LastWriteUtc);

public class SearchResult
{
    public string ReleaseName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public List<string> Files { get; set; } = [];
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
}

public record DownloadProgress(long DownloadedBytes, long TotalBytes, double BytesPerSecond, string? CurrentFileName = null,
    IReadOnlyList<GlDrive.Downloads.Segmented.LaneStats>? Lanes = null,
    GlDrive.Downloads.Segmented.TransferMap? Map = null,
    string? StatusDetail = null);

/// <summary>
/// Sanitizes a string for use as a Windows path segment (file or folder name).
/// Strips characters illegal on Windows and trims leading/trailing dots/spaces.
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] IllegalChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_";

        var span = name.AsSpan();
        Span<char> buf = stackalloc char[Math.Min(span.Length, 255)];
        int len = 0;

        for (int i = 0; i < span.Length && len < buf.Length; i++)
        {
            var c = span[i];
            if (c < 32 || IllegalChars.AsSpan().Contains(c))
                continue; // strip illegal chars and control chars
            buf[len++] = c;
        }

        // Trim leading/trailing dots and spaces (Windows silently strips these)
        var result = buf[..len];
        result = result.TrimStart(['.', ' ']);
        result = result.TrimEnd(['.', ' ']);

        if (result.Length == 0) return "_";

        var str = new string(result);

        // Check for Windows reserved device names (CON, NUL, COM1, etc.)
        var baseName = System.IO.Path.GetFileNameWithoutExtension(str);
        if (ReservedNames.Contains(baseName))
            str = "_" + str;

        return str;
    }
}
