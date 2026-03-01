namespace GlDrive.Downloads;

public enum MediaType { Movie, TvShow }
public enum WishlistStatus { Watching, Completed, Paused }
public enum QualityProfile { Any, SD, Q720p, Q1080p, Q2160p }
public enum DownloadStatus { Queued, Downloading, Completed, Failed, Cancelled }

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
    bool IsSeasonPack);

public record DownloadProgress(long DownloadedBytes, long TotalBytes, double BytesPerSecond);
