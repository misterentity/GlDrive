using GlDrive.Config;

namespace GlDrive.Irc;

public sealed record ServerInfoShareOptions
{
    public bool Bnc { get; init; } = true;
    public bool FtpLogin { get; init; }
    public bool Ircd { get; init; } = true;
    public bool IrcPassword { get; init; } = true;
    public bool Channels { get; init; } = true;
    public bool FishKeys { get; init; } = true;
}

/// <summary>Secrets resolved by the caller (Credential Manager / FiSH key store) so the builder stays pure.</summary>
public sealed record ServerInfoSecrets(
    string? FtpPassword,
    string? IrcPassword,
    IReadOnlyDictionary<string, FishKeyEntry>? FishKeys);

/// <summary>Formats a site's BNC + IRCd details as short IRC lines for sending in a PM.</summary>
public static class ServerInfoShare
{
    public static IReadOnlyList<string> BuildLines(ServerConfig server, ServerInfoShareOptions options, ServerInfoSecrets secrets)
    {
        var lines = new List<string> { $"[{server.Name}]" };
        var connection = server.Connection;
        if (options.Bnc && !string.IsNullOrWhiteSpace(connection.Host))
            lines.Add($"BNC: {connection.Host}:{connection.Port}");
        if (options.FtpLogin && !string.IsNullOrWhiteSpace(connection.Username))
            lines.Add(string.IsNullOrEmpty(secrets.FtpPassword)
                ? $"Login: {connection.Username}"
                : $"Login: {connection.Username} / {secrets.FtpPassword}");

        var irc = server.Irc;
        if (options.Ircd && !string.IsNullOrWhiteSpace(irc.Host))
        {
            lines.Add(irc.UseTls ? $"IRC: {irc.Host}:+{irc.Port} (SSL)" : $"IRC: {irc.Host}:{irc.Port}");
            if (options.IrcPassword && !string.IsNullOrEmpty(secrets.IrcPassword))
                lines.Add($"IRC pass: {secrets.IrcPassword}");
            if (!string.IsNullOrWhiteSpace(irc.InviteNick))
                lines.Add($"Invite bot: {irc.InviteNick}");
        }

        if (options.Channels)
        {
            foreach (var channel in irc.Channels.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(channel.Key))
                    parts.Add($"key: {channel.Key}");
                if (options.FishKeys && secrets.FishKeys != null
                    && secrets.FishKeys.TryGetValue(channel.Name, out var fish) && !string.IsNullOrEmpty(fish.Key))
                    parts.Add($"blowfish: {FormatFishKey(fish)}");
                lines.Add(parts.Count == 0 ? channel.Name : $"{channel.Name} ({string.Join(", ", parts)})");
            }
        }
        return lines;
    }

    // mIRC FiSH / FiSH10 convention: CBC keys carry a "cbc:" prefix, ECB keys are bare.
    private static string FormatFishKey(FishKeyEntry entry) =>
        entry.Mode == FishMode.CBC && !entry.Key.StartsWith("cbc:", StringComparison.OrdinalIgnoreCase)
            ? "cbc:" + entry.Key
            : entry.Key;
}
