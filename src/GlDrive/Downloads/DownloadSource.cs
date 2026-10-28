using System.IO;
using GlDrive.Ftp;

namespace GlDrive.Downloads;

internal interface IDownloadSource
{
    bool IsInCooldown { get; }
    Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct);
    Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct);
    Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress,
        CancellationToken ct, DownloadExecutionPolicy policy) => DownloadAsync(files, progress, ct);
    Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct);
}

internal sealed class DownloadSource(FtpOperations ftp, StreamingDownloader downloader) : IDownloadSource
{
    public bool IsInCooldown => downloader.IsPoolInCooldown;
    public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct) =>
        DownloadTree.BuildAsync(remotePath, localPath, ftp.DirectoryExists, ftp.FileExists, ftp.ListDirectory, ct);
    public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        downloader.DownloadFiles(files, progress, ct);
    public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress,
        CancellationToken ct, DownloadExecutionPolicy policy) =>
        downloader.DownloadFiles(files, progress, ct, policy.YieldWhen, policy.OnBytes);

    public async Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct)
    {
        // Manifests can be replaced while volumes are arriving, even at the same
        // size. Never use the volume cache's size-only fast path for them.
        var directory = Path.GetDirectoryName(file.LocalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(file.LocalPath)}.{Guid.NewGuid():N}.gldrive-tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await ftp.DownloadToStream(file.RemotePath, output, ct);
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, file.LocalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
