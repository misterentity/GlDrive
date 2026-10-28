using System.IO;

namespace GlDrive.Util;

/// <summary>App-owned state root, independent of config serialization for watchdog startup.</summary>
internal static class AppDataPaths
{
    internal static string? TestOverride { get; set; }
    internal static string Root => TestOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");
}
