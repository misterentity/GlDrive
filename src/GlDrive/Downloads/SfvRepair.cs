using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GlDrive.Ftp;
using GlDrive.Library;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>Opt-in repair authority is limited to files created in this download's own staging root.</summary>
internal static class SfvRepair
{
    private const string Marker = ".gldrive-sfv-owner";
    private const string Quarantine = ".gldrive-sfv-quarantine";
    internal sealed record Decision(bool Queued, string Reason);

    internal static void RecordOwnership(DownloadItem item, DownloadTreePlan plan, Func<bool> persist)
    {
        if (!plan.IsDirectory || item.SourceKind != DownloadSourceKind.Ftp) return;
        try
        {
            var root = LibraryPlanner.NormalizeRoot(item.LocalPath);
            LibraryPlanner.RejectReparseAncestors(root);
            if (item.SfvRepairRoot == null && !Directory.Exists(root) && !File.Exists(root))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                // CreateDirectoryW reports whether this invocation actually created the root.
                // An already existing folder, including a creation race, is never adopted.
                if (!CreateDirectoryW(root, IntPtr.Zero)) return;
                using var directories = new SafeLibraryImporter.DirectoryLocks();
                directories.Acquire(root, root, create: false);
                var owner = Guid.NewGuid().ToString("N");
                using (var marker = new FileStream(Path.Combine(root, Marker), FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                {
                    marker.Write(Encoding.UTF8.GetBytes(owner));
                    marker.Flush(flushToDisk: true);
                }
                item.SfvRepairRoot = root;
                item.SfvRepairOwner = owner;
            }
            ValidateRoot(item);
            item.SfvRepairFiles ??= [];
            var added = new List<SfvRepairFile>();
            foreach (var file in plan.Files.Where(f => !f.RemotePath.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)))
            {
                var path = ValidatePath(item, file.LocalPath);
                if (Find(item, path) != null || File.Exists(path) || Directory.Exists(path) ||
                    File.Exists(path + ".gdpart") || File.Exists(path + ".gdpart.json")) continue;
                var record = new SfvRepairFile { LocalPath = path };
                item.SfvRepairFiles.Add(record);
                added.Add(record);
            }
            if (!persist())
            {
                foreach (var record in added) item.SfvRepairFiles.Remove(record);
                Log.Warning("SFV repair ownership could not be persisted; ordinary download continues without new repair authority");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "SFV repair staging ownership unavailable; ordinary download continues");
        }
    }

