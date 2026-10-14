using System.IO;
using FluentFTP;
using GlDrive.Ftp;

namespace GlDrive.Downloads;

internal sealed record DownloadTreePlan(bool IsDirectory, IReadOnlyList<FtpDownloadFile> Files,
    IReadOnlyList<string> Directories)
{
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];
    public bool HasCompletionMarker { get; init; }
}

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
        var incomplete = new List<string>();
        var completeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                // Validate before classifying metadata: a leading dot in ../file
                // must not turn a traversal attempt into an ignored hidden file.
                if (string.IsNullOrEmpty(entry.Name) || entry.Name.Contains('/') || entry.Name.Contains('\\'))
                    throw new IOException($"Unsafe entry name in {directory.Remote}: {entry.Name}");
                // Record evidence BEFORE dropping markers, including markers
                // represented by directories. None of these are downloadable data.
                if (ReleaseSourceState.IsIncompleteMarker(entry.Name))
                    incomplete.Add(directory.Remote.TrimEnd('/') + "/" + entry.Name);
                if (ReleaseSourceState.IsCompletionMarker(entry.Name))
                    completeDirectories.Add(directory.Local);
                if (ReleaseSourceState.IsSiteMetadata(entry.Name, entry.Size)) continue;
                if (entry.Type == FtpObjectType.Directory && FtpSearchService.SkipDirectory(entry.Name)) continue;
                // Do not follow symlinks: they can point back to a parent or outside this release.
                if (entry.Type is not (FtpObjectType.File or FtpObjectType.Directory)) continue;

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
        var archives = files.Where(f => ReleaseSourceState.IsArchive(f.LocalPath)).ToArray();
        // A completed child only vouches for its own subtree, not another disc.
        var complete = completeDirectories.Contains(root) || (archives.Length > 0 && archives.All(f =>
            completeDirectories.Any(d => f.LocalPath.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
        return new DownloadTreePlan(true, files, directories)
        {
            IncompleteReasons = incomplete,
            HasCompletionMarker = complete
        };
    }
}
