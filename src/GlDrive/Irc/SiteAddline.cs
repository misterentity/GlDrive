namespace GlDrive.Irc;

/// <summary>One glftpd addline: the SITE command to send, and a copy safe to show or log.</summary>
public sealed record SiteAddlineCommand(string Verb, IReadOnlyList<string> Args)
{
    public string Command => $"SITE {Verb} {string.Join(' ', Args)}";

    /// <summary>The command with the new account's password masked.</summary>
    public string Redacted => $"SITE {Verb} {string.Join(' ', Args.Select((a, i) => i == SiteAddline.PasswordIndex(Verb) ? "********" : a))}";

    public string User => Verb == "GADDUSER" ? Args[1] : Args[0];
}

/// <summary>
/// Finds glftpd addlines — <c>SITE ADDUSER user pass [ident@ip...]</c>,
/// <c>SITE GADDUSER group user pass [ident@ip...]</c>, <c>SITE ADDIP user ident@ip...</c> —
/// in IRC text, and validates a hand-edited line before it is sent to a site.
/// Only these three verbs are ever produced: this path must not become a raw-command box.
/// </summary>
public static class SiteAddline
{
    private const int MaxMasks = 10;
    private const int MaxArgLength = 128;

    internal static int PasswordIndex(string verb) => verb switch { "ADDUSER" => 1, "GADDUSER" => 2, _ => -1 };

    private static int RequiredArgs(string verb) => verb switch { "GADDUSER" => 3, _ => 2 };

    /// <summary>Every addline in the given messages, in order, without duplicates.</summary>
    public static IReadOnlyList<SiteAddlineCommand> Extract(IEnumerable<string?> texts)
    {
        var found = new List<SiteAddlineCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                var verb = Verb(words[i]);
                if (verb == null || !Introduced(words, i)) continue;
                if (TryParse(verb, words, i + 1, out var command, out var consumed, allowTrailing: true)
                    && seen.Add(command.Command))
                {
                    found.Add(command);
                    i += consumed;
                }
            }
        }
        return found;
    }

    /// <summary>Validate one line as typed or edited by the user. The <c>SITE</c> prefix is optional.</summary>
    public static bool TryParseLine(string? line, out SiteAddlineCommand command, out string error)
    {
        command = null!;
        var words = (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var start = words.Length > 0 && (words[0].Equals("site", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("/site", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        var verb = start < words.Length ? Verb(words[start]) : null;
        if (verb == null)
        {
            error = "Only SITE ADDUSER, SITE GADDUSER and SITE ADDIP can be sent from here.";
            return false;
        }
        if (!TryParse(verb, words, start + 1, out command, out var consumed, allowTrailing: false)
            || start + 1 + consumed != words.Length)
        {
            error = verb switch
            {
                "GADDUSER" => "Expected: SITE GADDUSER <group> <user> <password> [ident@ip ...]",
                "ADDIP" => "Expected: SITE ADDIP <user> <ident@ip> [ident@ip ...]",
                _ => "Expected: SITE ADDUSER <user> <password> [ident@ip ...]"
            };
            return false;
        }
        error = "";
        return true;
    }

    private static string? Verb(string word)
    {
        var w = word.TrimStart('!', '/').ToUpperInvariant();
        return w is "ADDUSER" or "GADDUSER" or "ADDIP" ? w : null;
    }

    /// <summary>Prose like "can you adduser me" is not an addline: the verb must open the
    /// message, follow "site", carry a ! / slash, or follow a label such as "addline:".</summary>
    private static bool Introduced(string[] words, int i) =>
        i == 0 || words[i][0] is '!' or '/' ||
        words[i - 1].Equals("site", StringComparison.OrdinalIgnoreCase) ||
        words[i - 1].Equals("/site", StringComparison.OrdinalIgnoreCase) ||
        words[i - 1].EndsWith(':');

    private static bool TryParse(string verb, string[] words, int start, out SiteAddlineCommand command,
        out int consumed, bool allowTrailing)
    {
        command = null!;
        consumed = 0;
        var required = RequiredArgs(verb);
        if (words.Length - start < required) return false;
        var args = new List<string>();
        for (var k = 0; k < required; k++)
        {
            var arg = words[start + k];
            if (!SafeArg(arg)) return false;
            // ADDIP's second required argument is the first mask.
            if (verb == "ADDIP" && k == 1 && !IsMask(arg)) return false;
            // A user or group that looks like a mask means the line is out of order.
            if (k != PasswordIndex(verb) && !(verb == "ADDIP" && k == 1) && arg.Contains('@')) return false;
            args.Add(arg);
        }
        var next = start + required;
        while (next < words.Length && args.Count - required < MaxMasks)
        {
            var arg = words[next];
            if (!IsMask(arg) || !SafeArg(arg))
            {
                if (!allowTrailing) return false;
                break;
            }
            args.Add(arg);
            next++;
        }
        consumed = next - start;
        command = new SiteAddlineCommand(verb, args);
        return true;
    }

    private static bool IsMask(string arg)
    {
        var at = arg.IndexOf('@');
        return at >= 0 && at == arg.LastIndexOf('@') && at < arg.Length - 1;
    }

    private static bool SafeArg(string arg) =>
        arg.Length is > 0 and <= MaxArgLength && arg.All(c => c > ' ' && c != 0x7F);
}
