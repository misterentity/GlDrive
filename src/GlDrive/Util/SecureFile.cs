using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace GlDrive.Util;

public static class SecureFile
{
    public static void WriteAllTextRestricted(string path, string content)
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

        using var fs = System.IO.FileSystemAclExtensions.Create(
            new System.IO.FileInfo(tempPath),
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.ReadData,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security);
        using var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);

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
