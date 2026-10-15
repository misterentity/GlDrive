using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WholeDownloadModelContractTests
{
    [Theory]
    [InlineData(DownloadSourceKind.Ftp)]
    [InlineData(DownloadSourceKind.Torrent)]
    public void Paused_download_keeps_its_archive_owned_by_the_queue(DownloadSourceKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "paused-release");
        var item = new DownloadItem { SourceKind = kind, Status = DownloadStatus.Paused, LocalPath = root };
        Assert.True(DownloadExtractionGuard.Blocks(item, Path.Combine(root, "CD1", "disc.rar")));
        Assert.False(DownloadExtractionGuard.Blocks(item, Path.Combine(root + "-unrelated", "disc.rar")));
    }

    [Fact]
    public void Persisted_paused_torrent_remains_protected_before_its_manager_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-paused-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var item = new DownloadItem { SourceKind = DownloadSourceKind.Torrent,
                Status = DownloadStatus.Paused, LocalPath = Path.Combine(root, "release") };
            File.WriteAllText(Path.Combine(root, "downloads-__torrents.json"), JsonSerializer.Serialize(new[] { item }));
            var guard = new DownloadExtractionGuard(root, live: () => []);
            Assert.Equal(item.Id, guard.FindOwner(Path.Combine(item.LocalPath, "disc.rar"))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_numeric_statuses_are_preserved_when_paused_is_added()
    {
        Assert.Equal(0, (int)DownloadStatus.Queued);
        Assert.Equal(1, (int)DownloadStatus.Downloading);
        Assert.Equal(2, (int)DownloadStatus.Extracting);
        Assert.Equal(3, (int)DownloadStatus.Completed);
        Assert.Equal(4, (int)DownloadStatus.Failed);
        Assert.Equal(5, (int)DownloadStatus.Cancelled);
        Assert.Equal(0, (int)DownloadSourceKind.Ftp);
    }
}
