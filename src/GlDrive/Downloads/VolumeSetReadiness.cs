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
        new(@"^\.(?<kind>[rs])(?<num>\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// How many members of an old-style set (<c>base.rar</c>, <c>base.r00…</c>, <c>base.s00…</c>)
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
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in new[] { 'r', 's' })
        {
            var numbers = present
                .Select(n => ParseMember(baseName, n))
                .Where(m => m is { } v && v.Kind == kind)
                .Select(m => m!.Value)
                .ToList();
            if (numbers.Count == 0) continue;

            var width = numbers.Max(m => m.Width);
            var highest = numbers.Max(m => m.Number);
            var have = numbers.Select(m => m.Number).ToHashSet();
            for (var i = 0; i < highest; i++)
                if (!have.Contains(i))
                    missing.Add($"{baseName}.{kind}{i.ToString().PadLeft(width, '0')}");
        }

        if (sfvDeclaredNames != null)
        {
            foreach (var declared in sfvDeclaredNames)
            {
                var isMember = declared.Equals($"{baseName}.rar", StringComparison.OrdinalIgnoreCase)
                    || ParseMember(baseName, declared) != null;
                if (isMember && !present.Contains(declared)) missing.Add(declared);
            }
        }

        return missing.Count;
    }

    private static (char Kind, int Number, int Width)? ParseMember(string baseName, string fileName)
    {
        if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return null;
        var m = VolumeSuffix.Match(fileName[baseName.Length..]);
        if (!m.Success) return null;
        var digits = m.Groups["num"].Value;
        return (char.ToLowerInvariant(m.Groups["kind"].Value[0]), int.Parse(digits), digits.Length);
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
                        var lastSpace = trimmed.LastIndexOf(' ');
                        if (lastSpace <= 0) continue;
                        names.Add(trimmed[..lastSpace].Trim());
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
