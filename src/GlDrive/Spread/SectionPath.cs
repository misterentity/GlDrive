namespace GlDrive.Spread;

/// <summary>
/// A configured section path may declare a dated tree with the <c>{MMDD}</c> token,
/// e.g. <c>/FLAC/{MMDD}</c>. Whether a site dates a section is a property of that
/// site, not of the section's name: sitea dates /FLAC while siteb keeps
/// /incoming/mp3 flat, so the legacy "leaf is mp3/0day" guess cannot express it.
/// </summary>
internal static class SectionPath
{
    public const string DateToken = "{MMDD}";

    public static bool IsDated(string? path) =>
        path?.Contains(DateToken, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Substitute today's (UTC) date folder for the token; other paths pass through.</summary>
    public static string Expand(string path, DateTime utcNow) =>
        IsDated(path) ? path.Replace(DateToken, utcNow.ToString("MMdd"), StringComparison.OrdinalIgnoreCase) : path;

    public static string Expand(string path) => Expand(path, DateTime.UtcNow);
}
