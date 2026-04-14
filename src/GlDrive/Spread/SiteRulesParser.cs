using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Spread;

/// <summary>
/// Parses glftpd SITE RULES output to auto-configure spread settings.
/// Typical SITE RULES output is multi-line with numbered rules like:
///   200- 1. Max 3 simultaneous transfers per user
///   200- 2. No MP3 releases under 128kbps
///   200- 3. Allowed extensions: .rar .r00-.r99 .nfo .sfv .nzb .jpg
///   200  End of rules
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
        public long? MinSizeBytes { get; init; }
        public long? MaxSizeBytes { get; init; }
        public List<string> RawRules { get; init; } = [];
    }

    public static ParsedRules Parse(string siteRulesOutput)
    {
        var rules = new List<string>();
        var deniedExts = new List<string>();
        var allowedExts = new List<string>();
        var deniedPatterns = new List<string>();
        var affils = new List<string>();
        int? maxSimultaneous = null;
        int? maxUploads = null;
        int? maxDownloads = null;
        long? minSize = null;
        long? maxSize = null;

        foreach (var rawLine in siteRulesOutput.Split('\n'))
        {
            // Strip FTP response prefix (e.g., "200- ", "200  ")
            var line = StripFtpPrefix(rawLine).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Strip leading rule number (e.g., "1. ", "01) ", "#1 ")
            line = RuleNumberRegex().Replace(line, "").Trim();
            if (string.IsNullOrEmpty(line) || line.Equals("End of rules", StringComparison.OrdinalIgnoreCase))
                continue;

            rules.Add(line);

            var lower = line.ToLowerInvariant();

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
            var deniedMatch = DeniedExtRegex().Match(lower);
            if (deniedMatch.Success)
            {
                var exts = ExtractExtensions(line);
                deniedExts.AddRange(exts);
                continue;
            }

            // Allowed file extensions
            var allowedMatch = AllowedExtRegex().Match(lower);
            if (allowedMatch.Success)
            {
                var exts = ExtractExtensions(line);
                allowedExts.AddRange(exts);
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

            // Affiliation groups — "affils:", "pre-group:", "disallowed groups:"
            var affilMatch = AffilRegex().Match(lower);
            if (affilMatch.Success)
            {
                var groupText = affilMatch.Groups[1].Value;
                var groups = Regex.Split(groupText, @"[,\s/&|]+")
                    .Select(g => g.Trim().TrimStart('-').Trim())
                    .Where(g => g.Length > 1 && g.Length < 30 && !IsCommonWord(g));
                affils.AddRange(groups);
                continue;
            }

            // Disallowed/banned groups — "Disallowed Groups: -GRP1, -GRP2"
            var disallowedGroupsMatch = DisallowedGroupsRegex().Match(line);
            if (disallowedGroupsMatch.Success)
            {
                var groupText = disallowedGroupsMatch.Groups[1].Value;
                var groups = Regex.Split(groupText, @"[,\s]+")
                    .Select(g => g.Trim().TrimStart('-').Trim())
                    .Where(g => g.Length > 1 && g.Length < 30 && !IsCommonWord(g));
                affils.AddRange(groups);
                continue;
            }

            // Nuke rules — "Nuke 3x@dubs", "Nuke 3x@720P, 2160p", "Nuke 3x@Anything else"
            var nukeMatch = NukeRuleRegex().Match(line);
            if (nukeMatch.Success)
            {
                var nukePattern = nukeMatch.Groups[1].Value.Trim();

                // "Nuke 3x|Disallowed Groups: -GRP1, -GRP2" → extract as affils
                var groupsInNuke = DisallowedGroupsRegex().Match(nukePattern);
                if (groupsInNuke.Success)
                {
                    var groups = Regex.Split(groupsInNuke.Groups[1].Value, @"[,\s]+")
                        .Select(g => g.Trim().TrimStart('-').Trim())
                        .Where(g => g.Length > 1 && g.Length < 30 && !IsCommonWord(g));
                    affils.AddRange(groups);
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
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
            RawRules = rules
        };
    }

    /// <summary>
    /// Convert parsed rules into skiplist rules for the spreader.
    /// </summary>
    public static List<SkiplistRule> ToSkiplistRules(ParsedRules parsed)
    {
        var rules = new List<SkiplistRule>();

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

        return rules;
    }

    private static string StripFtpPrefix(string line)
    {
        // Match "200- ", "200  ", "XXX- ", etc.
        var match = FtpPrefixRegex().Match(line);
        return match.Success ? line[match.Length..] : line;
    }

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

    [GeneratedRegex(@"^(?:\d+[.):\-]\s*|#\d+\s*)")]
    private static partial Regex RuleNumberRegex();

    [GeneratedRegex(@"max(?:imum)?\s+(\d+)\s+(?:simultaneous\s+)?(?:transfer|connection|slot|login|upload|download)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSlotsRegex();

    [GeneratedRegex(@"(?:no|banned?|denied?|not\s+allowed?|forbidden|disallowed?|prohibited)\b.*(?:extension|file\s*type|format)", RegexOptions.IgnoreCase)]
    private static partial Regex DeniedExtRegex();

    [GeneratedRegex(@"(?:allowed?|accepted?|permitted?|valid)\b.*(?:extension|file\s*type|format)", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedExtRegex();

    [GeneratedRegex(@"(?:no|banned?|not\s+allowed?|forbidden)\s+(\w[\w\s]{1,40}?)(?:\s+(?:release|content|upload|allowed|permitted)|\s*[.!]|$)", RegexOptions.IgnoreCase)]
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
