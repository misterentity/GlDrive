using System.IO;
using GlDrive.Library;

namespace GlDrive.Downloads;

/// <summary>Local post-processing can resume without contacting a completed download's source.</summary>
public sealed class DownloadExtractionCheckpoint
{
    public string LocalRoot { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public DownloadSourceKind SourceKind { get; set; }
    public bool VerifiedSfv { get; set; }
    public bool RequiredNfo { get; set; }
    public List<string> Directories { get; set; } = [];
    public List<ExtractionInputSnapshot> Inputs { get; set; } = [];
    public string? WaitingDirectory { get; set; }
    public long RequiredBytes { get; set; }

    // Stores and API readers can still serialize an earlier published snapshot.
    // Publish a new object at every transition instead of mutating its lists.
    internal DownloadExtractionCheckpoint Transition(string? waitingDirectory = null, long requiredBytes = 0,
        string? completedDirectory = null) => new()
    {
        LocalRoot = LocalRoot, RemotePath = RemotePath, SourceKind = SourceKind,
        VerifiedSfv = VerifiedSfv, RequiredNfo = RequiredNfo,
        WaitingDirectory = waitingDirectory, RequiredBytes = requiredBytes,
        Directories = Directories.Where(dir => !string.Equals(dir, completedDirectory, StringComparison.OrdinalIgnoreCase)).ToList(),
        Inputs = Inputs.Where(file => !string.Equals(Path.GetDirectoryName(file.Path), completedDirectory, StringComparison.OrdinalIgnoreCase)).ToList()
    };

    internal static DownloadExtractionCheckpoint Capture(DownloadItem item, IReadOnlyList<string> directories,
        bool verifiedSfv = false, bool requiredNfo = false)
    {
        if (directories.Count > 10000 || directories.Any(dir => !SafePath(item.LocalPath, dir)))
            throw new IOException("Extraction directories must stay within the local download root");
        var inputs = directories.SelectMany(dir => Directory.EnumerateFiles(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100001).ToList();
        if (inputs.Count > 100000 || inputs.Any(path => !SafePath(item.LocalPath, path)))
            throw new IOException("Extraction inputs must stay within the local download root");
        return new()
        {
            LocalRoot = LibraryPlanner.NormalizeRoot(item.LocalPath), RemotePath = item.RemotePath,
            SourceKind = item.SourceKind, VerifiedSfv = verifiedSfv, RequiredNfo = requiredNfo,
            Directories = directories.Select(LibraryPlanner.NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Inputs = inputs.Select(path =>
                {
                    var file = new FileInfo(path);
                    return new ExtractionInputSnapshot(file.FullName, file.Length, file.LastWriteTimeUtc);
                }).ToList()
        };
    }

    internal static bool SafePath(string localRoot, string path)
    {
        try
        {
            var root = LibraryPlanner.NormalizeRoot(localRoot);
            var full = LibraryPlanner.NormalizePath(path);
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !LibraryPlanner.IsWithin(full, root)) return false;
            LibraryPlanner.RejectReparseAncestors(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }

    internal bool IsCurrent(DownloadItem item, bool requireSfv = false, bool requireNfo = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LocalRoot) || Directories == null || Inputs == null ||
                Directories.Any(string.IsNullOrWhiteSpace) || Inputs.Any(file => file == null || string.IsNullOrWhiteSpace(file.Path))) return false;
            var root = LibraryPlanner.NormalizeRoot(item.LocalPath);
            if (!root.Equals(LocalRoot, StringComparison.OrdinalIgnoreCase) ||
                item.RemotePath != RemotePath || item.SourceKind != SourceKind || item.SfvIntegrityHold || RequiredBytes < 0 ||
                requireSfv && !VerifiedSfv || requireNfo && !RequiredNfo ||
                Directories.Count > 10000 || Inputs.Count > 100000 ||
                Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Directories.Count ||
                Inputs.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Inputs.Count ||
                Directories.Any(dir => !SafePath(root, dir) || !dir.Equals(LibraryPlanner.NormalizeRoot(dir), StringComparison.OrdinalIgnoreCase)) ||
                WaitingDirectory != null && (!SafePath(root, WaitingDirectory) ||
                    !Directories.Contains(WaitingDirectory, StringComparer.OrdinalIgnoreCase))) return false;
            // New extracted outputs are expected. New/removed archive inputs or
            // manifests are not: they must pass the normal completion gates.
            static bool ArchiveInput(string path) => ArchiveExtractor.IsArchiveFile(path) ||
                Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase);
            var currentArchives = Directories.SelectMany(dir => Directory.EnumerateFiles(dir))
                .Where(ArchiveInput).Take(100001).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (currentArchives.Count > 100000 || !currentArchives.SetEquals(Inputs.Where(file => ArchiveInput(file.Path)).Select(file => file.Path))) return false;
            return Inputs.All(saved => SafePath(root, saved.Path) && new FileInfo(saved.Path) is { Exists: true } file &&
                file.Length == saved.Length && file.LastWriteTimeUtc == saved.ModifiedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
}

public sealed record ExtractionInputSnapshot(string Path, long Length, DateTime ModifiedUtc);
