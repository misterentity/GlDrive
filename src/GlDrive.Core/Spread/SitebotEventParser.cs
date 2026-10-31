using System.Globalization;
using System.Text.RegularExpressions;

namespace GlDrive.Spread;

/// <summary>Bounded parsing of common pzs-ng, ngBot and dZSbot theme families.</summary>
public static class SitebotEventParser
{
    public const int MaximumMessageLength = 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const string Labels = "NEW RELEASE|NEW|CHECKERED-FLAG|CROSSED STICKS|COMPLETE|HALFWAY|RACE|NUKED|NUKE|UNNUKED|UNNUKE|TV-MAZE|IMDB";
    private static readonly Regex Header = Pattern(@"^\s*(?:\[\s*(?<kind>" + Labels + @")\s*\]|(?<kind>" + Labels + @")\s*(?::|>>))");
    private static readonly Regex Bracketed = Pattern(@"^\s*\[\s*(?<kind>" + Labels + @")\s*\]\s*(?:in\s*)?\[\s*(?<section>[^\]]+)\s*\]\s*(?<release>\S+)(?<details>.*)$");
    private static readonly Regex Colon = Pattern(@"^\s*(?<kind>" + Labels + @")\s*:\s*(?<section>\S+)\s+(?<release>\S+)(?<details>.*)$");
    private static readonly Regex Arrows = Pattern(@"^\s*(?<kind>NUKED|UNNUKED)\s*>>\s*(?<section>[^/\s]+)/(?<release>\S+)\s*>>(?<details>.*)$");
    private static readonly Regex Size = Pattern(@"(?:^|[\s\[/=:])(?<size>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>[KMGT]?i?B)(?:$|[^A-Za-z])");
    private static readonly Regex Speed = Pattern(@"(?:^|[\s\[/=:])(?<speed>[0-9]+(?:\.[0-9]+)?)\s*(?<speedunit>[KMGT]?i?B)/s\b");
    private static readonly Regex Files = Pattern(@"(?:^|[\s/\[])(?<files>[0-9]+)\s*F(?:ILES)?(?:$|[\s/\]\-])");
    private static readonly Regex Imdb = Pattern(@"\b(?<imdb>tt[0-9]{5,12})\b");
    private static readonly Regex Solo = Pattern(@"\bSolo\s+by\s+(?<winner>[^\s,\[\]()<>/]+)");
    private static readonly Regex Winner = Pattern(@"\bwinner\s*[:=]\s*(?<winner>[^\s,\[\]()<>/]+)");
    private static readonly Regex Leader = Pattern(@"\bleader\s*[:=]\s*(?<leader>[^\s,\[\]()<>/]+)");
    private static readonly Regex Actor = Pattern(@"\bby\s+(?<actor>[^\s,\[\]()<>/]+)");
    private static readonly Regex Factor = Pattern(@"(?:^|[\s(\[])(?<factor>[0-9]+)x(?:$|[\s)\]])");
    private static readonly Regex Rating = Pattern(@"\brating\s*[:=]\s*(?<rating>[0-9]+(?:\.[0-9]+)?)");
    private static readonly Regex Votes = Pattern(@"\bvotes\s*[:=]\s*(?<votes>[0-9,]+)");

