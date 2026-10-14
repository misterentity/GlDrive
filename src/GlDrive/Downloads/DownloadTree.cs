using System.IO;
using FluentFTP;
using GlDrive.Ftp;

namespace GlDrive.Downloads;

internal sealed record DownloadTreePlan(bool IsDirectory, IReadOnlyList<FtpDownloadFile> Files,
    IReadOnlyList<string> Directories);

internal static class DownloadTree
{
    internal static async Task<DownloadTreePlan> BuildAsync(string remotePath, string localPath,
        Func<string, CancellationToken, Task<bool>> directoryExists,
        Func<string, CancellationToken, Task<bool>> fileExists,
        Func<string, CancellationToken, Task<FtpListItem[]>> list,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isDirectory = await directoryExists(remotePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!isDirectory)
        {
            var isFile = await fileExists(remotePath, ct);
            ct.ThrowIfCancellationRequested();
            if (!isFile)
                throw new FileNotFoundException($"Remote path does not exist or is inaccessible: {remotePath}");
            return new DownloadTreePlan(false, [new FtpDownloadFile(remotePath, localPath, -1)], []);
        }

        var root = Path.GetFullPath(localPath);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<FtpDownloadFile>();
        var directories = new List<string> { root };
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var pending = new Queue<(string Remote, string Local)>();
        pending.Enqueue((FtpSearchService.NormalizeSearchRoot(remotePath), root));

        while (pending.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            // Failed listings must fail the download, rather than mark a partial tree complete.
            var entries = await list(directory.Remote, ct);
            // A final empty listing has no foreach iteration in which to observe
            // cancellation, and no transfer will follow to observe it for us.
            ct.ThrowIfCancellationRequested();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name is "." or "..") continue;
                if (entry.Type == FtpObjectType.Directory && FtpSearchService.SkipDirectory(entry.Name)) continue;
                // Do not follow symlinks: they can point back to a parent or outside this release.
                if (entry.Type is not (FtpObjectType.File or FtpObjectType.Directory)) continue;
                if (string.IsNullOrEmpty(entry.Name) || entry.Name.Contains('/') || entry.Name.Contains('\\'))
                    throw new IOException($"Unsafe entry name in {directory.Remote}: {entry.Name}");

                var safeName = PathSanitizer.Sanitize(entry.Name);
                var destination = Path.GetFullPath(Path.Combine(directory.Local, safeName));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !destinations.Add(destination))
                    throw new IOException($"Download entry escapes or collides with another local path: {entry.Name}");

                var source = directory.Remote.TrimEnd('/') + "/" + entry.Name;
                if (entry.Type == FtpObjectType.Directory)
                {
                    directories.Add(destination);
                    pending.Enqueue((source, destination));
                }
                else
                {
                    files.Add(new FtpDownloadFile(source, destination, entry.Size));
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return new DownloadTreePlan(true, files, directories);
    }
}
