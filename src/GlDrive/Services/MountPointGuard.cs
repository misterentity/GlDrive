using System.IO;

namespace GlDrive.Services;

internal static class MountPointGuard
{
    internal static void EnsureAvailable(string driveLetter, IEnumerable<string> logicalDrives)
    {
        if (driveLetter.Length != 1 || !char.IsAsciiLetter(driveLetter[0]))
            throw new ArgumentException("A mount drive must be a single letter from A to Z", nameof(driveLetter));

        var root = char.ToUpperInvariant(driveLetter[0]) + ":\\";
        // The logical-drive list also covers unavailable/removable drives. Probing
        // Directory.Exists would misclassify those letters and can block on SMB.
        if (logicalDrives.Any(d => string.Equals(d.TrimEnd('\\', '/'), root.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Drive {root} is already in use. Unmount it or choose another drive letter in Settings.");
    }
}
