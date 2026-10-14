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

    internal async Task<object> AutomationDownloadTorrent(string magnet, string directory, bool skipBlockedFiles)
    {
        if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A magnet URI is required");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute destination directory is required");
        if (_torrentStream == null) throw new InvalidOperationException("Torrent engine is still initializing; retry shortly");
        var item = new TorrentDownloadVm { Name = "MCP download", Status = "Starting" };
        TorrentDownloads.Add(item);
        OnPropertyChanged(nameof(HasTorrentDownloads));
        var hash = await RunDownloadAsync(magnet, directory, item, skipBlockedFiles);
        if (hash != null) item.Hash = hash;
        return new { hash, status = item.Status, blockReason = item.BlockReason, started = hash != null };
    }
}
