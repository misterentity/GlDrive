using System.IO;

namespace GlDrive.Util;

public static class DialogPaths
{
    /// <summary>
    /// First candidate that resolves to an existing directory, walking up to the nearest
    /// existing ancestor. Common item dialogs throw Win32Exception from ShowDialog when
    /// InitialDirectory is on an unmounted drive or doesn't exist, so never hand them one.
    /// </summary>
    public static string? ExistingDirectoryOrNull(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string? dir;
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) continue;
                dir = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            while (dir != null && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            if (dir != null) return dir;
        }
        return null;
    }
}
