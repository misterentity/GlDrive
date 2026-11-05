using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Spread;

/// <summary>
/// The one deterministic SITE RULES parser, used by both the Detect Rules button and AI Setup's
/// supplement to the model. Typical SITE RULES output is multi-line with numbered rules like:
///   200- 1. Max 3 simultaneous transfers per user
///   200- 2. No MP3 releases under 128kbps
///   200- 3. Allowed extensions: .rar .r00-.r99 .nfo .sfv .nzb .jpg
///   200  End of rules
/// Two kinds of output: generic line rules (slots, extensions, "No X", nuke targets, groups,
/// sizes — always site-wide) and keyword rules ("English Only", "Windows Only", known banned
/// categories, "Current year only"), scoped to the section of the nearest "X Section Rules" header.
/// </summary>
public static partial class SiteRulesParser
{
    public record ParsedRules
    {
        public int? MaxSimultaneous { get; init; }
        public int? MaxUploads { get; init; }
        public int? MaxDownloads { get; init; }
        public List<string> DeniedExtensions { get; init; } = [];
        public List<string> AllowedExtensions { get; init; } = [];
        public List<string> DeniedPatterns { get; init; } = [];
        public List<string> Affils { get; init; } = [];
        /// <summary>Groups the rules ban ("Disallowed Groups: -GRP"); denied, never affils.</summary>
        public List<string> DeniedGroups { get; init; } = [];
        /// <summary>Keyword deny rules, section-scoped under a "X Section Rules" header.</summary>
        public List<SkiplistRule> KeywordRules { get; init; } = [];
        public long? MinSizeBytes { get; init; }
        public long? MaxSizeBytes { get; init; }
        public List<string> RawRules { get; init; } = [];
    }

    // Keywords that map to deny patterns; each may emit several glob spellings.
    private static readonly (string Keyword, string[] Patterns)[] DenyKeywords =
    [
        // Software categories
        ("driverware", ["*DRIVERWARE*"]),
        ("deviceware", ["*DEVICEWARE*"]),
        ("themeware", ["*THEMEWARE*"]),
        ("cadcam", ["*CADCAM*", "*CAD.CAM*", "*CAD-CAM*"]),
        ("carcad", ["*CARCAD*", "*CAR.CAD*", "*CAR-CAD*"]),
        ("cae", ["*CAE*"]),
        ("casing", ["*CASING*"]),
        ("casino", ["*CASINO*"]),
        ("csy", ["*CSY*"]),
        // Content types
        ("pop", ["*POP*"]),
        ("fortune", ["*FORTUNE*"]),
        ("monthly", ["*MONTHLY*"]),
        ("beta", ["*BETA*"]),
        ("abandoned", ["*ABANDONED*"]),
        ("vanity", ["*VANITY*"]),
        ("demo", ["*DEMO*"]),
        // Hardware/religious
        ("hardware", ["*HARDWARE*", "*FIRMWARE*"]),
        ("religious", ["*RELIGIOUS*", "*BIBLE*", "*CHRISTIAN*"]),
        ("rpc", ["*RPC*"]),
        // Quality
        ("crap", ["*CRAP*"]),
        // Apps-specific
        ("basicapps", ["*BASICAPPS*", "*BASIC.APPS*"]),
        ("courseware", ["*COURSEWARE*"]),
        ("examware", ["*EXAMWARE*"]),
        // OVA etc (0day specific)
        ("ova", ["*OVA*"]),
        ("svamhapics", ["*SVAMHAPICS*"]),
    ];

    private static readonly string[] LanguageTags =
    [
        "GERMAN", "FRENCH", "SPANISH", "ITALIAN", "DUTCH", "SWEDISH", "DANISH",
        "NORWEGIAN", "POLISH", "RUSSIAN", "PORTUGUESE", "CHINESE", "JAPANESE",
        "KOREAN", "ARABIC", "TURKISH", "HINDI", "THAI", "CZECH", "HUNGARIAN",
        "ROMANIAN", "FINNISH", "HEBREW", "GREEK", "CROATIAN", "SERBIAN",
        "SUBBED", "DUBBED", "MULTi", "MULTiLANGUAGE"
    ];

