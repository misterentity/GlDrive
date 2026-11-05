using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// What the extractor knows about archive files on disk: which file opens a set, which files
/// belong to it, and how to observe a set that may still be arriving. No state, no UI.
/// </summary>
internal static class ArchiveVolumes
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".lz", ".lzma", ".iso", ".cab",
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lz",
    };

    // Old-style volume extensions (.r00-.r99, .s00-.s99) and split archives (.001-.999)
    private static readonly Regex VolumeExtRegex = new(@"^\.[rs]\d{2,3}$|^\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Modern RAR multi-part: name.part02.rar, name.part003.rar (part01 is the first volume, keep it)
    private static readonly Regex RarPartNonFirstRegex = new(@"\.part(?!0*1\.rar)(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Match any volume file belonging to a RAR set (for size calculation)
    private static readonly Regex RarVolumeFileRegex = new(@"\.[rs]\d{2,3}$|\.part\d+\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True for the one file that opens an archive set and should be queued: a supported
    /// archive extension that is NOT a continuation volume (.r00/.s01/.001, .part02.rar...).
    /// Split archives named <c>name.7z.001</c> are continuation-shaped and are not queued.
    /// </summary>
    internal static bool IsFirstVolume(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;

        // Skip old-style volume files (.r00, .s01, .001, etc.)
        if (VolumeExtRegex.IsMatch(ext)) return false;

        // Skip modern multi-part RAR volumes that aren't the first part
        // e.g. name.part02.rar, name.part03.rar (but keep name.part01.rar and plain .rar)
        var name = Path.GetFileName(path);
        if (RarPartNonFirstRegex.IsMatch(name)) return false;

        // Check compound extensions like .tar.gz
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lz" })
        {
            if (name.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Map any file of a set (a watcher event on .r05 or .part07.rar) to the first volume the
    /// set is extracted from. Null when the file is not part of an extractable set.
    /// </summary>
    internal static string? ResolveFirstVolumePath(string path)
    {
        if (IsFirstVolume(path)) return path;
        var ext = Path.GetExtension(path);
        if (Regex.IsMatch(ext, @"^\.[rs]\d{2,3}$", RegexOptions.IgnoreCase))
            return Path.ChangeExtension(path, ".rar");

        var name = Path.GetFileName(path);
        var match = Regex.Match(name, @"^(?<base>.+\.part)(?<number>\d+)\.rar$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var first = 1.ToString($"D{match.Groups["number"].Value.Length}");
        return Path.Combine(Path.GetDirectoryName(path) ?? "", $"{match.Groups["base"].Value}{first}.rar");
    }

    /// <summary>
    /// Background-safe archive path collector. Does NOT touch any UI state.
    /// Used by the startup initial scan to avoid blocking the UI thread on
    /// slow/network-backed watch folders.
    /// </summary>
    internal static void CollectArchivePaths(string dir, bool recursive, List<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                if (IsFirstVolume(file))
                    results.Add(file);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        if (!recursive) return;

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subdirs)
        {
            try
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                CollectArchivePaths(sub, true, results);
            }
            catch { }
        }
    }

    /// <summary>
    /// Manually recurse directories so a single permission error doesn't kill the whole scan.
    /// Directory.EnumerateFiles with AllDirectories throws on the first inaccessible dir.
    /// </summary>
    internal static void ScanDirectory(string dir, bool recursive, Action<string> onArchive)
    {
        // Scan files in this directory
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsFirstVolume(file))
                    onArchive(file);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Access denied scanning files in {Dir}", dir);
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "I/O error scanning files in {Dir}", dir);
        }

        if (!recursive) return;

        // Recurse into subdirectories one at a time
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Access denied listing subdirs of {Dir}", dir);
            return;
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "I/O error listing subdirs of {Dir}", dir);
            return;
        }

        foreach (var subdir in subdirs)
        {
            try
            {
                if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0) continue;
                ScanDirectory(subdir, true, onArchive);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug(ex, "Access denied entering {Dir}", subdir);
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "I/O error entering {Dir}", subdir);
            }
        }
    }

    internal static ArchiveItem CreateItem(string path)
    {
        var info = new FileInfo(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        // Detect compound types
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) ext = "TGZ";
        else if (name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)) ext = "TBZ2";
        else if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) ext = "TXZ";

        // Calculate total size across all volumes for RAR sets
        var totalSize = info.Exists ? info.Length : 0L;
        var volumeCount = 1;
        if (ext == "RAR" && info.Directory != null)
        {
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(path);

                // Strip .partNN for modern multi-part naming
                var partMatch = Regex.Match(baseName, @"^(.+)\.part\d+$", RegexOptions.IgnoreCase);
                var setBase = partMatch.Success ? partMatch.Groups[1].Value : baseName;

                foreach (var file in info.Directory.EnumerateFiles())
                {
                    if (file.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fn = file.Name;

                    // Old-style: baseName.r00, baseName.r01, baseName.s00, ...
                    if (fn.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                        VolumeExtRegex.IsMatch(Path.GetExtension(fn)))
                    {
                        totalSize += file.Length;
                        volumeCount++;
                    }
                    // Modern multi-part: setBase.partNN.rar
                    else if (partMatch.Success &&
                             fn.StartsWith(setBase, StringComparison.OrdinalIgnoreCase) &&
                             RarVolumeFileRegex.IsMatch(fn) &&
                             fn.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        totalSize += file.Length;
                        volumeCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to enumerate volume files for {Path}", path);
            }
        }

        var typeLabel = volumeCount > 1 ? $"RAR×{volumeCount}" : ext;

        return new ArchiveItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            ArchiveType = typeLabel,
            Size = totalSize,
            SizeText = ByteFormat.Compact(totalSize),
            Status = "Queued",
        };
    }

    /// <summary>
    /// For old-style scene multi-volume RARs (name.rar + name.r00 + name.r01 + ...
    /// + optionally name.s00 + ...) enumerate all volumes in order and return the
    /// complete file list. Returns null if this doesn't look like an old-style set
    /// (e.g. for .partNN.rar modern multi-volume, for single-file RARs, or for
    /// non-RAR archives).
    /// </summary>
    internal static List<FileInfo>? TryDiscoverRarVolumes(string firstVolumePath)
    {
        try
        {
            var ext = Path.GetExtension(firstVolumePath);
            if (!ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)) return null;

            var dir = Path.GetDirectoryName(firstVolumePath);
            if (dir == null || !Directory.Exists(dir)) return null;

            var baseName = Path.GetFileNameWithoutExtension(firstVolumePath);
            // Skip modern .partNN.rar naming — SharpCompress handles that via
            // single-path auto-detection. This helper is only for old-style sets.
            if (Regex.IsMatch(baseName, @"\.part\d+$", RegexOptions.IgnoreCase))
                return null;

            var result = new List<FileInfo> { new(firstVolumePath) };

            // Enumerate all files in the directory once to avoid 2000+ File.Exists calls
            var candidates = new DirectoryInfo(dir)
                .EnumerateFiles($"{baseName}.*")
                .Where(f => Path.GetFileNameWithoutExtension(f.Name)
                    .Equals(baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // .r00 – .r999 volumes, in numeric order
            var rVolumes = candidates
                .Select(f => new { File = f, Match = Regex.Match(f.Extension, @"^\.r(\d{2,3})$", RegexOptions.IgnoreCase) })
                .Where(x => x.Match.Success)
                .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.File);
            result.AddRange(rVolumes);

            // .s00 – .s999 continuation volumes (used by some releases after r99)
            var sVolumes = candidates
                .Select(f => new { File = f, Match = Regex.Match(f.Extension, @"^\.s(\d{2,3})$", RegexOptions.IgnoreCase) })
                .Where(x => x.Match.Success)
                .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.File);
            result.AddRange(sVolumes);

            return result.Count > 1 ? result : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Extractor: volume discovery failed for {File}", firstVolumePath);
            return null;
        }
    }

    /// <summary>
    /// Observe a volume set: how many parts exist, how many bytes they hold, and how many are
    /// still write-locked. A part that cannot be opened for exclusive read is being written;
    /// that is the signal the old first-volume-only gate had no way to see.
    /// </summary>
    internal static VolumeSetReadiness.Snapshot SampleVolumeSet(string firstVolumePath)
    {
        try
        {
            var volumes = VolumeSetReadiness.DiscoverVolumes(firstVolumePath);

            long totalBytes = 0;
            var locked = 0;

            foreach (var volume in volumes)
            {
                try
                {
                    volume.Refresh();
                    if (!volume.Exists)
                    {
                        locked++;
                        continue;
                    }

                    totalBytes += volume.Length;

                    using var fs = new FileStream(volume.FullName, FileMode.Open,
                        FileAccess.Read, FileShare.Read);
                }
                catch (IOException)
                {
                    locked++;
                }
                catch (UnauthorizedAccessException)
                {
                    locked++;
                }
            }

            var missing = VolumeSetReadiness.CountMissingVolumes(
                Path.GetFileNameWithoutExtension(firstVolumePath),
                volumes.Where(v => v.Exists).Select(v => v.Name),
                VolumeSetReadiness.ReadSfvDeclaredNames(Path.GetDirectoryName(firstVolumePath)!));

            return new VolumeSetReadiness.Snapshot(volumes.Count, totalBytes, locked, missing);
        }
        catch (Exception ex)
        {
            // A set we cannot observe must not be reported as settled — return the
            // never-ready sentinel so the caller keeps waiting rather than extracting blind.
            Log.Debug(ex, "Extractor: could not sample volume set for {Path}", firstVolumePath);
            return new VolumeSetReadiness.Snapshot(0, 0, 0);
        }
    }

    /// <summary>
    /// Identify a volume set by what a retry could actually change: how many parts are
    /// present and how many bytes they hold. A missing part arriving, or a truncated one
    /// being replaced, moves this — which is exactly when a permanent verdict should lapse.
    /// Covers both old-style and modern RAR sets so either can revive when another part arrives.
    /// </summary>
    internal static (int VolumeCount, long TotalBytes) ComputeVolumeSetFingerprint(string path)
    {
        try
        {
            var volumes = VolumeSetReadiness.DiscoverVolumes(path);
            if (volumes.Count > 0)
            {
                // Per-volume, guarded. This used to be volumes.Sum(v => v.Length), which threw
                // the moment ANY part was unreadable and discarded the whole measurement — and
                // one part is unreadable in the normal case, because TryDiscoverRarVolumes
                // seeds the list with the caller's first-volume path unconditionally and for
                // old-style sets over FXP the .rar arrives LAST (it sorts after every .rNN).
                // See VolumeSetFingerprint for the 2026-08-18 incident this caused.
                var fp = VolumeSetFingerprint.FromVolumes(volumes.Select(TryMeasure));
                if (fp.VolumeCount > 0) return (fp.VolumeCount, fp.TotalBytes);
            }

            var fi = new FileInfo(path);
            return fi.Exists ? (1, fi.Length) : (0, 0);
        }
        catch (Exception ex)
        {
            // A fingerprint we genuinely cannot read (the directory itself is gone) is Unknown,
            // NOT "changed". TransientAbandonLedger has a dedicated state for it; it must never
            // be mistaken for a set that grew.
            Log.Warning(ex, "Extractor: could not fingerprint volume set for {Path}", path);
            return (VolumeSetFingerprint.Unknown.VolumeCount, VolumeSetFingerprint.Unknown.TotalBytes);
        }
    }

    /// <summary>
    /// Length of one volume, or null if it vanished or cannot be read right now. A set being
    /// written is racy by definition — a part disappearing between enumeration and measurement
    /// is ordinary, so it degrades the fingerprint rather than destroying it.
    /// </summary>
    private static long? TryMeasure(FileInfo volume)
    {
        try
        {
            volume.Refresh();
            return volume.Exists ? volume.Length : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The site's zipscript marker saying this folder is incomplete at its SOURCE — evidence a
    /// retry cannot change — or null. Only consulted when the set is actually missing parts.
    /// </summary>
    internal static string? IncompleteAtSourceMarker(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir == null || SampleVolumeSet(path).MissingCount == 0) return null;
            return ReleaseSourceState.FindIncompleteMarker(Directory.EnumerateFileSystemEntries(dir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string GetUniqueFileName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }
}