    public static SitebotEvent? Parse(string? text, IReadOnlyList<SitebotTemplate>? templates = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumMessageLength) return null;
        try
        {
            if (templates != null && templates.Count <= 32)
                foreach (var template in templates)
                {
                    if (ValidateTemplate(template).Count != 0) continue;
                    var match = Pattern(template.Pattern).Match(text);
                    if (match.Success && match.Index == 0) return Build(template.Kind, match, text);
                }
            foreach (var pattern in new[] { Bracketed, Colon, Arrows })
            {
                var match = pattern.Match(text);
                if (match.Success && Kind(match.Groups["kind"].Value) is { } kind) return Build(kind, match, text);
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    public static SitebotEvent? ParseTrusted(string serverId, string channel, string nick, string userHost,
        string text, SitebotChannelConfig? config, DateTime observedAtUtc)
    {
        if (string.IsNullOrEmpty(channel) || "#&!+".IndexOf(channel[0]) < 0 || !IsTrusted(config, nick, userHost)) return null;
        var result = Parse(text, config!.Templates);
        return result == null ? null : result with
        {
            ServerId = serverId, Channel = channel, BotNick = nick, UserHost = userHost,
            ObservedAtUtc = observedAtUtc.Kind == DateTimeKind.Utc ? observedAtUtc : observedAtUtc.ToUniversalTime()
        };
    }

    public static bool IsTrusted(SitebotChannelConfig? config, string? nick, string? userHost)
    {
        if (config is not { Enabled: true } || string.IsNullOrWhiteSpace(config.BotNick) ||
            config.BotNick.Length > 128 || !string.Equals(config.BotNick, nick, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(config.UserHostPattern)) return true;
        if (config.UserHostPattern.Length > 256 || string.IsNullOrEmpty(userHost) || userHost.Length > 256) return false;
        return WildcardMatch(config.UserHostPattern, userHost);
    }

    // Matched directly rather than by regex: a cold regex under load blew its 50 ms timeout,
    // and the timeout was answered as "untrusted". Both inputs are bounded at 256 chars.
    private static bool WildcardMatch(string mask, string host)
    {
        int m = 0, h = 0, star = -1, resume = 0;
        while (h < host.Length)
        {
            if (m < mask.Length && mask[m] == '*') { star = m++; resume = h; }
            else if (m < mask.Length && (mask[m] == '?' || char.ToUpperInvariant(mask[m]) == char.ToUpperInvariant(host[h]))) { m++; h++; }
            else if (star >= 0) { m = star + 1; h = ++resume; }
            else return false;
        }
        while (m < mask.Length && mask[m] == '*') m++;
        return m == mask.Length;
    }

    /// <summary>Keep explicit non-NEW labels away from legacy catch-all new-release rules, even if malformed.</summary>
    public static bool IsNonNewMessage(string text, IReadOnlyList<SitebotTemplate>? templates = null)
    {
        if (text.Length > MaximumMessageLength) return false;
        try
        {
            var header = Header.Match(text);
            if (header.Success && Kind(header.Groups["kind"].Value) is { } kind) return kind != SitebotEventKind.New;
            if (templates != null && templates.Count <= 32)
                foreach (var template in templates)
                    if (template is { Kind: not SitebotEventKind.New } && ValidateTemplate(template).Count == 0)
                    {
                        var match = Pattern(template.Pattern).Match(text);
                        if (match.Success && match.Index == 0) return true;
                    }
        }
        catch (RegexMatchTimeoutException) { }
        return false;
    }

    public static IReadOnlyList<string> ValidateTemplate(SitebotTemplate? template)
    {
        if (template == null) return ["template must not be null"];
        if (!Enum.IsDefined(template.Kind)) return ["unknown event kind"];
        if (string.IsNullOrWhiteSpace(template.Pattern) || template.Pattern.Length > 1024)
            return ["template pattern must contain 1–1024 characters"];
        if (!template.Pattern.StartsWith('^') && !template.Pattern.StartsWith(@"\A", StringComparison.Ordinal))
            return ["template pattern must be anchored at the start"];
        try
        {
            var regex = Pattern(template.Pattern);
            var groups = regex.GetGroupNames();
            if (!groups.Contains("release") || !groups.Contains("section")) return ["template needs section and release captures"];
        }
        catch (ArgumentException) { return ["template regex is invalid"]; }
        catch (NotSupportedException) { return ["template regex must support nonbacktracking matching"]; }
        return [];
    }

    private static SitebotEvent? Build(SitebotEventKind kind, Match match, string text)
    {
        string? Capture(string field) => match.Groups[field].Success ? match.Groups[field].Value.Trim() : null;
        var release = Capture("release") ?? "";
        var section = Capture("section") ?? "";
        if (!PlausibleRelease(release) || section.Length is 0 or > 128 || section.Any(char.IsControl)) return null;
        var details = Capture("details") ?? text[match.Groups["release"].Index..];
        var bytes = Capture("bytes") is { } rawBytes ? PositiveLong(rawBytes) : null;
        if (!bytes.HasValue && Capture("size") is { } amount) bytes = Amount(amount, Capture("unit") ?? "MB");
        if (!bytes.HasValue)
            foreach (Match size in Size.Matches(details))
            {
                // Size's delimiter consumes '/'; distinguish a throughput-only field from release bytes.
                var unitEnd = size.Groups["unit"].Index + size.Groups["unit"].Length;
                if (details.AsSpan(unitEnd).StartsWith("/s", StringComparison.OrdinalIgnoreCase)) continue;
                bytes = Amount(size.Groups["size"].Value, size.Groups["unit"].Value);
                if (bytes.HasValue) break;
            }
        var files = PositiveInt(Capture("files") ?? Files.Match(details).Groups["files"].Value, 1_000_000);
        var speed = Speed.Match(details);
        var speedBytes = Capture("speed") is { } explicitSpeed
            ? Amount(explicitSpeed, Capture("speedunit") ?? "MB")
            : Amount(speed.Groups["speed"].Value, speed.Groups["speedunit"].Value);
        var solo = Solo.Match(details);
        var winner = kind == SitebotEventKind.Complete
            ? Capture("winner") ?? (solo.Success ? solo.Groups["winner"].Value : NullIfEmpty(Winner.Match(details).Groups["winner"].Value)) : null;
        var imdb = Capture("imdb") ?? NullIfEmpty(Imdb.Match(details).Groups["imdb"].Value);
        if (imdb != null && !Pattern(@"\Att[0-9]{5,12}\z").IsMatch(imdb)) imdb = null;
        var nukeesText = Capture("nukees") ?? Field(details, "nukees?");
        var nukees = nukeesText?.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length <= 128).Take(64).ToArray() ?? [];
        var genres = (Capture("genres") ?? Field(details, "genres?"))?
            .Split([',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(16).ToArray() ?? [];
        var ratingText = Capture("rating") ?? Rating.Match(details).Groups["rating"].Value;
        var rating = double.TryParse(ratingText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedRating) &&
            parsedRating is >= 0 and <= 10 ? (double?)parsedRating : null;
        var votes = PositiveInt((Capture("votes") ?? Votes.Match(details).Groups["votes"].Value).Replace(",", ""), int.MaxValue);
        return new SitebotEvent
        {
            Kind = kind, Section = section, Release = release, Bytes = bytes, Files = files,
            SpeedBytesPerSecond = speedBytes, Leader = ValidActor(Capture("leader") ?? NullIfEmpty(Leader.Match(details).Groups["leader"].Value)),
            Winner = ValidActor(winner), Solo = kind == SitebotEventKind.Complete && solo.Success,
            Actor = ValidActor(Capture("actor") ?? NullIfEmpty(Actor.Match(details).Groups["actor"].Value)),
            Nukees = nukees, NukeFactor = PositiveInt(Capture("factor") ?? Factor.Match(details).Groups["factor"].Value, 1000),
            Reason = Capture("reason") ?? Field(details, "reason"), ImdbId = imdb,
            Network = Capture("network") ?? Field(details, "network"),
            ShowStatus = Capture("status") ?? Field(details, "status"), Genres = genres, ImdbRating = rating, ImdbVotes = votes
        };
    }

    private static string? Field(string text, string label) => NullIfEmpty(Pattern(@"\b(?:" + label + @")\s*[:=]\s*(?<value>[^\]|>]+)")
        .Match(text).Groups["value"].Value.Trim());
    private static string? ValidActor(string? actor) => actor is { Length: > 0 and <= 128 } &&
        !actor.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? actor : null;
    private static bool PlausibleRelease(string value) => value.Length is >= 10 and <= 512 &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '[' or ']' or '<' or '>') &&
        !value.StartsWith('.') && value.Any(c => c is '.' or '_' || char.IsDigit(c));
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int? PositiveInt(string value, int maximum) => int.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var number) && number is > 0 && number <= maximum ? number : null;
    private static long? PositiveLong(string value) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var number) && number is > 0 and <= (1L << 50) ? number : null;
    private static long? Amount(string value, string unit)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0) return null;
        var factor = unit.ToUpperInvariant() switch
        {
            "B" => 1L, "KB" or "KIB" => 1L << 10, "MB" or "MIB" => 1L << 20,
            "GB" or "GIB" => 1L << 30, "TB" or "TIB" => 1L << 40, _ => 0
        };
        if (factor == 0 || amount > (1L << 50) / (decimal)factor) return null;
        var result = decimal.Truncate(amount * factor);
        return result >= 1 ? (long)result : null;
    }
    private static Regex Pattern(string expression) => new(expression, Options, Timeout);
    private static SitebotEventKind? Kind(string label) => label.ToUpperInvariant() switch
    {
        "NEW" or "NEW RELEASE" => SitebotEventKind.New, "COMPLETE" or "CHECKERED-FLAG" => SitebotEventKind.Complete,
        "CROSSED STICKS" => SitebotEventKind.Progress, "HALFWAY" => SitebotEventKind.Halfway,
        "RACE" => SitebotEventKind.Race, "NUKE" or "NUKED" => SitebotEventKind.Nuke,
        "UNNUKE" or "UNNUKED" => SitebotEventKind.Unnuke, "TV-MAZE" or "IMDB" => SitebotEventKind.Metadata, _ => null
    };
}
