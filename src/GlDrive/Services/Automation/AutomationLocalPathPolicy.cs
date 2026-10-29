using System.IO;
using System.Text.Json;

namespace GlDrive.Services.Automation;

internal static class AutomationLocalPathPolicy
{
    internal static void Enforce(string tool, JsonElement args, IReadOnlyList<string> roots)
    {
        // Saved extractor state can contain paths not present in this request. Its local UI
        // remains available; a scoped token cannot turn an uninspected saved queue into disk access.
        if (tool is "extractor.start" or "extractor.settings") throw new AutomationDeniedException("Scoped clients cannot operate extraction from saved paths; use the local extractor window.");
        foreach (var path in Paths(tool, args))
            if (!roots.Any(root => WithinRoot(root, path)))
                throw new AutomationDeniedException("A local path is outside the client's allowed roots or crosses a link.");
    }

    internal static bool WithinRoot(string root, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)) return false;
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var canonicalPath = Path.GetFullPath(path);
            if (!Directory.Exists(canonicalRoot) || !Below(canonicalRoot, canonicalPath)) return false;
            // Check every existing ancestor, including above the selected root: a root itself
            // nested beneath a junction must not authorize the junction's arbitrary target.
            var candidate = canonicalPath;
            while (!string.IsNullOrEmpty(candidate))
            {
                try { if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                candidate = Path.GetDirectoryName(candidate);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool Below(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Paths(string tool, JsonElement args)
    {
        string? property = tool switch
        {
            "ftp.upload" or "ftp.download" or "downloads.enqueue" => "localPath",
            "player.play_local" => "path", "player.download_torrent" => "directory",
            "cleanup.scan" or "cleanup.delete" => "root", "library.plan" or "library.import" => "sourcePath", _ => null
        };
        if (property != null && Text(args, property) is { } path) yield return path;
        var array = tool == "cleanup.delete" ? "files" : tool == "extractor.add" ? "paths" : null;
        if (array != null && args.TryGetProperty(array, out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var item in values.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) yield return item.GetString()!;
        if (tool == "extractor.settings" && args.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in settings.EnumerateObject())
            {
                if (field.Name.Equals("customOutputPath", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(field.Value.GetString())) yield return field.Value.GetString()!;
                if (field.Name.Equals("watchFolders", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.Array)
                    foreach (var folder in field.Value.EnumerateArray()) if (folder.ValueKind == JsonValueKind.String) yield return folder.GetString()!;
            }
        }
    }
    private static string? Text(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
