using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace GlDrive.Util;

public static class SecureFile
{
    // Per-path lock object — serializes concurrent writes to the same file.
    // Without this, AgentRunner.Save (ConfigManager.Save), NotificationStore.Save,
    // and RaceHistoryStore.Save raced and threw IOException ("being used by another process")
    // because the .tmp staging file was locked or the rename clashed.
    private static readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    internal static void WithPathLock(string path, Action action)
    {
        lock (_locks.GetOrAdd(Path.GetFullPath(path), _ => new object())) action();
    }

    public static void WriteAllTextRestricted(string path, string content)
        => WriteAllTextRestricted(path, () => content);

    /// <summary>Capture mutable state only after earlier saves to this path finish.</summary>
    public static void WriteAllTextRestricted(string path, Func<string> createContent)
        => WriteRestricted(path, () => System.Text.Encoding.UTF8.GetBytes(createContent()));

    public static void WriteAllBytesRestricted(string path, byte[] content)
        => WriteRestricted(path, () => content);

    private static void WriteRestricted(string path, Func<byte[]> createContent)
    {
        var gate = _locks.GetOrAdd(Path.GetFullPath(path), _ => new object());
        lock (gate)
        {
            var content = createContent();
            // Belt-and-suspenders: even with the per-path lock, another GlDrive process
            // (e.g. a watchdog-spawned restart still draining writes) or AV/OneDrive
            // can hold the file briefly. Retry with short backoff before giving up.
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    WriteAllBytesRestrictedInner(path, content);
                    return;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    var delayMs = 50 * attempt; // 50, 100, 150, 200, 250 ms
                    Log.Debug("SecureFile retry {Attempt}/{Max} on {Path} after {DelayMs}ms: {Msg}",
                        attempt, maxAttempts, path, delayMs, ex.Message);
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
                {
                    var delayMs = 50 * attempt;
                    Log.Debug("SecureFile retry {Attempt}/{Max} (auth) on {Path} after {DelayMs}ms: {Msg}",
                        attempt, maxAttempts, path, delayMs, ex.Message);
                    Thread.Sleep(delayMs);
                }
            }
        }
    }

    private static void WriteAllBytesRestrictedInner(string path, byte[] content)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl, AccessControlType.Allow));

        var tempPath = TempPathFor(path);
        try
        {
            using (var fs = System.IO.FileSystemAclExtensions.Create(
                new System.IO.FileInfo(tempPath),
                FileMode.CreateNew,
                FileSystemRights.WriteData | FileSystemRights.ReadData,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None,
                security))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            RestrictFilePermissions(path);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    internal static string TempPathFor(string path) => path + $".{Guid.NewGuid():N}.tmp";

    // "<name>.<32 lowercase hex>.tmp" — exactly what TempPathFor produces.
    private static readonly System.Text.RegularExpressions.Regex TempName =
        new(@"^.+\.[0-9a-f]{32}\.tmp$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsOrphanCandidate(string fileName) => TempName.IsMatch(fileName);

    /// <summary>
    /// Deletes staging files an interrupted write left behind (crash or kill between create
    /// and move). Top level of <paramref name="directory"/> only, only names matching the
    /// staging pattern, only files older than <paramref name="minAge"/> so a write in flight
    /// is never touched, and at most <paramref name="maxFiles"/> per call.
    /// </summary>
    public static int SweepOrphanedTempFiles(string directory, TimeSpan minAge, int maxFiles = 200, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(directory)) return 0;
        var cutoff = (nowUtc ?? DateTime.UtcNow) - minAge;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            if (deleted >= maxFiles) break;
            if (!IsOrphanCandidate(Path.GetFileName(file))) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("Orphaned temp file {File} not removed: {Msg}", file, ex.Message);
            }
        }
        if (deleted > 0) Log.Information("Removed {Count} orphaned temp file(s) from {Directory}", deleted, directory);
        return deleted;
    }

    public static void RestrictFilePermissions(string path)
    {
        try
        {
            var fileInfo = new System.IO.FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                security.RemoveAccessRule(rule);
            var currentUser = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to restrict file permissions on {File}", path);
        }
    }
}