    // Header word → candidate section keys, first configured one wins.
    private static readonly (string Alias, string[] Candidates)[] HeaderAliases =
    [
        ("0day", ["0DAY", "0DAY_INT"]),
        ("apps", ["APPS", "APPS_INT"]),
        ("games", ["GAMES", "GAMES_INT", "NSW"]),
        ("tv", ["TV_HD", "TV", "TV_SPORTS", "X264_HD"]),
        ("flac", ["FLAC"]),
        ("mp3", ["MP3"]),
        ("mvid", ["MVID"]),
        ("bookware", ["BOOKWARE"]),
        ("x265", ["X265"]),
        ("bluray", ["BLURAY", "BLURAY_UHD"]),
    ];

    /// <param name="sectionNames">Configured section keys that "X Section Rules" headers resolve
    /// to. Without them keyword rules are site-wide.</param>
    /// <param name="currentYear">Year "Current year only" is relative to; defaults to now.</param>
    public static ParsedRules Parse(string siteRulesOutput, IReadOnlyCollection<string>? sectionNames = null,
        int? currentYear = null)
    {
        var sections = sectionNames?.ToList() ?? [];
        var year = currentYear ?? DateTime.Now.Year;
        var rules = new List<string>();
        var deniedExts = new List<string>();
        var allowedExts = new List<string>();
        var deniedPatterns = new List<string>();
        var affils = new List<string>();
        var deniedGroups = new List<string>();
        var keywordRules = new List<SkiplistRule>();
        int? maxSimultaneous = null;
        int? maxUploads = null;
        int? maxDownloads = null;
        long? minSize = null;
        long? maxSize = null;
        string? currentSection = null;

        void AddKeywordRule(string pattern) => keywordRules.Add(new SkiplistRule
        {
            Pattern = pattern, Action = SkiplistAction.Deny,
            MatchDirectories = true, MatchFiles = false, Section = currentSection
        });

        foreach (var rawLine in siteRulesOutput.Split('\n'))
        {
            // Strip FTP response prefix (e.g., "200- ", "200  ")
            var unnumbered = StripFtpPrefix(rawLine).Trim();
            if (string.IsNullOrEmpty(unnumbered)) continue;

            // Strip leading rule number (e.g., "1. ", "01) ", "(1) ", "#1 ")
            var numbered = RuleNumberRegex().IsMatch(unnumbered);
            var line = RuleNumberRegex().Replace(unnumbered, "").Trim();
            if (string.IsNullOrEmpty(line) || line.Equals("End of rules", StringComparison.OrdinalIgnoreCase))
                continue;

            rules.Add(line);

            var lower = line.ToLowerInvariant();

            // Section headers ("0Day Section Rules") set the keyword-rule scope; "General Upload
            // Rules" / "General Site Rules" clear it. A header line yields no keyword rules but
            // still goes through the line rules below.
            if (lower.Contains("section rule"))
                currentSection = DetectSectionFromHeader(lower, sections);
            else if (lower.Contains("general") && (lower.Contains("upload") || lower.Contains("site")) && lower.Contains("rule"))
                currentSection = null;
            else
                CollectKeywordRules(lower, numbered, currentSection != null, year, AddKeywordRule);

            // Max simultaneous transfers/connections
            var slotMatch = MaxSlotsRegex().Match(lower);
            if (slotMatch.Success && int.TryParse(slotMatch.Groups[1].Value, out var slots))
            {
                if (lower.Contains("upload"))
                    maxUploads = slots;
                else if (lower.Contains("download"))
                    maxDownloads = slots;
                else
                    maxSimultaneous = slots;
                continue;
            }

            // Denied/banned file extensions
            if (DeniedExtRegex().IsMatch(lower))
            {
                deniedExts.AddRange(ExtractExtensions(line));
                continue;
            }

            // Allowed file extensions
            if (AllowedExtRegex().IsMatch(lower))
            {
                allowedExts.AddRange(ExtractExtensions(line));
                continue;
            }

            // Denied content patterns (e.g., "no xxx", "no porn", "no webcam")
            var deniedContentMatch = DeniedContentRegex().Match(lower);
            if (deniedContentMatch.Success)
            {
                var pattern = deniedContentMatch.Groups[1].Value.Trim();
                if (pattern.Length > 0 && pattern.Length < 50)
                    deniedPatterns.Add($"*{pattern}*");
                continue;
            }

            // Affiliation groups — "affils:", "pre-group:"
            var affilMatch = AffilRegex().Match(line);
            if (affilMatch.Success)
            {
                affils.AddRange(SplitGroups(affilMatch.Groups[1].Value, @"[,\s/&|]+"));
                continue;
            }

            // Disallowed/banned groups — "Disallowed Groups: -GRP1, -GRP2"
            var disallowedGroupsMatch = DisallowedGroupsRegex().Match(line);
            if (disallowedGroupsMatch.Success)
            {
                deniedGroups.AddRange(SplitGroups(disallowedGroupsMatch.Groups[1].Value, @"[,\s]+"));
                continue;
            }

            // Nuke rules — "Nuke 3x@dubs", "Nuke 3x@720P, 2160p", "Nuke 3x@Anything else"
            var nukeMatch = NukeRuleRegex().Match(line);
            if (nukeMatch.Success)
            {
                var nukePattern = nukeMatch.Groups[1].Value.Trim();

                // "Nuke 3x|Disallowed Groups: -GRP1, -GRP2"
                var groupsInNuke = DisallowedGroupsRegex().Match(nukePattern);
                if (groupsInNuke.Success)
                {
                    deniedGroups.AddRange(SplitGroups(groupsInNuke.Groups[1].Value, @"[,\s]+"));
                }
                else if (!string.IsNullOrEmpty(nukePattern) &&
                    !nukePattern.Equals("Anything else", StringComparison.OrdinalIgnoreCase))
                {
                    // Split comma-separated nuke targets: "720P, 2160p" → ["720P", "2160p"]
                    foreach (var part in nukePattern.Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (part.Length > 0 && part.Length < 40)
                            deniedPatterns.Add($"*{part}*");
                    }
                }
                continue;
            }

            // Min file/release size
            var minSizeMatch = MinSizeRegex().Match(lower);
            if (minSizeMatch.Success)
            {
                minSize = ParseSize(minSizeMatch.Groups[1].Value, minSizeMatch.Groups[2].Value);
                continue;
            }

            // Max file/release size
            var maxSizeMatch = MaxSizeRegex().Match(lower);
            if (maxSizeMatch.Success)
            {
                maxSize = ParseSize(maxSizeMatch.Groups[1].Value, maxSizeMatch.Groups[2].Value);
                continue;
            }
        }

        return new ParsedRules
        {
            MaxSimultaneous = maxSimultaneous,
            MaxUploads = maxUploads,
            MaxDownloads = maxDownloads,
            DeniedExtensions = deniedExts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedExtensions = allowedExts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DeniedPatterns = deniedPatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Affils = affils.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DeniedGroups = deniedGroups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            KeywordRules = keywordRules
                .DistinctBy(r => (r.Pattern.ToUpperInvariant(), r.Section?.ToUpperInvariant())).ToList(),
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
            RawRules = rules
        };
    }

