using System.IO;
using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

/// <summary>
/// Decides whether a multi-volume archive set has finished arriving.
///
/// Root cause this exists for (observed 2026-08-14): the watch folder gated extraction on
/// <c>WaitForFileReady(path)</c> — but <c>path</c> is only the FIRST volume. SharpCompress
/// opens the whole set through <c>SourceStream.LoadAllParts()</c>, so extraction began the
/// moment <c>name.rar</c> stopped growing, while <c>name.r22</c>, <c>.r21</c>, <c>.r20</c> …
/// were still being written. The readiness invariant is a property of the volume SET; it was
/// being enforced on one member of it.
///
/// Three separate log clusters were the same defect:
///   * <c>IOException "…\.r22 is being used by another process"</c> — the honest symptom,
///     retried five times over ~2.5 min (far less than a 2160p set takes to land) and then
///     abandoned non-durably, so every app restart replayed the whole cycle.
///   * <c>"unpacked file size does not match header: expected 16924333715 found 1994329334"</c>
///     — not corruption, a set that was 2 GB into a 16.9 GB download. Ruled Permanent and
///     recorded DURABLY.
///   * <c>"UnRAR.exe failed (exit 3)"</c> (CRC) — the natural result of reading a half-written
///     volume, likewise ruled Permanent.
///
/// The last two mean the classifier issued "unrecoverable" verdicts about files that were
/// merely incomplete at the time. <see cref="ExtractAbandonStore"/>'s fingerprint let them
/// lapse once the download finished, which is why this never became permanent damage — but
/// the work, the log noise, and the false verdicts were all avoidable.
///
/// A set is ready when nothing about it is still moving: no volume is write-locked, and
/// neither the volume count nor the total byte count changed between two consecutive
/// samples. The count check matters as much as the bytes — sampling while parts 1-5 exist
/// and parts 6-30 have not started yet would otherwise look perfectly settled.
/// </summary>
public static class VolumeSetReadiness
{
    /// <summary>
    /// One observation of a volume set. <paramref name="LockedCount"/> is how many members
    /// could not be opened for exclusive read, i.e. are still being written.
    /// </summary>
    public readonly record struct Snapshot(int Count, long TotalBytes, int LockedCount, int MissingCount = 0);

    /// <summary>
    /// True when <paramref name="current"/> shows a set that has stopped changing and is
    /// fully unlocked, and <paramref name="previous"/> agrees on its shape.
    ///
    /// An empty set is never ready: zero volumes means discovery raced the first write, and
    /// treating that as settled would hand SharpCompress an archive with no parts.
    ///
    /// A set with a known-absent member is never ready either, however still it is. The
    /// motion checks alone could not see this (observed 2026-09-24 19:47): a client that
    /// writes one volume at a time and pauses between files left <c>.rar</c> + <c>.r31–.r39</c>
    /// on disk and <c>.r00–.r30</c> not yet started. Two samples 2s apart agreed, nothing was
    /// locked, and the set was called "settled" at 10 of 41 parts — SharpCompress then threw
    /// "unpacked file size does not match header: expected 2024379567 found 474383163" and the
    /// classifier ruled it unrecoverable. Absence is structural, not temporal, so it is keyed
    /// on the set's own shape (<see cref="CountMissingVolumes"/>), not on a longer wait.
    /// </summary>
    public static bool IsReady(Snapshot previous, Snapshot current)
    {
        if (current.Count <= 0) return false;
        if (current.LockedCount > 0) return false;
        if (current.MissingCount > 0) return false;

        return current.Count == previous.Count
            && current.TotalBytes == previous.TotalBytes;
    }

    /// <summary>
    /// True when the set is still actively arriving — a volume is locked, or the set grew
    /// between samples. Callers use this to distinguish "not finished yet" (keep waiting,
    /// the input is fine) from "waited the full budget and it never settled" (a genuine
    /// stall worth a retry slot). Keeping those apart is what stops a slow download from
    /// burning the bounded retry budget that exists for real faults.
    /// </summary>
    public static bool IsStillArriving(Snapshot previous, Snapshot current)
    {
        if (current.LockedCount > 0) return true;

        return current.Count != previous.Count
            || current.TotalBytes != previous.TotalBytes;
    }

