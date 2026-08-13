using System;
using System.Text;

namespace GlDrive.Irc;

/// <summary>
/// Strips credential-bearing parameters out of an outbound IRC line before it is written
/// to the Verbose trace log.
///
/// Why this exists: <c>SendRawAsync</c> used to special-case exactly one command
/// (<c>PASS</c>), so <c>JOIN #chan &lt;key&gt;</c> wrote the FiSH channel key — the key the
/// project deliberately keeps DPAPI-encrypted in fish-keys-{serverId}.json — as cleartext
/// into gldrive-{date}.log. Enabling Verbose logging to diagnose an IRC problem would have
/// dumped every channel key and every services password to disk.
///
/// The rule this keys on is "which PARAMETER of this command is a credential", not "which
/// command have we seen leak" — an enumeration of observed cases is what left the gap
/// (same failure shape as the zipscript sidecar filter, v3.10.44).
///
/// Redaction is applied to the LOGGED copy only; the line sent to the server is untouched.
/// </summary>
internal static class IrcLineRedactor
{
    internal const string Mask = "[REDACTED]";

    /// <summary>
    /// Trailing text whose FIRST word is one of these is a credential exchange with a
    /// services bot, whatever the target nick happens to be called on this network.
    /// Keying on the verb rather than the target covers NickServ, X, AuthServ, custom
    /// site bots, and /quote-style hand-typed commands alike.
    /// </summary>
    private static readonly string[] CredentialVerbs =
    [
        "identify", "id", "login", "auth", "register", "pass", "password",
        "ghost", "recover", "release", "regain", "sidentify", "setpass"
    ];

    /// <summary>Commands whose parameters are credentials from the given index onward.</summary>
    private static int CredentialParamsFrom(string command) => command.ToUpperInvariant() switch
    {
        // PASS <password>
        "PASS" => 0,
        // OPER <name> <password>
        "OPER" => 1,
        // JOIN <channels> [<keys>] — the channel list is not secret, the key list is.
        "JOIN" => 1,
        // Services aliases: every parameter is part of the credential exchange.
        "NICKSERV" or "NS" or "CHANSERV" or "CS" or "AUTHSERV" or "AS"
            or "AUTH" or "IDENTIFY" or "SQUERY" => 0,
        _ => -1
    };

    /// <summary>
    /// Returns a copy of <paramref name="line"/> safe to write to the log. Never throws —
    /// a redactor that fails must not take the send path down with it, and on any doubt it
    /// returns a fully masked line rather than the original.
    /// </summary>
    internal static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? "";

        try
        {
            // Split off the trailing parameter (":" onwards) first — it may legitimately
            // contain spaces, and for PRIVMSG/NOTICE it is where a password would sit.
            var trailingStart = FindTrailingStart(line);
            var head = trailingStart >= 0 ? line[..trailingStart] : line;
            var trailing = trailingStart >= 0 ? line[(trailingStart + 1)..] : null;

            var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return line;

            // Inbound lines carry a ":prefix" first; the command is the next token. This
            // matters because the server echoes channel keys back at us in MODE and in
            // 324 RPL_CHANNELMODEIS.
            var cmdIndex = parts[0].StartsWith(':') ? 1 : 0;
            if (cmdIndex >= parts.Length) return line;

            var command = parts[cmdIndex];
            var sb = new StringBuilder();
            for (var i = 0; i <= cmdIndex; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(parts[i]);
            }

            var from = IsKeyBearingModeLine(parts, cmdIndex)
                ? ModeKeyParamIndex(parts, cmdIndex)
                : CredentialParamsFrom(command);

            for (var i = cmdIndex + 1; i < parts.Length; i++)
            {
                var isCredential = from >= 0 && (i - cmdIndex - 1) >= from;
                sb.Append(' ').Append(isCredential ? Mask : parts[i]);
            }

            if (trailing != null)
            {
                var trailingIsCredential =
                    // "NICKSERV :identify hunter2" — command itself is a credential exchange
                    (from == 0 && !IsMessageCommand(command))
                    // "PRIVMSG NickServ :identify hunter2" — verb marks it, target-agnostic
                    || StartsWithCredentialVerb(trailing);

                sb.Append(" :").Append(trailingIsCredential ? Mask : trailing);
            }

            return sb.ToString();
        }
        catch
        {
            // Unparseable input is exactly when a naive logger leaks. Fail closed.
            return Mask;
        }
    }

    /// <summary>
    /// True for a channel-mode line that carries a key: outbound <c>MODE #chan +k secret</c>
    /// and the server's own echo, <c>:server 324 nick #chan +k secret</c>. Both put the FiSH
    /// channel key on the wire in cleartext, so both must be masked in the trace log.
    /// </summary>
    private static bool IsKeyBearingModeLine(string[] parts, int cmdIndex)
    {
        var command = parts[cmdIndex];
        if (!command.Equals("MODE", StringComparison.OrdinalIgnoreCase) && command != "324")
            return false;

        return FindModeTokenIndex(parts, cmdIndex) >= 0;
    }

    /// <summary>
    /// Everything after the mode string is masked when that mode string sets or clears a key.
    /// Over-masking a companion parameter (a +l limit, say) costs nothing in a diagnostic
    /// log; working out each mode letter's argument arity to mask one token precisely would
    /// be a second place to get wrong.
    /// </summary>
    private static int ModeKeyParamIndex(string[] parts, int cmdIndex)
    {
        var modeToken = FindModeTokenIndex(parts, cmdIndex);
        return modeToken < 0 ? -1 : modeToken - cmdIndex; // == (modeToken - cmdIndex - 1) + 1
    }

    private static int FindModeTokenIndex(string[] parts, int cmdIndex)
    {
        for (var i = cmdIndex + 1; i < parts.Length; i++)
        {
            var token = parts[i];
            if (token.Length < 2 || (token[0] != '+' && token[0] != '-')) continue;
            if (token.IndexOf('k', StringComparison.OrdinalIgnoreCase) > 0) return i;
        }
        return -1;
    }

    private static bool IsMessageCommand(string command) =>
        command.Equals("PRIVMSG", StringComparison.OrdinalIgnoreCase)
        || command.Equals("NOTICE", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithCredentialVerb(string trailing)
    {
        var trimmed = trailing.TrimStart();
        if (trimmed.Length == 0) return false;

        var space = trimmed.IndexOf(' ');
        var first = space < 0 ? trimmed : trimmed[..space];

        // A bare verb with no argument carries nothing worth hiding ("PRIVMSG bob :login?").
        if (space < 0) return false;

        foreach (var verb in CredentialVerbs)
            if (first.Equals(verb, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Index of the ':' that introduces the trailing parameter, or -1. A ':' only starts the
    /// trailing parameter at the beginning of a parameter, so it must follow a space.
    /// </summary>
    private static int FindTrailingStart(string line)
    {
        for (var i = 1; i < line.Length; i++)
            if (line[i] == ':' && line[i - 1] == ' ')
                return i;
        return -1;
    }
}
