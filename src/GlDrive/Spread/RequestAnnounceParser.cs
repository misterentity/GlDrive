using System.Globalization;
using System.Text.RegularExpressions;

namespace GlDrive.Spread;

public enum RequestEventKind { Requested, Filled }

public sealed record RequestEvent(RequestEventKind Kind, string Release, int? Number, string? Nick);

public sealed class RequestAnnounceParser
{
    public const string DefaultRequestedPattern =
        @"REQUESTED by\s+(?<nick>\S+)\s*:\s*\[\s*(?<number>\d+)\s*:?\s*\]\s*(?<release>\S+)";
    public const string DefaultFilledPattern =
        @"(?<release>\S+)\s+was\s+REQFILLED(?:\s+thanks to\s+(?<nick>[^\s!]+))?";
    public const string LegacyCommandPattern = @"!request\s+(?<release>\S+)";
    public const string DefaultDirectoryPrefix = "REQUEST-";

    private readonly Regex _requested;
    private readonly Regex _filled;
    private readonly string _directoryPrefix;

    public RequestAnnounceParser(string? requestedPattern = null, string? filledPattern = null,
        string? directoryPrefix = null)
    {
        if (string.Equals(requestedPattern?.Trim(), LegacyCommandPattern, StringComparison.Ordinal))
            requestedPattern = null;

        var invalidPatterns = new List<string>();
        _requested = CreateRegex(requestedPattern, DefaultRequestedPattern, invalidPatterns);
        _filled = CreateRegex(filledPattern, DefaultFilledPattern, invalidPatterns);
        _directoryPrefix = string.IsNullOrWhiteSpace(directoryPrefix) ? DefaultDirectoryPrefix : directoryPrefix;
        InvalidPatterns = invalidPatterns.AsReadOnly();
    }

    public IReadOnlyList<string> InvalidPatterns { get; }

    public RequestEvent? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return ParseMatch(_filled.Match(text), RequestEventKind.Filled)
                ?? ParseMatch(_requested.Match(text), RequestEventKind.Requested);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public string? ReleaseFromDirectoryName(string? name)
    {
        if (name is null || !name.StartsWith(_directoryPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var release = name[_directoryPrefix.Length..];
        return IsPlausibleRelease(release) ? release : null;
    }

    public string DirectoryNameFor(string release) => _directoryPrefix + release;

    public static bool IsPlausibleRelease(string? release)
    {
        if (release is null || release.Length is < 5 or > 200 || release[0] is '.' or '-'
            || release.Contains("..", StringComparison.Ordinal))
            return false;

        var hasLetterOrDigit = false;
        foreach (var character in release)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)
                || character is '/' or '\\' or '*' or '?' or '"' or '<' or '>' or '|')
                return false;

            hasLetterOrDigit |= char.IsLetterOrDigit(character);
        }

        return hasLetterOrDigit;
    }

    private static Regex CreateRegex(string? pattern, string fallback, List<string> invalidPatterns)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromMilliseconds(200);
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                var regex = new Regex(pattern, options, timeout);
                if (regex.GetGroupNames().Contains("release", StringComparer.Ordinal))
                    return regex;
            }
            catch (ArgumentException)
            {
            }

            invalidPatterns.Add(pattern);
        }

        return new Regex(fallback, options, timeout);
    }

    private static RequestEvent? ParseMatch(Match match, RequestEventKind kind)
    {
        if (!match.Success)
            return null;

        var release = match.Groups["release"].Value.TrimEnd(',', ';', ':', '!');
        if (!IsPlausibleRelease(release))
            return null;

        int? number = int.TryParse(match.Groups["number"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
        var nick = match.Groups["nick"].Value;
        return new RequestEvent(kind, release, number, string.IsNullOrWhiteSpace(nick) ? null : nick);
    }
}