    private static readonly Regex VolumeSuffix =
        new(@"^\.(?<kind>[rs])(?<num>[0-9]{2,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModernBase =
        new(@"^(?<base>.+)\.part[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PartSuffix =
        new(@"^\.part(?<num>[0-9]+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SfvEntry =
        new(@"^(?<name>.+?)[ \t]+[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// How many members of an old-style or modern <c>base.partNN.rar</c> set
    /// are provably absent. Two independent proofs, counted once per distinct name:
    ///   * a gap in the volume numbering — a present <c>.r31</c> proves <c>.r00–.r30</c> exist;
    ///   * a set member declared by an SFV in the same folder that is not on disk — the only
    ///     proof available for missing TAIL volumes, which leave no gap behind them.
    /// SFV entries that are not members of this set (samples, nfo, other releases) are ignored.
    /// </summary>
    public static int CountMissingVolumes(
        string baseName, IEnumerable<string> presentFileNames, IEnumerable<string>? sfvDeclaredNames)
    {
        var present = new HashSet<string>(presentFileNames, StringComparer.OrdinalIgnoreCase);
        var modern = ModernBase.Match(baseName);
        var setBase = modern.Success ? modern.Groups["base"].Value : baseName;
        var members = present.Select(n => ParseMember(setBase, n, modern.Success))
            .Where(m => m != null).Select(m => m!.Value).ToList();
        long missing = 0;
        var ranges = new Dictionary<char, (int First, int Last, HashSet<int> Have)>();

        foreach (var kind in modern.Success ? new[] { 'p' } : new[] { 'r', 's' })
        {
            var have = members.Where(m => m.Kind == kind).Select(m => m.Number).ToHashSet();
            var first = kind == 'p' ? 1 : 0;
            var highest = have.DefaultIfEmpty(first - 1).Max();
            // Classic numbering crosses from .r99 to .s00; .s00 proves the entire
            // preceding r range, even if none of those files has arrived yet.
            if (kind == 'r' && members.Any(m => m.Kind == 's')) highest = Math.Max(99, highest);
            ranges[kind] = (first, highest, have);
            // Arithmetic avoids walking an arbitrarily large .partNN suffix.
            missing += highest - first + 1 - have.Count;
        }

        if (sfvDeclaredNames != null)
        {
            foreach (var declared in sfvDeclaredNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (present.Contains(declared)) continue;
                var member = ParseMember(setBase, declared, modern.Success);
                if (member is { } m)
                {
                    var range = ranges[m.Kind];
                    if (m.Number <= range.Last && !range.Have.Contains(m.Number)) continue;
                    missing++;
                }
                else if (!modern.Success && declared.Equals($"{baseName}.rar", StringComparison.OrdinalIgnoreCase))
                    missing++;
            }
        }

        return (int)Math.Min(int.MaxValue, missing);
    }

    private static (char Kind, int Number)? ParseMember(string baseName, string fileName, bool modern)
    {
        if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return null;
        var m = (modern ? PartSuffix : VolumeSuffix).Match(fileName[baseName.Length..]);
        if (!m.Success || !int.TryParse(m.Groups["num"].Value, out var number)
            || (modern && number < 1)) return null;
        return (modern ? 'p' : char.ToLowerInvariant(m.Groups["kind"].Value[0]), number);
    }

    /// <summary>
    /// Observe both naming schemes without changing the extractor's archive-opening policy.
    /// Discovery failures propagate so the sampler cannot mistake an unreadable set for one file.
    /// </summary>
    internal static List<FileInfo> DiscoverVolumes(string firstVolumePath)
    {
        var first = new FileInfo(firstVolumePath);
        if (!first.Extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)) return [first];

        var baseName = Path.GetFileNameWithoutExtension(first.Name);
        var modern = ModernBase.Match(baseName);
        var setBase = modern.Success ? modern.Groups["base"].Value : baseName;
        var result = new List<FileInfo> { first };
        foreach (var candidate in first.Directory!.EnumerateFiles())
        {
            if (!candidate.Name.Equals(first.Name, StringComparison.OrdinalIgnoreCase)
                && ParseMember(setBase, candidate.Name, modern.Success) != null)
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// File names declared by every <c>*.sfv</c> in <paramref name="directory"/>. Unreadable
    /// SFVs contribute nothing: this is an extra proof of absence, never a reason to proceed.
    /// </summary>
    public static List<string> ReadSfvDeclaredNames(string directory)
    {
        var names = new List<string>();
        try
        {
            foreach (var sfv in Directory.EnumerateFiles(directory, "*.sfv"))
            {
                try
                {
                    foreach (var line in File.ReadLines(sfv))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;
                        var entry = SfvEntry.Match(trimmed);
                        if (entry.Success) names.Add(entry.Groups["name"].Value);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return names;
    }
}
