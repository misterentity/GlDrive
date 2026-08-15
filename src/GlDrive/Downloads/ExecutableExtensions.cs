namespace GlDrive.Downloads;

/// <summary>
/// The one definition of "Windows would run this from Explorer", shared by every path that
/// writes untrusted content to disk.
///
/// It lives in its own type because it has TWO consumers that must never drift apart:
///   * <see cref="GlDrive.Player.TorrentContentPolicy"/> — screens torrent contents.
///   * <see cref="ArchiveExtractor"/> and ExtractorWindow — unpack archives from watch folders.
///
/// Keeping the list in the torrent policy alone would have left an end-to-end bypass, and it
/// is not hypothetical on this machine: the extractor's watch folders include E:\movies and
/// D:\x265, which are exactly where torrent downloads are saved. A torrent carrying
/// Movie.2024.rar with Sample/setup.exe inside would pass the torrent gate (archives are
/// allowed, and must be — it is how scene releases ship), then get auto-extracted with no
/// filtering at all. Same invariant, two call sites: put it on the resource, not the caller.
///
/// Membership rule: opening it from Explorer transfers control to code named by the file,
/// with at most one confirmation dialog — and it does not plausibly appear in a real release.
/// Deliberate exclusions, each with a reason, are documented on TorrentContentPolicy.
/// </summary>
public static class ExecutableExtensions
{
    public static readonly IReadOnlySet<string> Default =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // PATHEXT — what cmd.exe runs by bare name.
            ".com", ".exe", ".bat", ".cmd", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".msc", ".cpl",

            // Script hosts and legacy executable forms. .scr is the classic media-adjacent
            // disguise: a PE binary Explorer runs on double-click.
            ".ps1", ".psc1", ".hta", ".scr", ".pif",

            // Installers.
            ".msi", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle",

            // Shell metadata that runs something else. .settingcontent-ms was neutralised by
            // CVE-2018-8414 on current Windows and is kept only for downlevel/managed boxes;
            // saying so beats including it silently.
            ".lnk", ".url", ".scf", ".library-ms", ".settingcontent-ms",

            // Office add-ins load as code, unlike documents.
            ".xll", ".wll", ".xlam",
        };

    /// <summary>
    /// True when this name's effective extension is one Windows would execute.
    ///
    /// Trailing dots and spaces are trimmed first because Win32 drops them when resolving a
    /// path — "evil.exe." is created on disk as "evil.exe", and Path.GetExtension returns ""
    /// for it. Comparison is invariant-lowercase so a Turkish locale cannot quietly stop
    /// ".PIF" and ".MSI" from matching.
    /// </summary>
    public static bool IsExecutable(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var cut = fileName.LastIndexOfAny(['/', '\\']);
        var leaf = cut >= 0 ? fileName[(cut + 1)..] : fileName;

        var trimmed = leaf.TrimEnd('.', ' ');
        if (trimmed.Length == 0) return false;

        var dot = trimmed.LastIndexOf('.');
        if (dot < 0) return false;

        return Default.Contains(trimmed[dot..].ToLowerInvariant());
    }
}
