using System.Text.RegularExpressions;

namespace GlDrive.AiAgent;

public sealed record ParsedNuke(DateTime NukedAt, string Nuker, string Release, int Multiplier, string Reason, string Section);

public static class NukeParser
{
    private static readonly Regex[] Patterns =
    {
        new(@"(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s+by\s+(?<nuker>\S+)\s*[:\-]\s*(?<release>\S+)\s*\((?<mult>\d+)x\)\s*[\-:]\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Nuke\s+(?<release>\S+)\s+\((?<mult>\d+)x\)\s+by\s+(?<nuker>\S+)\s+at\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*[\-:]\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?<release>\S+)\s+\((?<mult>\d+)x\)\s+(?<nuker>\S+)\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*(?<reason>.+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public static IEnumerable<ParsedNuke> Parse(string siteNukesOutput, string fallbackSection = "")
    {
        if (string.IsNullOrWhiteSpace(siteNukesOutput)) yield break;
        foreach (var raw in siteNukesOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            foreach (var rx in Patterns)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                if (!DateTime.TryParse(m.Groups["ts"].Value, out var ts)) break;
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
}
