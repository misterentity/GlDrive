using System.IO;

namespace GlDrive.UI;

public partial class PlayerViewModel
{
    internal async Task AutomationPlayLocal(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException("An existing absolute media path is required");
        if (_mediaPlayer == null) throw new InvalidOperationException("Player is still initializing; retry after ui.state shows it ready");
        await PlayLocalFile(path);
    }

    internal Task<object> AutomationDownloadTorrent(string magnet, string directory, bool skipBlockedFiles)
    {
        if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A magnet URI is required");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute destination directory is required");
        var item = QueueTorrentDownload(magnet, "MCP download", directory, skipBlockedFiles);
        return Task.FromResult<object>(new
        {
            id = item.Id, queueId = item.ServerId, hash = item.RemotePath,
            status = item.Status.ToString(), blockReason = item.ErrorMessage,
            queued = true, started = item.Status is GlDrive.Downloads.DownloadStatus.Downloading or GlDrive.Downloads.DownloadStatus.Extracting,
            path = item.LocalPath
        });
    }
}
