using System.IO;

namespace GlDrive.Tests;

/// <summary>
/// Source of the archive extraction engine for the structural suites. Those suites pinned
/// their invariants on <c>UI/ExtractorWindow.xaml.cs</c> until the engine moved out of the
/// window into <c>Downloads/ArchiveExtraction/</c>; a method may now live in any file of
/// that folder, so they read all of it.
/// </summary>
internal static class ExtractorEngineSource
{
    internal static string Folder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "src", "GlDrive", "Downloads", "ArchiveExtraction");
            if (Directory.Exists(folder)) return folder;
        }

        throw new DirectoryNotFoundException(
            "src/GlDrive/Downloads/ArchiveExtraction not found walking up from " + AppContext.BaseDirectory);
    }

    /// <summary>Every engine file, concatenated in name order, comments NOT stripped.</summary>
    internal static string ReadAll() =>
        string.Join("\n", Directory.GetFiles(Folder(), "*.cs")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));

    internal static string Read(string fileName) => File.ReadAllText(Path.Combine(Folder(), fileName));
}
