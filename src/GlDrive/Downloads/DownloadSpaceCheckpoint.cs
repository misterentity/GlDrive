using System.IO;
using GlDrive.Ftp;
using GlDrive.Library;

namespace GlDrive.Downloads;

/// <summary>A disk admission hint, never authority to download or skip source verification.</summary>
public sealed class DownloadSpaceCheckpoint
{
    public string LocalPath { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string ServerId { get; set; } = "";
    public DownloadSourceKind SourceKind { get; set; }
    public bool SkipBlockedTorrentFiles { get; set; }
    public long RequiredBytes { get; set; }
    public List<FtpDownloadFile>? Files { get; set; }

    internal static DownloadSpaceCheckpoint Capture(DownloadItem item, long bytes,
        IReadOnlyList<FtpDownloadFile>? files) => new()
    {
        LocalPath = LibraryPlanner.NormalizePath(item.LocalPath), RemotePath = item.RemotePath,
        ServerId = item.ServerId, SourceKind = item.SourceKind,
        SkipBlockedTorrentFiles = item.SkipBlockedTorrentFiles,
        RequiredBytes = bytes, Files = files?.ToList()
    };

    internal long? RemainingBytes(DownloadItem item)
    {
        try
        {
            if (RequiredBytes < 0 ||
                !LibraryPlanner.NormalizePath(item.LocalPath).Equals(LocalPath, StringComparison.OrdinalIgnoreCase) ||
                item.RemotePath != RemotePath || item.ServerId != ServerId || item.SourceKind != SourceKind ||
                item.SkipBlockedTorrentFiles != SkipBlockedTorrentFiles) return null;
            if (Files == null) return RequiredBytes == item.TotalBytes ? RequiredBytes : null;
            if (Files.Count > 100000 || Files.Any(file => file == null || file.Size < 0 ||
                !DownloadExtractionCheckpoint.SafePath(LocalPath, file.LocalPath))) return null;
            // A cache may have been completed or removed while parked. Re-measure
            // its actual files instead of indefinitely trusting saved progress.
            return DownloadManager.RequiredDownloadBytes(Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or OverflowException) { return null; }
    }
}
