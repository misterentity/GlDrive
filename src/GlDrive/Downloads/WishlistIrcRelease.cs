using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Downloads;

/// <summary>Resolves an already received announce without making any FTP requests.</summary>
internal static class WishlistIrcRelease
{
    internal static string? ResolvePath(ServerConfig server, string section, string release)
    {
        if (!SafeSegment(section) || !SafeSegment(release)) return null;
        var mapped = SectionMapper.Resolve(server.SpreadSite, section, release);
        string? folder = null;
        if (mapped is { } mapping)
        {
            folder = !string.IsNullOrWhiteSpace(mapping.Mapping?.Path)
                ? mapping.Mapping.Path : FindSection(server, mapping.RemoteSection);
            // A broken explicit mapping is not permission to guess another folder.
            if (string.IsNullOrWhiteSpace(folder)) return null;
        }
        folder ??= FindSection(server, section);
        folder ??= server.Notifications.WatchPath.TrimEnd('/') + "/" + section;
        if (!folder.StartsWith('/') || folder.Any(char.IsControl)) return null;
        return folder.TrimEnd('/') + "/" + release;
    }

    private static string? FindSection(ServerConfig server, string section) =>
        server.SpreadSite.Sections.FirstOrDefault(pair => pair.Key.Equals(section, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool SafeSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && value is not ("." or "..") && !value.StartsWith('.')
        && !value.Any(c => c is '/' or '\\' || char.IsControl(c));
}