    private static void CollectKeywordRules(string lower, bool numbered, bool inSection, int year,
        Action<string> add)
    {
        if (lower.Contains("english only"))
            foreach (var lang in LanguageTags) add($"*{lang}*");

        if (lower.Contains("windows only"))
        {
            add("*LINUX*");
            add("*MACOS*");
            add("*MAC.OSX*");
            add("*MacOSX*");
        }

        // Category bans, including slash lists: "No Driverware/Deviceware/Themeware",
        // "No BETA/ABANDONED/Vanity/Demo", "CADCAM/CARCAD Computer-Aided" (no "No" needed in
        // a section). A line counts as a restriction when it says so, is a numbered or table
        // rule, or sits under a section header.
        var isRestriction = inSection || numbered || NoWordRegex().IsMatch(lower) || lower.Contains("nuke") ||
            lower.Contains("| ") || lower.Contains("not allowed") || lower.Contains("banned");
        if (isRestriction)
        {
            foreach (var (keyword, patterns) in DenyKeywords)
            {
                // Whole word (plural allowed): "popular" is not POP, "removal" is not OVA.
                if (!Regex.IsMatch(lower, $@"(?<![a-z0-9]){keyword}s?(?![a-z0-9])")) continue;
                foreach (var pattern in patterns) add(pattern);
            }
        }

        // "Current year only" (games): deny the three prior year tags
        if (lower.Contains("current year only"))
            for (var y = year - 3; y < year; y++) add($"*{y}*");
    }

