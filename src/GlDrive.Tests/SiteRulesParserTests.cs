using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// <see cref="SiteRulesParser"/> is the merge of two parsers that used to run side by side:
/// the old <c>SiteRulesParser.Parse</c> (Detect Rules button: slots, extensions, "No X", nuke
/// targets, groups, sizes) and <c>OpenRouterClient.ParseRulesFallback</c> (AI Setup's supplement:
/// section-scoped keyword rules). These tests began as characterization of both (commit
/// f8a52d3); every expectation that differs from that baseline is marked CHANGED with why.
/// Both buttons now get the union of the two outputs.
/// </summary>
public sealed class SiteRulesParserTests
{
    private const int Year = 2026;

    private static SiteRulesParser.ParsedRules Parse(string text) =>
        SiteRulesParser.Parse(text, SiteRulesFixtures.Sections.Keys, Year);

    private static string[] Keyword(SiteRulesParser.ParsedRules parsed) =>
        parsed.KeywordRules.Select(Describe).ToArray();

    private static string[] Skiplist(SiteRulesParser.ParsedRules parsed) =>
        SiteRulesParser.ToSkiplistRules(parsed).Select(Describe).ToArray();

    private static string Describe(GlDrive.Config.SkiplistRule r) =>
        $"{r.Pattern}|{r.Section ?? "-"}|{(r.MatchDirectories ? "d" : "")}{(r.MatchFiles ? "f" : "")}{(r.IsRegex ? "|re" : "")}";

    private static readonly string[] LanguageTags =
    [
        "GERMAN", "FRENCH", "SPANISH", "ITALIAN", "DUTCH", "SWEDISH", "DANISH",
        "NORWEGIAN", "POLISH", "RUSSIAN", "PORTUGUESE", "CHINESE", "JAPANESE",
        "KOREAN", "ARABIC", "TURKISH", "HINDI", "THAI", "CZECH", "HUNGARIAN",
        "ROMANIAN", "FINNISH", "HEBREW", "GREEK", "CROATIAN", "SERBIAN",
        "SUBBED", "DUBBED", "MULTi", "MULTiLANGUAGE"
    ];

    private static readonly string[] SectionHeaderKeywordRules =
    [
        .. LanguageTags.Select(t => $"*{t}*|0DAY|d"),
        "*DRIVERWARE*|0DAY|d", "*DEVICEWARE*|0DAY|d", "*THEMEWARE*|0DAY|d",
        "*BETA*|0DAY|d", "*ABANDONED*|0DAY|d", "*VANITY*|0DAY|d", "*DEMO*|0DAY|d",
        "*CADCAM*|0DAY|d", "*CAD.CAM*|0DAY|d", "*CAD-CAM*|0DAY|d",
        "*CARCAD*|0DAY|d", "*CAR.CAD*|0DAY|d", "*CAR-CAD*|0DAY|d",
        "*LINUX*|APPS|d", "*MACOS*|APPS|d", "*MAC.OSX*|APPS|d", "*MacOSX*|APPS|d",
        "*HARDWARE*|APPS|d", "*FIRMWARE*|APPS|d",
        "*2023*|GAMES|d", "*2024*|GAMES|d", "*2025*|GAMES|d",
        // CHANGED: "*POP*|GAMES" is gone — keywords now match whole words, and "Ports of
        // popular titles are fine" only contains "pop" inside "popular".
    ];

    [Fact]
    public void Section_rules_headers_scope_keyword_rules()
    {
        Assert.Equal(SectionHeaderKeywordRules, Keyword(Parse(SiteRulesFixtures.SectionHeaders)));
    }

    [Fact]
    public void Section_header_layout_line_rules()
    {
        var parsed = Parse(SiteRulesFixtures.SectionHeaders);

        Assert.Equal(3, parsed.MaxSimultaneous);
        Assert.Null(parsed.MaxUploads);
        Assert.Null(parsed.MaxDownloads);
        Assert.Equal([".rar", ".r00", ".nfo", ".sfv", ".zip", ".diz"], parsed.AllowedExtensions);
        Assert.Empty(parsed.DeniedExtensions);
        Assert.Equal(["*hardware specific*", "*dubs*"], parsed.DeniedPatterns);
        Assert.Empty(parsed.Affils);
        Assert.Equal(18, parsed.RawRules.Count);
        // CHANGED: "(1)" numbering is stripped like "1." already was (was "(1) No leeching ...").
        Assert.Equal("No leeching from other sites, 3x nuke", parsed.RawRules[3]);
    }

