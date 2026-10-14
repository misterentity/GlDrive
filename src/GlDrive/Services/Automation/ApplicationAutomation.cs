using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using GlDrive.UI;

namespace GlDrive.Services.Automation;

public sealed class ApplicationAutomation(Func<UpdateChecker> updater) : IAutomationModule
{
    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("app.status", "Read application version, process, uptime and MCP capabilities.", Schema(new { }), true),
        AutomationToolDefinition.Create("app.restart", "Restart GlDrive to apply saved configuration. Stops ongoing app activity and invalidates current automation jobs. Reconnect after the app starts.", Schema(new { }), destructive: true),
        AutomationToolDefinition.Create("app.update_check", "Check the app's configured GitHub release feed for an update.", Schema(new { }), true),
        AutomationToolDefinition.Create("app.update_install", "Download and install the newest official release through the existing signature-verified updater. May require Windows approval and closes the current app.", Schema(new { }), destructive: true),
        AutomationToolDefinition.Create("cleanup.scan", "Find leftover archive files beside extracted media beneath an absolute local directory. Scans at most five directory levels; skips junctions. Presence of media is a heuristic, not verification that extraction succeeded.", Schema(new { root = new { type = "string", maxLength = 4096 } }, "root"), true),
        AutomationToolDefinition.Create("cleanup.delete", "Delete explicitly selected leftover archive files returned by cleanup.scan. Revalidates candidates and scope before deleting; never deletes media or directories.", Schema(new { root = new { type = "string", maxLength = 4096 }, files = new { type = "array", minItems = 1, maxItems = 1000, items = new { type = "string", maxLength = 4096 } } }, "root", "files"), destructive: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation == "app.status") return new
        {
            version = UpdateChecker.CurrentVersion.ToString(), pid = Environment.ProcessId,
            startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            transport = "stdio bridge to authenticated loopback", jobsPersistAcrossRestart = false
        };
        if (operation == "app.restart")
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("--restart-after"); start.ArgumentList.Add(Environment.ProcessId.ToString());
                    Process.Start(start);
                    App.MarkExiting("MCP restart");
                    Application.Current.Shutdown();
                });
            });
            return new { restarting = true };
        }
        if (operation is "app.update_check" or "app.update_install")
        {
            var checker = updater();
            var release = await checker.CheckForUpdateAsync();
            if (operation == "app.update_check") return new { currentVersion = UpdateChecker.CurrentVersion.ToString(), available = release,
                message = release == null ? "No newer release returned. This also occurs when the update check fails; inspect diagnostics for details." : "Newer release found" };
            if (release == null) return new { installed = false, message = "No newer release returned; the check may have failed. Inspect diagnostics." };
            await checker.DownloadAndInstallAsync(release);
            return new { attempted = true, installationConfirmed = false, release = release.TagName,
                message = "Updater returned. It may have refused or deferred installation; inspect diagnostics and verify app.status after restart." };
        }
        if (operation.StartsWith("cleanup.", StringComparison.Ordinal))
        {
            var root = arguments.GetProperty("root").GetString()!;
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new ArgumentException("root must be an existing absolute directory");
            root = Path.GetFullPath(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("root cannot be a junction or symbolic link");
            var candidates = await Task.Run(() => CleanupWindow.ScanForLeftovers(root), ct);
            if (operation == "cleanup.scan") return new { folders = candidates.Take(1000).ToArray(), total = candidates.Count, truncated = candidates.Count > 1000 };
            if (operation != "cleanup.delete") throw new KeyNotFoundException("Unknown cleanup action");
            var eligible = candidates.SelectMany(c => c.ArchiveFiles).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = arguments.GetProperty("files").EnumerateArray().Select(p => Path.GetFullPath(p.GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var file in files)
                if (!eligible.Contains(file) || !IsContainedRegularFile(root, file)) throw new ArgumentException("A selected file is no longer an eligible archive beneath root; scan again");
            var removed = new List<string>();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                // Recheck links immediately before the individual delete.
                if (!IsContainedRegularFile(root, file)) throw new IOException("Cleanup path changed during operation");
                File.Delete(file); removed.Add(file);
            }
            return new { deleted = removed };
        }
        throw new KeyNotFoundException("Unknown application operation");
    }

    internal static bool IsContainedRegularFile(string root, string file)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullFile = Path.GetFullPath(file);
        if (!fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(fullFile)) return false;
        for (var path = fullFile; !string.Equals(path, fullRoot, StringComparison.OrdinalIgnoreCase); path = Path.GetDirectoryName(path)!)
            if (path == null || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
