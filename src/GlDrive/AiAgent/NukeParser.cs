using System.Text.RegularExpressions;
using System.Globalization;

namespace GlDrive.AiAgent;

public sealed record ParsedNuke(DateTime NukedAt, string Nuker, string Release, int Multiplier, string Reason,
    string Section, string? Nukee = null, bool HasExactTimestamp = true, bool ReleaseTruncated = false)
{
    /// <summary>Lower bound from a relative age's displayed precision; null when it cannot be established.</summary>
    public DateTime? EarliestNukedAtUtc { get; init; }
}

public static class NukeParser
{
    private static readonly Regex[] Patterns =
    {
        new(@"(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s+by\s+(?<nuker>\S+)\s*[:\-]\s*(?<release>\S+)\s*\((?<mult>\d+)x\)\s*[\-:]\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"Nuke\s+(?<release>\S+)\s+\((?<mult>\d+)x\)\s+by\s+(?<nuker>\S+)\s+at\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*[\-:]\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"(?<release>\S+)\s+\((?<mult>\d+)x\)\s+(?<nuker>\S+)\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
    };

    private static readonly Regex Amount = new(@"^(?<mult>\d{1,4})\s*x\s*[\d.]+\s*[KMGT]?$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AgePart = new(@"(?<value>\d{1,5})\s*(?<unit>[wdhms])",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ValidAge = new(@"^\s*(?:\d{1,5}\s*[wdhms]\s*)+$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static IEnumerable<ParsedNuke> Parse(string siteNukesOutput, string fallbackSection = "", DateTime? observedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(siteNukesOutput)) yield break;
        if (siteNukesOutput.Length > 2 * 1024 * 1024) yield break;
        var observed = observedAtUtc ?? DateTime.UtcNow;
        (string Nuker, string Nukee, int Multiplier, string Reason)? pending = null;
        foreach (var raw in siteNukesOutput.Split('\n').Take(10000))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Length > 8192) { pending = null; continue; }
            if (line.Length >= 4 && line.Take(3).All(char.IsAsciiDigit) && line[3] is '-' or ' ')
                line = line[4..].Trim();
            if (line.StartsWith('|') && line.EndsWith('|'))
            {
                var columns = line[1..^1].Split('|').Select(column => column.Trim()).ToArray();
                if (columns.All(column => column.All(c => c is '+' or '-' or '=' or ' '))) continue;
                if (columns.Length >= 2 && columns[0].StartsWith("Age:", StringComparison.OrdinalIgnoreCase))
                {
                    if (pending is { } row && columns[1].StartsWith("Dir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = columns[1][4..].Trim().TrimEnd('/');
                        var release = path[(path.LastIndexOf('/') + 1)..];
                        if (!string.IsNullOrWhiteSpace(release) && release.Length <= 1024)
                        {
                            var section = fallbackSection;
                            var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            if (section.Length == 0 && pathParts.Length > 1) section = pathParts[^2];
                            var (time, earliest) = ApproximateTime(columns[0][4..], observed);
                            yield return new ParsedNuke(time, row.Nuker, release,
                                row.Multiplier, row.Reason, section, row.Nukee, HasExactTimestamp: false,
                                ReleaseTruncated: release.Contains("...", StringComparison.Ordinal) || release.Contains('…'))
                            { EarliestNukedAtUtc = earliest };
                        }
                    }
                    pending = null;
                    continue;
                }
                pending = null;
                if (columns.Length >= 4 && Amount.Match(columns[2]) is { Success: true } amount &&
                    int.TryParse(amount.Groups["mult"].Value, out var multiplier) && multiplier > 0 &&
                    columns[0].Length is > 0 and <= 128 && columns[1].Length is > 0 and <= 128)
                {
                    var reason = string.Join(" | ", columns.Skip(3));
                    if (reason.Length is > 0 and <= 2048) pending = (columns[0], columns[1], multiplier, reason);
                }
                continue;
            }
            if (line.All(c => c is '+' or '-' or '=' or ' ')) continue;
            pending = null;
            foreach (var rx in Patterns)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups["ts"].Value, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) break;
                yield return new ParsedNuke(
                    NukedAt: ts,
                    Nuker: m.Groups["nuker"].Value,
                    Release: m.Groups["release"].Value,
                    Multiplier: int.TryParse(m.Groups["mult"].Value, out var mult) ? mult : 1,
                    Reason: m.Groups["reason"].Value.Trim(),
                    Section: fallbackSection
                );
                break;
            }
        }
    }

    private static (DateTime Time, DateTime? Earliest) ApproximateTime(string age, DateTime observed)
    {
        if (!ValidAge.IsMatch(age)) return (observed, null);
        double seconds = 0;
        double precision = double.MaxValue;
        foreach (Match part in AgePart.Matches(age))
        {
            var value = int.Parse(part.Groups["value"].Value, CultureInfo.InvariantCulture);
            var unit = char.ToLowerInvariant(part.Groups["unit"].Value[0]) switch
            {
                'w' => 604800d, 'd' => 86400d, 'h' => 3600d, 'm' => 60d, 's' => 1d, _ => 0d
            };
            seconds += value * unit;
            precision = Math.Min(precision, unit);
        }
        var maximumAge = (observed - DateTime.MinValue).TotalSeconds;
        return (observed.AddSeconds(-Math.Min(seconds, maximumAge)),
            observed.AddSeconds(-Math.Min(seconds + precision, maximumAge)));
    }
}