    [Fact]
    public void Section_header_layout_skiplist_is_the_union_of_both_old_parsers()
    {
        // CHANGED: Detect Rules used to produce only the last three; AI Setup only the keyword rules.
        Assert.Equal(
        [
            .. SectionHeaderKeywordRules,
            @"^.*\.(?!(rar|r00|nfo|sfv|zip|diz)$)[^.]+$|-|f|re",
            "*hardware specific*|-|df",
            "*dubs*|-|df",
        ], Skiplist(Parse(SiteRulesFixtures.SectionHeaders)));
    }

    [Fact]
    public void Banner_layout()
    {
        var parsed = Parse(SiteRulesFixtures.BannerHeaders);

        // "[ TV ]" / "-= MP3 =-" banners deliberately do not scope anything: a rule scoped to a
        // Sections key only fires when the announce category is literally that key, so scoping
        // here would turn working site-wide denies into dead ones.
        Assert.Empty(parsed.KeywordRules);
        Assert.Equal(2, parsed.MaxUploads);
        Assert.Equal(5L * 1024 * 1024, parsed.MinSizeBytes);
        // CHANGED: "*music*" is gone — "Piano music" is no longer read as "no music"
        // ("no" must start a word).
        Assert.Equal(["*720P*", "*2160p*", "*subbed*", "*bootlegs*"], parsed.DeniedPatterns);
        // CHANGED: disallowed groups (LOL, KILLERS) used to be added to Affils — the list that
        // auto-allows past every deny. They are now DeniedGroups. Affils also keep their case
        // (were "grp1", "grp2", "grp3").
        Assert.Equal(["GRP1", "GRP2", "GRP3"], parsed.Affils);
        Assert.Equal(["LOL", "KILLERS"], parsed.DeniedGroups);
        Assert.Equal(
        [
            "*720P*|-|df", "*2160p*|-|df", "*subbed*|-|df", "*bootlegs*|-|df",
            // CHANGED: new — banned groups deny their release directories.
            "*-LOL|-|d", "*-KILLERS|-|d",
        ], Skiplist(parsed));
    }

    [Fact]
    public void Table_layout()
    {
        var parsed = Parse(SiteRulesFixtures.Table);

        Assert.Equal(4, parsed.MaxDownloads);
        Assert.Equal([".exe", ".com", ".bat"], parsed.DeniedExtensions);
        Assert.Empty(parsed.DeniedPatterns); // "No POP |" and "No CRAP quality" have no terminator
        // CHANGED: "*OVA*" is gone — "removal" only contains "ova" mid-word.
        Assert.Equal(["*POP*|-|d", "*CRAP*|-|d"], Keyword(parsed));
        // CHANGED: Detect Rules gains the two keyword rows; AI Setup gains the extension rows.
        Assert.Equal(["*POP*|-|d", "*CRAP*|-|d", "*.exe|-|f", "*.com|-|f", "*.bat|-|f"], Skiplist(parsed));
    }

    [Fact]
    public void Without_section_names_keyword_rules_are_site_wide()
    {
        var parsed = SiteRulesParser.Parse(SiteRulesFixtures.SectionHeaders, currentYear: Year);

        Assert.NotEmpty(parsed.KeywordRules);
        Assert.All(parsed.KeywordRules, r => Assert.Null(r.Section));
    }

    [Fact]
    public void General_header_clears_the_section_scope()
    {
        const string text = """
            Apps Section Rules
            1. No BETA
            General Upload Rules
            2. No BETA
            """;

        Assert.Equal(["*BETA*|APPS|d", "*BETA*|-|d"], Keyword(Parse(text)));
    }

    [Fact]
    public void Keywords_match_whole_words_and_plurals()
    {
        Assert.Equal(["*DEMO*|-|d"], Keyword(Parse("1. No demos")));
        Assert.Empty(Keyword(Parse("1. Democracy is not a release type")));
    }

    [Fact]
    public void Groups_banned_inside_a_nuke_rule_are_denied()
    {
        var parsed = Parse("Nuke 3x|Disallowed Groups: -GRP1, -GRP2");

        Assert.Empty(parsed.Affils);
        Assert.Equal(["GRP1", "GRP2"], parsed.DeniedGroups);
    }

    [Fact]
    public void Skiplist_deduplicates_by_pattern_and_section_ignoring_case()
    {
        // "No BETA releases" yields "*BETA*" from the keyword table and "*beta*" from "No X".
        var rules = Skiplist(Parse("1. No BETA releases"));

        Assert.Equal(["*BETA*|-|d"], rules);
    }
}
