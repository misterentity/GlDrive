using System.Text.RegularExpressions;

namespace GlDrive.Irc;

public sealed record SiteInviteChannel(string Name, string? Key, string? Blowfish);

/// <summary>What a siteop sent back after adding you: where the site is, and its IRC.</summary>
public sealed record SiteInviteInfo(
    string? Name,
    string? FtpHost, int? FtpPort, IReadOnlyList<string> AlternateFtp,
    string? IrcHost, int? IrcPort, bool IrcTls, string? IrcPassword,
    IReadOnlyList<SiteInviteChannel> Channels, string? InviteBot,
    string? Username, string? Password)
{
    public bool HasFtp => !string.IsNullOrWhiteSpace(FtpHost) && FtpPort is > 0 and <= 65535;
}

/// <summary>
/// Reads the free-form lines a siteop pastes ("bnc: 1.2.3.4:1234", "irc.net.org +6697",
/// "#chan key: x blowfish: y", "Login: user / pass") — including GlDrive's own
/// "Send server info" format. Anything not recognised is ignored; the user reviews the result
/// in the Add Server form before anything is saved.
/// </summary>
public static partial class SiteInviteParser
{
    [GeneratedRegex(@"(?<![\w.:-])(?<host>(?:\d{1,3}\.){3}\d{1,3}|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,24})(?<sep>\s*:\s*|\s+(?:port\s+)?)(?<plus>\+)?(?<port>\d{2,5})(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex HostPortRegex();

    [GeneratedRegex(@"(?<![\w])(?<name>[#&][^\s,()\[\]]+)")]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"\b(?:blowfish|fish|bf|fishkey|blow)\s*(?:key)?\s*[:=]?\s*(?<key>(?:cbc:)?[^\s,()]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BlowfishRegex();

    [GeneratedRegex(@"\b(?:chan(?:nel)?\s*)?key\s*[:=]?\s*(?<key>[^\s,()]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelKeyRegex();

    [GeneratedRegex(@"\b(?:login|user(?:name)?)\s*[:=]\s*(?<user>[^\s/,]+)(?:\s*(?:/|,|\s)\s*(?:pass(?:word)?\s*[:=]\s*)?(?<pass>[^\s,]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRegex();

    [GeneratedRegex(@"\bpass(?:word)?\s*[:=]\s*(?<pass>[^\s,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"\b(?:invite\s*bot|sitebot|bot)\s*[:=]\s*(?<nick>[^\s,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InviteBotRegex();

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]{1,40})\]\s*$|\b(?:site\s*name|sitename|name)\s*[:=]\s*(?<name2>[^\s,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    public static SiteInviteInfo Parse(IEnumerable<string?> lines)
    {
        string? name = null, ircHost = null, ircPassword = null, inviteBot = null, user = null, pass = null;
        int? ircPort = null;
        var ircTls = false;
        var ftp = new List<(string Host, int Port)>();
        var channels = new List<SiteInviteChannel>();

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();

            if (name == null && NameRegex().Match(line) is { Success: true } n)
                name = (n.Groups["name"].Success ? n.Groups["name"].Value : n.Groups["name2"].Value).Trim();

            var isIrcLine = Regex.IsMatch(lower, @"\birc(?:d)?\b|\birc\.|\bnetwork\b|\bserver\s*:\s*irc");
            if (lower.Contains("irc") && PasswordRegex().Match(line) is { Success: true } ircPass &&
                Regex.IsMatch(lower, @"irc\s*pass"))
                ircPassword = ircPass.Groups["pass"].Value;
            else if (LoginRegex().Match(line) is { Success: true } login && !isIrcLine)
            {
                user = login.Groups["user"].Value;
                if (login.Groups["pass"].Success) pass = login.Groups["pass"].Value;
            }
            else if (!isIrcLine && PasswordRegex().Match(line) is { Success: true } p)
                pass = p.Groups["pass"].Value;

            if (InviteBotRegex().Match(line) is { Success: true } bot) inviteBot = bot.Groups["nick"].Value;

            foreach (Match hp in HostPortRegex().Matches(line))
            {
                if (!int.TryParse(hp.Groups["port"].Value, out var port) || port is < 1 or > 65535) continue;
                var host = hp.Groups["host"].Value;
                if (FileExtensions.Contains(host[(host.LastIndexOf('.') + 1)..])) continue;
                // "host 1234" (no colon) only counts for an IP or a labelled line, never prose.
                var colon = hp.Groups["sep"].Value.Contains(':');
                var isIp = char.IsDigit(host[^1]);
                if (!colon && !isIp && !isIrcLine && !Regex.IsMatch(lower, @"\b(?:bnc|site|ftp|host|addr|address|ip|port|server)\b")) continue;
                if (IsIrcHost(host, isIrcLine))
                {
                    if (ircHost == null)
                    {
                        ircHost = host; ircPort = port;
                        ircTls = hp.Groups["plus"].Success || lower.Contains("ssl") || lower.Contains("tls") || port is 6697 or 7000 or 9999;
                    }
                }
                else if (!ftp.Contains((host, port))) ftp.Add((host, port));
            }

            var lineChannels = ChannelRegex().Matches(line).Select(m => m.Groups["name"].Value.TrimEnd('.', ':')).ToList();
            var blowfish = BlowfishRegex().Match(line) is { Success: true } bf ? bf.Groups["key"].Value : null;
            var keyText = BlowfishRegex().Replace(line, " ");
            var key = ChannelKeyRegex().Match(keyText) is { Success: true } k ? k.Groups["key"].Value : null;
            if (lineChannels.Count > 0)
            {
                foreach (var channel in lineChannels.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    channels.RemoveAll(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase));
                    // A key/blowfish on a line naming several channels applies to the first only.
                    var first = channel == lineChannels[0];
                    channels.Add(new SiteInviteChannel(channel, first ? key : null, first ? blowfish : null));
                }
            }
            else if (blowfish != null && channels.Count > 0)
                channels[^1] = channels[^1] with { Blowfish = blowfish };
        }

        var primary = ftp.Count > 0 ? ftp[0] : default;
        return new SiteInviteInfo(name,
            ftp.Count > 0 ? primary.Host : null, ftp.Count > 0 ? primary.Port : null,
            ftp.Skip(1).Select(f => $"{f.Host}:{f.Port}").ToArray(),
            ircHost, ircPort, ircTls, ircPassword, channels, inviteBot, user, pass);
    }

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfo", "sfv", "rar", "zip", "mkv", "mp4", "avi", "mp3", "flac", "txt", "jpg", "jpeg", "png", "gif",
        "iso", "nzb", "srt", "sub", "idx", "exe", "dll", "log", "diz", "m3u", "cue", "par2", "torrent", "html"
    };

    private static bool IsIrcHost(string host, bool ircLine) =>
        ircLine || host.StartsWith("irc.", StringComparison.OrdinalIgnoreCase);
}
