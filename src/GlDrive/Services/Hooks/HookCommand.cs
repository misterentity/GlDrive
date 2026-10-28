using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlDrive.Config;
using Microsoft.Win32.SafeHandles;

namespace GlDrive.Services.Hooks;

internal static class HookCommand
{
    internal static void Validate(HookCommandConfig command)
    {
        if (command.Id == null || !System.Text.RegularExpressions.Regex.IsMatch(command.Id, "^[a-zA-Z0-9_-]{1,64}$"))
            throw new ArgumentException("Hook id must be 1-64 letters, digits, underscores or hyphens");
        if (command.Events == null || command.Events.Count == 0 || command.Events.Any(e => !HookEvent.Names.Contains(e)))
            throw new ArgumentException("Choose supported hook events");
        if (command.TimeoutSeconds is < 1 or > 3600 || command.OutputLimitCharacters is < 0 or > 65536)
            throw new ArgumentException("Invalid hook timeout or output limit");
        if (string.IsNullOrWhiteSpace(command.Executable) || !Path.IsPathFullyQualified(command.Executable) || !command.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Hook executable must be an absolute .exe path");
        var name = Path.GetFileNameWithoutExtension(command.Executable);
        if (new[] { "cmd", "sh", "bash", "wscript", "cscript", "mshta", "node", "nodejs", "python", "python3", "pythonw", "py", "perl", "ruby" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Shell command hooks are unsupported");
        var powershell = name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase);
        if (command.ScriptPath == null || powershell != (command.ScriptPath.Length > 0) || command.ScriptPath.Length > 0 &&
            (!Path.IsPathFullyQualified(command.ScriptPath) || !command.ScriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("PowerShell hooks require an absolute .ps1 scriptPath; other executables use arguments");
        if (command.Arguments == null || command.Arguments.Count > 128 || command.Arguments.Any(a => a == null || a.Length > 8192 || a.Contains('\0')))
            throw new ArgumentException("Hook arguments exceed the supported bounds");
    }

    internal static ProcessStartInfo Build(HookCommandConfig command, HookEvent data)
    {
        Validate(command);
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(command.ScriptPath.Length > 0 ? command.ScriptPath : command.Executable)!
        };
        if (command.ScriptPath.Length > 0)
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", command.ScriptPath }) start.ArgumentList.Add(arg);
        foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        // Inherit only process essentials, never arbitrary tokens from the app's environment.
        var essentials = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA" }
            .Select(key => (key, value: Environment.GetEnvironmentVariable(key))).ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in essentials) if (value != null) start.Environment[key] = value;
        start.Environment["GLDRIVE_EVENT"] = data.Event;
        start.Environment["GLDRIVE_RELEASE"] = data.Release;
        start.Environment["GLDRIVE_CATEGORY"] = data.Category;
        start.Environment["GLDRIVE_QUALITY"] = data.Quality;
        start.Environment["GLDRIVE_STATUS"] = data.Status;
        start.Environment["GLDRIVE_LOCAL_PATH"] = data.LocalPath;
        return start;
    }

    internal static bool IsOutsideRoots(string path, IEnumerable<string> roots)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        return !roots.Where(r => !string.IsNullOrWhiteSpace(r)).Any(r =>
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal sealed class PathRefusalException(string reason) : IOException(reason);

    internal static string? UnsafeReparseReason(string path,
        Func<string, (FileAttributes Attributes, uint Tag)>? readTag = null)
    {
        readTag ??= ReadAttributeTag;
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            var (attributes, tag) = readTag(cursor);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            // CLOUD and CLOUD_1..CLOUD_F retain the filesystem identity; all
            // redirecting or unknown tags remain outside executable authority.
            if ((tag & ~0x0000F000u) == 0x9000001Au) continue;
            return $"unsupported reparse tag 0x{tag:X8} (only cloud placeholders are permitted)";
        }
        return null;
    }

    private static (FileAttributes Attributes, uint Tag) ReadAttributeTag(string path)
    {
        // Ordinary ancestors need only an attribute query. Opening a directory
        // handle can require extra permissions even when its child is readable.
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return (attributes, 0);
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new PathRefusalException($"Hook path metadata could not be inspected (Windows error {Marshal.GetLastWin32Error()})");
        return ((FileAttributes)info.Attributes, info.Tag);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out AttributeTagInfo information, uint size);

    internal static async Task<string> CaptureAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var tail = new StringBuilder();
        var buffer = new char[2048];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > limit) tail.Remove(0, tail.Length - limit);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return tail.ToString();
    }
}
