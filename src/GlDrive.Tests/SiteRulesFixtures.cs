namespace GlDrive.Tests;

/// <summary>
/// SITE RULES bodies in the layouts glftpd sites actually send: FluentFTP hands the multi-line
/// reply over either with or without the "200-" continuation prefix, rules come numbered as
/// "1.", "(1)", "01)" or inside an ASCII table, and sections are introduced by "X Section Rules",
/// "[ TV ]" or "-= MP3 =-" banners.
/// </summary>
internal static class SiteRulesFixtures
{
    public static readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0DAY"] = "/site/0day",
        ["APPS"] = "/site/apps",
        ["GAMES"] = "/site/games",
        ["TV_HD"] = "/site/tv-hd",
        ["MP3"] = "/site/mp3",
    };

    /// <summary>"X Section Rules" headers, "(n)" numbering, FTP continuation prefixes.</summary>
    public const string SectionHeaders = """
        200- .----------------------------------------------.
        200- |            General Site Rules                |
        200- `----------------------------------------------'
        200- (1) No leeching from other sites, 3x nuke
        200- (2) Max 3 simultaneous logins per user
        200- (3) Allowed extensions: .rar .r00 .nfo .sfv .zip .diz
        200- 0Day Section Rules
        200- (1) English Only
        200- (2) No Driverware/Deviceware/Themeware
        200- (3) No BETA/ABANDONED/Vanity/Demo
        200- (4) CADCAM/CARCAD Computer-Aided Design
        200- Apps Section Rules
        200- (1) Windows Only
        200- (2) No Hardware Specific releases
        200- Games Section Rules
        200- (1) Current year only
        200- (2) Nuke 3x@dubs
        200- (3) Ports of popular titles are fine
        200  End of rules
        """;

    /// <summary>"[ TV ]" / "-= MP3 =-" banners, "1." numbering, nuke and group lines, no FTP prefix.</summary>
    public const string BannerHeaders = """
        [ TV ]
         1. Nuke 3x@720P, 2160p
         2. No SUBBED releases
         3. Disallowed Groups: -LOL, -KILLERS
         4. Only English allowed
        -= MP3 =-
         1. No bootlegs allowed
         2. Max 2 uploads per user
         3. Affils: GRP1, GRP2/GRP3
         4. Minimum size 5MB
         5. Piano music releases are welcome
        """;

    /// <summary>An ASCII rule table where every rule is a "| nn |" row.</summary>
    public const string Table = """
        +----+-----------------------------------+--------+
        | 01 | No POP                            | nuke 5x |
        | 02 | No CRAP quality encodes           | nuke 3x |
        | 03 | Banned file types: .exe .com .bat | nuke 1x |
        | 04 | Do not request removal of files   | warn    |
        | 05 | Max 4 downloads                   |         |
        +----+-----------------------------------+--------+
        """;
}
