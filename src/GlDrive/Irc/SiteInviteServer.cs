using GlDrive.Config;

namespace GlDrive.Irc;

/// <summary>A new server built from a siteop's reply, with the login to store and the FiSH keys
/// that cannot ride in a channel's key field.</summary>
public sealed record SiteInviteServerDraft(ServerConfig Server, string? Password,
    IReadOnlyList<(string Channel, string Key, FishMode Mode)> FishKeys, IReadOnlyList<string> Notes);

public static class SiteInviteServer
{
    /// <summary>
    /// Combine what they sent with the addline you sent them. Their reply wins when it names a
    /// login (some siteops pick the password); otherwise the sent addline's login is used.
    /// Not mounted as a drive by default.
    /// </summary>
    public static SiteInviteServerDraft Build(SiteInviteInfo info, AddlineOffer? offer, string fromNick, string? ircNick)
    {
        var notes = new List<string>();
        var username = info.Username ?? offer?.Username ?? "";
        var password = info.Password ?? offer?.Password;
        if (info.Username == null && offer != null) notes.Add($"Login from the addline you sent {fromNick} on {offer.SentAtUtc.ToLocalTime():g}.");
        else if (info.Username != null) notes.Add($"Login from {fromNick}'s reply.");
        else notes.Add("No login found: no addline was sent to this nick from here. Enter the username and password.");
        if (info.AlternateFtp.Count > 0) notes.Add("Other addresses they sent: " + string.Join(", ", info.AlternateFtp));

        var server = new ServerConfig
        {
            Name = !string.IsNullOrWhiteSpace(info.Name) ? info.Name! : info.FtpHost ?? fromNick,
            Enabled = true,
        };
        server.Connection.Host = info.FtpHost ?? "";
        server.Connection.Port = info.FtpPort ?? 21;
        server.Connection.Username = username;
        server.Mount.MountDrive = false;

        var fish = new List<(string, string, FishMode)>();
        if (!string.IsNullOrWhiteSpace(info.IrcHost))
        {
            server.Irc.Enabled = true;
            server.Irc.AutoConnect = true;
            server.Irc.Host = info.IrcHost!;
            server.Irc.Port = info.IrcPort ?? (info.IrcTls ? 6697 : 6667);
            server.Irc.UseTls = info.IrcTls;
            server.Irc.Nick = !string.IsNullOrWhiteSpace(ircNick) ? ircNick! : username;
            if (!string.IsNullOrWhiteSpace(info.InviteBot)) server.Irc.InviteNick = info.InviteBot!;
            foreach (var channel in info.Channels)
            {
                var (fishKey, mode) = FishKey(channel.Blowfish);
                if (fishKey != null) server.Irc.FishEnabled = true;
                string key;
                if (fishKey != null && string.IsNullOrEmpty(channel.Key))
                    key = $"[{(mode == FishMode.CBC ? "cbc" : "ecb")}:{fishKey}]";
                else
                {
                    key = channel.Key ?? "";
                    if (fishKey != null) fish.Add((channel.Name, fishKey, mode));
                }
                server.Irc.Channels.Add(new IrcChannelConfig { Name = channel.Name, Key = key, AutoJoin = true });
            }
        }
        else if (info.Channels.Count > 0)
            notes.Add("Channels were listed but no IRC server address was found; add the IRC server on the IRC tab.");

        return new SiteInviteServerDraft(server, password, fish, notes);
    }

    /// <summary>mIRC FiSH convention: a "cbc:" prefix means CBC, a bare key is ECB.</summary>
    private static (string? Key, FishMode Mode) FishKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, FishMode.ECB);
        return text.StartsWith("cbc:", StringComparison.OrdinalIgnoreCase)
            ? (text[4..], FishMode.CBC)
            : text.StartsWith("ecb:", StringComparison.OrdinalIgnoreCase) ? (text[4..], FishMode.ECB) : (text, FishMode.ECB);
    }
}
