using System.IO;
using System.Text;
using GlDrive.Downloads;
using GlDrive.Plex;

namespace GlDrive.Library;

public sealed record LibraryIndexEntry(LibraryMediaIdentity Identity, string Path, string Source,
    bool VerifiedOnDisk, string? PlexRatingKey, IReadOnlyList<string> Guids);
public sealed record LibraryIndexSnapshot(DateTime RefreshedAtUtc, IReadOnlyList<LibraryIndexEntry> Entries,
    int ScannedRoots, int VideoFiles, int ScanIssues, bool Complete)
{
    public static LibraryIndexSnapshot Empty { get; } = new(DateTime.MinValue, [], 0, 0, 0, false);
}
public sealed record LibraryOwnershipComparison(string Status, QualityProfile? BestObservedQuality,
    bool VerifiedOnDisk, bool MeetsCutoff, bool CandidateIsUpgrade, bool CompleteEvidence,
    IReadOnlyList<LibraryIndexEntry> Matches);

/// <summary>Explicit, read-only inventory. No polling, persistence, wishlist mutation or library writes.</summary>
public sealed class LibraryIndex
{
    private LibraryIndexSnapshot _snapshot = LibraryIndexSnapshot.Empty;
    public LibraryIndexSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public LibraryIndexSnapshot Refresh(IEnumerable<string> roots, IEnumerable<PlexLibraryItem> plexItems,
        CancellationToken cancellationToken = default)
    {
        var disk = new Dictionary<string, LibraryIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var verifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedRoots = 0;
        var issues = 0;
        foreach (var configuredRoot in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try
            {
                root = LibraryPlanner.NormalizeRoot(configuredRoot);
                LibraryPlanner.RejectReparseAncestors(root);
                if (!Directory.Exists(root)) { issues++; continue; }
            }
            catch { issues++; continue; }
            scannedRoots++;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(directory)) continue;
                try
                {
                    LibraryPlanner.RejectReparseAncestors(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline)) != 0
                                || ExcludedName(Path.GetFileName(path))) continue;
                            if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(path); continue; }
                            if (!LibraryPlanner.IsVideoPath(path)) continue;
                            LibraryPlanner.RejectReparseAncestors(path);
                            var file = new FileInfo(path);
                            if (file.Length <= 0) continue;
                            var normalized = Path.GetFullPath(path);
                            verifiedFiles.Add(normalized);
                            var identity = IdentityForFile(path);
                            if (identity != null) disk[normalized] = new(identity, normalized, "disk", true, null, []);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { issues++; }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { issues++; }
            }
        }

        var entries = new List<LibraryIndexEntry>();
        // Plex paths may belong to a case-sensitive server filesystem; local Windows
        // merging remains case-insensitive through verifiedFiles/disk instead.
        var plexKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plexItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item == null || item.Type is not ("movie" or "episode")) continue;
            var title = item.Type == "episode" ? item.SeriesTitle : item.Title;
            if (string.IsNullOrWhiteSpace(title) || (item.Type == "episode" && (item.Season is null or < 0 || item.Episode is null or < 1))) continue;
            // Plex's episode year is the episode's air year, not necessarily the series identity year.
            var year = item.Type == "movie" ? item.Year : null;
            IReadOnlyList<PlexMediaFile> files = item.Files.Count == 0 ? [new PlexMediaFile("", "")] : item.Files;
            foreach (var media in files)
            {
                var key = item.RatingKey + "\n" + media.Path;
                if (!plexKeys.Add(key)) continue;
                string? localPath = null;
                try { localPath = LibraryPlanner.NormalizePath(media.Path); } catch { }
                // Only the explicit root scan can establish local presence. A Plex response
                // never causes disk access outside configured roots or to a network share.
                var verified = localPath != null && verifiedFiles.Contains(localPath);
                var quality = ParseResolution(media.VideoResolution);
                var observedYear = year;
                if (verified && localPath != null && disk.Remove(localPath, out var found))
                {
                    if (quality == QualityProfile.Any) quality = found.Identity.Quality;
                    if (item.Type == "episode" && SameIdentity(found.Identity,
                            new(title.Trim(), null, item.Season, item.Episode, quality)))
                        observedYear = found.Identity.Year;
                }
                entries.Add(new(new(title.Trim(), observedYear, item.Type == "episode" ? item.Season : null,
                        item.Type == "episode" ? item.Episode : null, quality),
                    localPath ?? media.Path, verified ? "both" : "plex", verified, item.RatingKey, item.Guids.ToArray()));
            }
        }
        entries.AddRange(disk.Values);
        var snapshot = new LibraryIndexSnapshot(DateTime.UtcNow,
            entries.OrderBy(e => e.Identity.Title, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            scannedRoots, verifiedFiles.Count, issues, issues == 0 && scannedRoots > 0);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    public static LibraryOwnershipComparison Lookup(LibraryIndexSnapshot snapshot, LibraryMediaIdentity wanted)
    {
        var matches = snapshot.Entries.Where(entry => SameIdentity(entry.Identity, wanted)).ToArray();
        if (matches.Length == 0) return new(snapshot.Complete ? "not-observed" : "unknown", null, false, false, false, snapshot.Complete, matches);
        // With no requested series year, multiple known remakes are ambiguous rather than interchangeable.
        if (wanted.Year == null && matches.Select(m => m.Identity.Year).Where(y => y.HasValue).Distinct().Count() > 1)
            return new("ambiguous", null, false, false, false, snapshot.Complete, matches);
        var local = matches.Where(m => m.VerifiedOnDisk).ToArray();
        var candidates = local.Length == 0 ? matches : local;
        var best = (QualityProfile)candidates.Max(e => (int)e.Identity.Quality);
        if (local.Length == 0) return new("catalog-only", best, false, false, false, snapshot.Complete, matches);
        if (wanted.Quality != QualityProfile.Any && best == QualityProfile.Any)
            return new("unknown-quality", best, true, false, false, snapshot.Complete, matches);
        var meets = wanted.Quality == QualityProfile.Any || best >= wanted.Quality;
        return new(meets ? "owned" : "upgrade", best, true, meets, !meets, snapshot.Complete, matches);
    }

    public static bool SameIdentity(LibraryMediaIdentity first, LibraryMediaIdentity second)
    {
        var firstTitle = NormalizeTitle(first.Title);
        if (firstTitle.Length == 0 || firstTitle != NormalizeTitle(second.Title) || first.Kind != second.Kind
            || first.Season != second.Season || first.Episode != second.Episode) return false;
        if (second.Year.HasValue) return first.Year == second.Year;
        return true;
    }

    private static string NormalizeTitle(string title) => new(title.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static LibraryMediaIdentity? IdentityForFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var direct = LibraryPlanner.TryIdentity(stem);
        if (direct != null) return direct;
        var parent = Path.GetDirectoryName(path);
        if (parent == null) return null;
        var folder = LibraryPlanner.TryIdentity(Path.GetFileName(parent));
        // A generic filename can borrow movie identity from its immediate release folder;
        // episode numbers are never guessed from an arbitrary ordinal or season folder.
        return folder is { Kind: "movie" } ? folder : null;
    }

    internal static bool ExcludedName(string name) => name.StartsWith('.')
        || name.StartsWith("-MISSING-", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(name).EndsWith("-sample", StringComparison.OrdinalIgnoreCase)
        || new[] { "sample", "proof", "subs", "subtitles", "extras", "featurettes", "trailers" }
            .Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);

    public static QualityProfile ParseResolution(string? resolution) => resolution?.Trim().ToLowerInvariant() switch
    {
        "4k" or "uhd" or "2160" or "2160p" => QualityProfile.Q2160p,
        "1080" or "1080p" or "1080i" => QualityProfile.Q1080p,
        "720" or "720p" => QualityProfile.Q720p,
        "sd" or "480" or "480p" or "576" or "576p" => QualityProfile.SD,
        _ => QualityProfile.Any
    };
}
