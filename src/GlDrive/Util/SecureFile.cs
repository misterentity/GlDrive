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

    public static void WriteAllTextRestricted(string path, string content)
    {
        var gate = _locks.GetOrAdd(Path.GetFullPath(path), _ => new object());
        lock (gate)
        {
            // Belt-and-suspenders: even with the per-path lock, another GlDrive process
            // (e.g. a watchdog-spawned restart still draining writes) or AV/OneDrive
            // can hold the file briefly. Retry with short backoff before giving up.
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    WriteAllTextRestrictedInner(path, content);
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

    private static void WriteAllTextRestrictedInner(string path, string content)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl, AccessControlType.Allow));

        var tempPath = path + ".tmp";
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { }
        }

        using (var fs = System.IO.FileSystemAclExtensions.Create(
            new System.IO.FileInfo(tempPath),
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.ReadData,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security))
        using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(content);
        }

        File.Move(tempPath, path, overwrite: true);
        RestrictFilePermissions(path);
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
