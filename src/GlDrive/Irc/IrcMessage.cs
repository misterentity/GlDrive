namespace GlDrive.Irc;

/// <summary>
/// RFC 1459 IRC message parser.
/// Format: [:prefix] command [params] [:trailing]
/// </summary>
public class IrcMessage
{
    public string? Prefix { get; set; }
    public string Command { get; set; } = "";
    public List<string> Params { get; set; } = [];
    public string? Trailing { get; set; }

    public string Nick { get; private set; } = "";
    public string UserHost { get; private set; } = "";

    public static IrcMessage Parse(string raw)
    {
        var msg = new IrcMessage();
        var pos = 0;

        // Parse prefix
        if (raw.StartsWith(':'))
        {
            var space = raw.IndexOf(' ', 1);
            if (space < 0) { msg.Command = raw[1..]; return msg; }
            msg.Prefix = raw[1..space];
            var bangIdx = msg.Prefix.IndexOf('!');
            if (bangIdx >= 0)
            {
                msg.Nick = msg.Prefix[..bangIdx];
                msg.UserHost = msg.Prefix[(bangIdx + 1)..];
            }
            else
            {
                msg.Nick = msg.Prefix;
            }
            pos = space + 1;
        }

        // Parse command
        var rest = raw[pos..];
        var cmdEnd = rest.IndexOf(' ');
        if (cmdEnd < 0)
        {
            msg.Command = rest;
            return msg;
        }
        msg.Command = rest[..cmdEnd];
        rest = rest[(cmdEnd + 1)..];

        // Parse params and trailing
        while (rest.Length > 0)
        {
            if (rest.StartsWith(':'))
            {
                msg.Trailing = rest[1..];
                break;
            }

            var nextSpace = rest.IndexOf(' ');
            if (nextSpace < 0)
            {
                msg.Params.Add(rest);
                break;
            }

            msg.Params.Add(rest[..nextSpace]);
            rest = rest[(nextSpace + 1)..];
        }

        return msg;
    }

    public string ToRaw()
    {
        var parts = new List<string>();
        if (Prefix != null) parts.Add($":{Prefix}");
        parts.Add(Command);
        parts.AddRange(Params);
        if (Trailing != null) parts.Add($":{Trailing}");
        return string.Join(" ", parts);
    }
}