    private static string? DetectSectionFromHeader(string lowerHeader, List<string> sectionNames)
    {
        // Direct section name matching: "0Day" → "0DAY", "Apps" → "APPS", "Games" → "GAMES"
        foreach (var (alias, candidates) in HeaderAliases)
        {
            if (!lowerHeader.Contains(alias)) continue;
            foreach (var candidate in candidates)
            {
                if (sectionNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        // Fuzzy: try normalizing both sides
        foreach (var name in sectionNames)
        {
            var normName = name.ToLowerInvariant().Replace("_", "").Replace("-", "");
            if (lowerHeader.Contains(normName)) return name;
        }

        return null;
    }

    /// <summary>
    /// Convert parsed rules into skiplist rules for the spreader: keyword rules first (they are
    /// the section-aware ones), then extension, content and banned-group rules. Deduplicated by
    /// pattern + section, case-insensitively.
    /// </summary>
    public static List<SkiplistRule> ToSkiplistRules(ParsedRules parsed)
    {
        var rules = new List<SkiplistRule>(parsed.KeywordRules);

        // Denied extensions become file deny rules
        foreach (var ext in parsed.DeniedExtensions)
        {
            var pattern = ext.StartsWith('.') ? $"*{ext}" : $"*.{ext}";
            rules.Add(new SkiplistRule
            {
                Pattern = pattern,
                Action = SkiplistAction.Deny,
                MatchDirectories = false,
                MatchFiles = true
            });
        }

        // If allowed extensions are specified, deny everything else
        // (only if we have allowed exts and no denied exts — avoids conflicts)
        if (parsed.AllowedExtensions.Count > 0 && parsed.DeniedExtensions.Count == 0)
        {
            // Build a regex that matches files NOT in the allowed list
            var extPatterns = parsed.AllowedExtensions
                .Select(e => e.TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var allowedRegex = $@"^.*\.(?!({string.Join("|", extPatterns)})$)[^.]+$";
            rules.Add(new SkiplistRule
            {
                Pattern = allowedRegex,
                IsRegex = true,
                Action = SkiplistAction.Deny,
                MatchDirectories = false,
                MatchFiles = true
            });
        }

        // Denied content patterns become directory deny rules
        foreach (var pattern in parsed.DeniedPatterns)
        {
            rules.Add(new SkiplistRule
            {
                Pattern = pattern,
                Action = SkiplistAction.Deny,
                MatchDirectories = true,
                MatchFiles = true
            });
        }

        // Banned groups deny their release directories ("Show.S01E01.720p-GRP")
        foreach (var group in parsed.DeniedGroups)
        {
            rules.Add(new SkiplistRule
            {
                Pattern = $"*-{group}",
                Action = SkiplistAction.Deny,
                MatchDirectories = true,
                MatchFiles = false
            });
        }

        return rules
            .DistinctBy(r => (r.Pattern.ToUpperInvariant(), r.Section?.ToUpperInvariant()))
            .ToList();
    }

    private static string StripFtpPrefix(string line)
    {
        // Match "200- ", "200  ", "XXX- ", etc.
        var match = FtpPrefixRegex().Match(line);
        return match.Success ? line[match.Length..] : line;
    }

    private static IEnumerable<string> SplitGroups(string groupText, string separators) =>
        Regex.Split(groupText, separators)
            .Select(g => g.Trim().TrimStart('-').Trim())
            .Where(g => g.Length > 1 && g.Length < 30 && !IsCommonWord(g.ToLowerInvariant()));

    private static List<string> ExtractExtensions(string text)
    {
        return ExtensionRegex().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(e => e.Length <= 10)
            .ToList();
    }

    private static long? ParseSize(string numberStr, string unitStr)
    {
        if (!double.TryParse(numberStr, out var number)) return null;
        return unitStr.ToLowerInvariant() switch
        {
            "kb" or "k" => (long)(number * 1024),
            "mb" or "m" => (long)(number * 1024 * 1024),
            "gb" or "g" => (long)(number * 1024L * 1024 * 1024),
            "tb" or "t" => (long)(number * 1024L * 1024 * 1024 * 1024),
            _ => (long)number
        };
    }

    private static bool IsCommonWord(string word) =>
        word is "the" or "and" or "are" or "for" or "not" or "with" or "this" or "from"
            or "site" or "group" or "groups" or "affil" or "affils" or "affiliated";

    [GeneratedRegex(@"^\d{3}[- ] ?")]
    private static partial Regex FtpPrefixRegex();

    [GeneratedRegex(@"^(?:\d+[.):\-]\s*|\(\d+\)\s*|#\d+\s*)")]
    private static partial Regex RuleNumberRegex();

    [GeneratedRegex(@"\bno\b")]
    private static partial Regex NoWordRegex();

    [GeneratedRegex(@"max(?:imum)?\s+(\d+)\s+(?:simultaneous\s+)?(?:transfer|connection|slot|login|upload|download)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSlotsRegex();

    [GeneratedRegex(@"(?:no|banned?|denied?|not\s+allowed?|forbidden|disallowed?|prohibited)\b.*(?:extension|file\s*type|format)", RegexOptions.IgnoreCase)]
    private static partial Regex DeniedExtRegex();

    [GeneratedRegex(@"(?:allowed?|accepted?|permitted?|valid)\b.*(?:extension|file\s*type|format)", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedExtRegex();

    [GeneratedRegex(@"\b(?:no|banned?|not\s+allowed?|forbidden)\s+(\w[\w\s]{1,40}?)(?:\s+(?:release|content|upload|allowed|permitted)|\s*[.!]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeniedContentRegex();

    [GeneratedRegex(@"(?:affil(?:iat(?:ed?|ion))?s?|pre[- ]?group|site\s*group)s?\s*[:=\-]\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex AffilRegex();

    [GeneratedRegex(@"(?:disallowed|banned|denied|blacklisted)\s+groups?\s*[:=\-]\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisallowedGroupsRegex();

    [GeneratedRegex(@"nuke\s+\d+x?\s*[@|]\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex NukeRuleRegex();

    [GeneratedRegex(@"min(?:imum)?\s+(?:file\s*)?size\s*[:=]?\s*(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb|k|m|g|t)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinSizeRegex();

    [GeneratedRegex(@"max(?:imum)?\s+(?:file\s*)?size\s*[:=]?\s*(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb|k|m|g|t)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSizeRegex();

    [GeneratedRegex(@"\.\w{1,10}\b")]
    private static partial Regex ExtensionRegex();
}
