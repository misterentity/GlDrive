using System.IO;

namespace GlDrive.Downloads;

/// <summary>
/// A protocol that transfers and verifies a complete item itself, while DownloadManager
/// owns its queue, persistence, cancellation, retries, disk budget and extraction.
/// Preparation must not start payload writes: the manager reserves disk space first.
/// </summary>
internal interface IWholeDownloadSource
{
    Task<IPreparedDownload> PrepareAsync(DownloadItem item,
        IProgress<DownloadProgress>? progress, CancellationToken ct);
}

internal interface IPreparedDownload : IAsyncDisposable
{
    long TotalBytes { get; }
    IReadOnlyList<string> Directories { get; }

    /// <summary>Returns only after integrity verification and closing payload writers.</summary>
    Task ExecuteAsync(IProgress<DownloadProgress>? progress, CancellationToken ct);
}

/// <summary>A deliberate refusal that retries cannot resolve without a user decision.</summary>
internal class DownloadBlockedException(string message, bool canSkipFiles = false) : IOException(message)
{
    public bool CanSkipFiles { get; } = canSkipFiles;
}
