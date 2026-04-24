namespace GlDrive.Downloads;

public enum MediaType { Movie, TvShow }
public enum WishlistStatus { Watching, Completed, Paused }
public enum QualityProfile { Any, SD, Q720p, Q1080p, Q2160p }
public enum DownloadStatus { Queued, Downloading, Extracting, Completed, Failed, Cancelled }

public class WishlistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MediaType Type { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TvMazeId { get; set; }
    public QualityProfile Quality { get; set; } = QualityProfile.Q1080p;
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

public class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
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
    public string Category { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int RetryCount { get; set; }
}

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

public record ParsedRelease(
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    QualityProfile Quality,
    string? Group,
    bool IsSeasonPack,
    string? Source = null);

public record DownloadProgress(long DownloadedBytes, long TotalBytes, double BytesPerSecond, string? CurrentFileName = null);

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