    internal static bool HasOwnedMismatch(DownloadItem item, DownloadTreePlan plan, IEnumerable<SfvIssue> issues)
    {
        try
        {
            ValidateRoot(item);
            return issues.Any(issue => issue.Kind == SfvIssueKind.ChecksumMismatch && issue.LocalPath != null &&
                Find(item, ValidatePlannedPath(item, plan, issue.LocalPath)) != null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    internal static Decision Prepare(DownloadItem item, DownloadTreePlan plan, IReadOnlyList<SfvIssue> errors,
        int maximumAttempts, Func<bool> persist)
    {
        if (errors.Count == 0 || errors.Any(e => e.Kind != SfvIssueKind.ChecksumMismatch))
            return new(false, "SFV manifest or read errors cannot be repaired by re-fetching");
        try
        {
            ValidateRoot(item);
            var candidates = new List<(SfvRepairFile Record, SfvIssue Issue, FileInfo File)>();
            foreach (var group in errors.GroupBy(e => e.LocalPath, StringComparer.OrdinalIgnoreCase))
            {
                var issue = group.First();
                if (issue.LocalPath == null || issue.ActualCrc == null || issue.ExpectedCrc == null ||
                    group.Any(e => e.ExpectedCrc != issue.ExpectedCrc || e.ActualCrc != issue.ActualCrc))
                    return new(false, "SFV repair needs an unambiguous checksum and local path");
                var path = ValidatePlannedPath(item, plan, issue.LocalPath);
                var record = Find(item, path);
                if (record == null) return new(false, "CRC-failing file is not owned by this download's staging");
                if (record.Attempts >= Math.Clamp(maximumAttempts, 1, 2))
                    return new(false, "SFV repair attempt budget exhausted; source copy may be corrupt");
                if (record.Attempts > 0 && record.LastBadCrc == issue.ActualCrc)
                    return new(false, "Source copy corrupt: the re-fetched file has the same wrong CRC");
                var info = new FileInfo(path);
                if (!info.Exists) return new(false, "CRC-failing file changed before repair");
                candidates.Add((record, issue, info));
            }

            // Spend each budget before the first rename. This is a destructive repair
            // gate only: a failed queue write never gates ordinary downloads or logins.
            foreach (var (record, issue, file) in candidates)
            {
                if (record.QuarantinePath != null)
                    (record.PriorQuarantines ??= []).Add(new(record.QuarantinePath, record.QuarantineLength, record.QuarantineLastWriteUtc));
                record.Attempts++;
                record.LastBadCrc = issue.ActualCrc;
                record.Pending = true;
                record.QuarantinePath = QuarantinePath(item, record, record.Attempts);
                record.QuarantineLength = file.Length;
                record.QuarantineLastWriteUtc = file.LastWriteTimeUtc;
            }
            // Earlier repaired files that now pass need no further payload offer.
            // Missing files retain their pending state and the existing SFV wait verdict.
            foreach (var record in item.SfvRepairFiles ?? [])
                if (record.Pending && !candidates.Any(c => ReferenceEquals(c.Record, record)) && File.Exists(record.LocalPath))
                    record.Pending = false;
            if (!persist()) return new(false, "SFV repair budget could not be persisted; files retained");
            ResumePending(item, plan, persist);
            item.DownloadedBytes = Math.Max(0, item.TotalBytes - candidates.Sum(c => c.File.Length));
            return new(true, $"SFV repair queued for {candidates.Count} CRC-failing file(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Warning(ex, "SFV repair refused; staged files retained");
            return new(false, "SFV repair could not safely quarantine the CRC-failing file; retained for review");
        }
    }

    /// <summary>Recover a durable repair intent after a crash, without spending its budget again.</summary>
    internal static void ResumePending(DownloadItem item, DownloadTreePlan plan, Func<bool> persist)
    {
        var pending = (item.SfvRepairFiles ?? []).Where(f => f.Pending).ToArray();
        if (pending.Length == 0) return;
        var root = ValidateRoot(item);
        using var directories = new SafeLibraryImporter.DirectoryLocks();
        foreach (var record in pending)
        {
            var path = ValidatePlannedPath(item, plan, record.LocalPath);
            var quarantine = QuarantinePath(item, record, record.Attempts);
            if (!SamePath(record.QuarantinePath, quarantine)) throw new IOException("Invalid SFV repair intent.");
            LibraryPlanner.RejectReparseAncestors(quarantine);
            // Existing final quarantine is already durable recovery evidence. A
            // current write failure must not prevent verifying its replacement.
            if (File.Exists(quarantine)) continue;
            // An intent retained after a failed save cannot authorize a new move.
            if (!persist()) throw new IOException("SFV repair budget could not be persisted; files retained.");
            directories.Acquire(root, Path.GetDirectoryName(path)!, create: false);
            directories.Acquire(root, Path.GetDirectoryName(quarantine)!, create: true);
            ValidateRoot(item);
            // A quarantine proves the original move happened. If a final file now
            // exists it is a replacement to verify, not another file to discard.
            if (File.Exists(quarantine)) continue;
            // This file had a complete final name when CRC failed. Any older part
            // must not become the replacement, but preserve it rather than delete it.
            foreach (var suffix in new[] { ".gdpart", ".gdpart.json" })
            {
                var part = path + suffix;
                if (!File.Exists(part)) continue;
                LibraryPlanner.RejectReparseAncestors(part);
                var info = new FileInfo(part);
                var savedPart = quarantine + suffix;
                if (File.Exists(savedPart)) throw new IOException("SFV repair partial quarantine already exists.");
                SafeLibraryImporter.RenameStagedFile(part, savedPart, info.Length, info.LastWriteTimeUtc);
                // These sidecars are retained for review; they are not payload and
                // never authorize another download or automatic deletion.
            }
            // Final quarantine is the commit marker: it is published only after
            // stale parts have moved. After a crash, its presence proves that new
            // .gdpart files belong to the replacement and must remain resumable.
            if (File.Exists(path))
                SafeLibraryImporter.RenameStagedFile(path, quarantine, record.QuarantineLength, record.QuarantineLastWriteUtc);
        }
    }

    internal static IReadOnlyList<FtpDownloadFile> SelectPayload(DownloadItem item, IReadOnlyList<FtpDownloadFile> payload)
    {
        var pending = (item.SfvRepairFiles ?? []).Where(f => f.Pending).ToArray();
        // A repair does not freeze the source inventory. Other files can arrive
        // upstream or disappear locally while the repaired file awaits verification.
        return pending.Length == 0 ? payload : payload.Where(file =>
            pending.Any(p => SamePath(p.LocalPath, file.LocalPath)) || !IsLocallyComplete(file)).ToArray();
    }

    internal static bool IsLocallyComplete(FtpDownloadFile file)
    {
        try
        {
            return file.Size >= 0 && !File.Exists(file.LocalPath + ".gdpart") &&
                !File.Exists(file.LocalPath + ".gdpart.json") &&
                new FileInfo(file.LocalPath) is { Exists: true } info && info.Length == file.Size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    internal static void Verified(DownloadItem item, Func<bool> persist)
    {
        if (!item.SfvIntegrityHold && !(item.SfvRepairFiles ?? []).Any(f => f.Pending) && item.SfvRepairRoot == null) return;
        item.SfvIntegrityHold = false;
        foreach (var record in item.SfvRepairFiles ?? []) record.Pending = false;
        // Keep the recovery evidence if clearing the intent is not yet durable.
        // Completion still succeeds in memory; a restart will re-verify its final.
        if (!persist()) return;
        // Cleanup is optional; an inability to remove quarantine never changes the
        // verifier's success or prevents extraction/completion.
        try
        {
            var root = ValidateRoot(item);
            using var directories = new SafeLibraryImporter.DirectoryLocks();
            directories.Acquire(root, root, create: false);
            ValidateRoot(item);
            var quarantine = Path.Combine(root, Quarantine);
            if (Directory.Exists(quarantine))
            {
                using (var quarantineLock = new SafeLibraryImporter.DirectoryLocks())
                {
                    quarantineLock.Acquire(root, quarantine, create: false);
                    foreach (var record in item.SfvRepairFiles ?? [])
                    {
                        var artifacts = (record.PriorQuarantines ?? []).ToList();
                        if (record.QuarantinePath != null)
                            artifacts.Add(new(record.QuarantinePath, record.QuarantineLength, record.QuarantineLastWriteUtc));
                        foreach (var artifact in artifacts)
                        {
                            if (!Enumerable.Range(1, Math.Clamp(record.Attempts, 0, 2)).Any(attempt =>
                                    SamePath(artifact.Path, QuarantinePath(item, record, attempt)))) continue;
                            if (!File.Exists(artifact.Path)) continue;
                            try { SafeLibraryImporter.DeleteStagedFile(artifact.Path, artifact.Length, artifact.LastWriteUtc); }
                            catch (IOException ex) { Log.Debug(ex, "Verified SFV repair retained a changed or busy quarantine file"); }
                        }
                    }
                }
                // Only the empty opened directory can be removed. Untracked stale
                // parts and changed/busy artifacts remain available for review.
                try { SafeLibraryImporter.DeleteEmptyStagedDirectory(quarantine); }
                catch (IOException ex) { Log.Debug(ex, "Verified SFV repair retained its nonempty or busy quarantine directory"); }
            }
            ValidateRoot(item);
            var marker = new FileInfo(Path.Combine(root, Marker));
            SafeLibraryImporter.DeleteStagedFile(marker.FullName, marker.Length, marker.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Log.Debug(ex, "Verified SFV repair retained its quarantine files"); }
    }

    private static string ValidateRoot(DownloadItem item)
    {
        var root = LibraryPlanner.NormalizeRoot(item.LocalPath);
        if (!SamePath(root, item.SfvRepairRoot) || !Guid.TryParseExact(item.SfvRepairOwner, "N", out _))
            throw new IOException("This download has no persisted staging ownership.");
        LibraryPlanner.RejectReparseAncestors(root);
        var marker = Path.Combine(root, Marker);
        LibraryPlanner.RejectReparseAncestors(marker);
        var info = new FileInfo(marker);
        if (!info.Exists || info.Length != 32 || File.ReadAllText(marker) != item.SfvRepairOwner)
            throw new IOException("The staging ownership marker no longer matches this download.");
        return root;
    }

    private static string ValidatePath(DownloadItem item, string path)
    {
        var full = LibraryPlanner.NormalizePath(path);
        if (!LibraryPlanner.IsWithin(full, LibraryPlanner.NormalizeRoot(item.LocalPath)))
            throw new IOException("SFV repair file escapes its staging root.");
        LibraryPlanner.RejectReparseAncestors(full);
        return full;
    }

    private static string ValidatePlannedPath(DownloadItem item, DownloadTreePlan plan, string path)
    {
        var full = ValidatePath(item, path);
        if (!plan.IsDirectory || !plan.Files.Any(f => SamePath(f.LocalPath, full) &&
                !f.RemotePath.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("SFV repair file is absent from the exact source plan.");
        return full;
    }

    private static SfvRepairFile? Find(DownloadItem item, string path) =>
        (item.SfvRepairFiles ?? []).FirstOrDefault(f => SamePath(f.LocalPath, path));

    internal static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static string QuarantinePath(DownloadItem item, SfvRepairFile record, int attempt)
    {
        var path = ValidatePath(item, record.LocalPath);
        if (attempt is < 1 or > 2) throw new IOException("Invalid SFV repair attempt.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return Path.Combine(item.SfvRepairRoot!, Quarantine, $"{item.SfvRepairOwner}-{hash}-{attempt}.crcbad");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
