using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Characterization of the two SITE RULES parsers as they stand before being merged:
/// <c>SiteRulesParser.Parse</c> (Detect Rules button) and <c>OpenRouterClient.ParseRulesFallback</c>
/// (AI Setup's deterministic supplement). Pins current output, including its known false positives.
/// </summary>
public sealed class SiteRulesParserTests
{
    private static string[] Fallback(string text) =>
        OpenRouterClient.ParseRulesFallback(text, SiteRulesFixtures.Sections)
            .Select(r => $"{r.Pattern}|{r.Section ?? "-"}|{(r.MatchDirectories ? "d" : "")}{(r.MatchFiles ? "f" : "")}")
            .ToArray();

    private static string[] Skiplist(SiteRulesParser.ParsedRules parsed) =>
        SiteRulesParser.ToSkiplistRules(parsed)
            .Select(r => $"{r.Pattern}|{r.Section ?? "-"}|{(r.MatchDirectories ? "d" : "")}{(r.MatchFiles ? "f" : "")}{(r.IsRegex ? "|re" : "")}")
            .ToArray();

    private static readonly string[] LanguageTags =
    [
        "GERMAN", "FRENCH", "SPANISH", "ITALIAN", "DUTCH", "SWEDISH", "DANISH",
        "NORWEGIAN", "POLISH", "RUSSIAN", "PORTUGUESE", "CHINESE", "JAPANESE",
        "KOREAN", "ARABIC", "TURKISH", "HINDI", "THAI", "CZECH", "HUNGARIAN",
        "ROMANIAN", "FINNISH", "HEBREW", "GREEK", "CROATIAN", "SERBIAN",
        "SUBBED", "DUBBED", "MULTi", "MULTiLANGUAGE"
    ];

    [Fact]
    public void Fallback_scopes_keyword_rules_to_section_rules_headers()
    {
        var year = DateTime.Now.Year;
        string[] expected =
        [
            .. LanguageTags.Select(t => $"*{t}*|0DAY|d"),
            "*DRIVERWARE*|0DAY|d", "*DEVICEWARE*|0DAY|d", "*THEMEWARE*|0DAY|d",
            "*BETA*|0DAY|d", "*ABANDONED*|0DAY|d", "*VANITY*|0DAY|d", "*DEMO*|0DAY|d",
            "*CADCAM*|0DAY|d", "*CAD.CAM*|0DAY|d", "*CAD-CAM*|0DAY|d",
            "*CARCAD*|0DAY|d", "*CAR.CAD*|0DAY|d", "*CAR-CAD*|0DAY|d",
            "*LINUX*|APPS|d", "*MACOS*|APPS|d", "*MAC.OSX*|APPS|d", "*MacOSX*|APPS|d",
            "*HARDWARE*|APPS|d", "*FIRMWARE*|APPS|d",
            $"*{year - 3}*|GAMES|d", $"*{year - 2}*|GAMES|d", $"*{year - 1}*|GAMES|d",
            "*POP*|GAMES|d", // false positive: "Ports of popular titles are fine"
        ];
        Assert.Equal(expected, Fallback(SiteRulesFixtures.SectionHeaders));
    }

    [Fact]
    public void Fallback_finds_nothing_under_banner_headers()
    {
        // No "Section Rules" header and no dictionary keyword: "Only English allowed" is not
        // "english only", and SUBBED/dubs are not keywords outside the language list.
        Assert.Empty(Fallback(SiteRulesFixtures.BannerHeaders));
    }

    [Fact]
    public void Fallback_reads_table_rows_as_restrictions()
    {
        string[] expected =
        [
            "*POP*|-|d", "*CRAP*|-|d",
            "*OVA*|-|d", // false positive: "removal" contains "ova"
        ];
        Assert.Equal(expected, Fallback(SiteRulesFixtures.Table));
    }

    [Fact]
    public void Parse_section_header_layout()
    {
        var parsed = SiteRulesParser.Parse(SiteRulesFixtures.SectionHeaders);

        Assert.Equal(3, parsed.MaxSimultaneous);
        Assert.Null(parsed.MaxUploads);
        Assert.Null(parsed.MaxDownloads);
        Assert.Equal([".rar", ".r00", ".nfo", ".sfv", ".zip", ".diz"], parsed.AllowedExtensions);
        Assert.Empty(parsed.DeniedExtensions);
        Assert.Equal(["*hardware specific*", "*dubs*"], parsed.DeniedPatterns);
        Assert.Empty(parsed.Affils);
        Assert.Equal(18, parsed.RawRules.Count);
        Assert.Equal("(1) No leeching from other sites, 3x nuke", parsed.RawRules[3]);
        Assert.Equal(
        [
            @"^.*\.(?!(rar|r00|nfo|sfv|zip|diz)$)[^.]+$|-|f|re",
            "*hardware specific*|-|df",
            "*dubs*|-|df",
        ], Skiplist(parsed));
    }

    [Fact]
    public void Parse_banner_layout()
    {
        var parsed = SiteRulesParser.Parse(SiteRulesFixtures.BannerHeaders);

        Assert.Equal(2, parsed.MaxUploads);
        Assert.Equal(5L * 1024 * 1024, parsed.MinSizeBytes);
        // "*music*" is a false positive: "Piano music" reads as "no music".
        Assert.Equal(["*720P*", "*2160p*", "*subbed*", "*bootlegs*", "*music*"], parsed.DeniedPatterns);
        // Disallowed groups land in Affils — the list that auto-allows past every deny.
        Assert.Equal(["LOL", "KILLERS", "grp1", "grp2", "grp3"], parsed.Affils);
        Assert.Equal(
        [
            "*720P*|-|df", "*2160p*|-|df", "*subbed*|-|df", "*bootlegs*|-|df", "*music*|-|df",
        ], Skiplist(parsed));
    }

    [Fact]
    public void Parse_table_layout()
    {
        var parsed = SiteRulesParser.Parse(SiteRulesFixtures.Table);

        Assert.Equal(4, parsed.MaxDownloads);
        Assert.Equal([".exe", ".com", ".bat"], parsed.DeniedExtensions);
        Assert.Empty(parsed.DeniedPatterns); // "No POP |" and "No CRAP quality" have no terminator
        Assert.Equal(["*.exe|-|f", "*.com|-|f", "*.bat|-|f"], Skiplist(parsed));
    }
}
