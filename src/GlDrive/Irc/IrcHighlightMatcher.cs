using GlDrive.Config;

namespace GlDrive.Irc;

public static class IrcHighlightMatcher
{
    public static bool SameNick(string? left, string? right) =>
        string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);

    public static bool IsHighlight(string text, string sender, IEnumerable<string> ownNicks, IrcAttentionConfig settings)
    {
        var nicks = ownNicks.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        if (nicks.Any(n => SameNick(n, sender)) || settings.IgnoredNicks.Any(n => SameNick(n, sender))) return false;
        return (settings.NickHighlightsEnabled && nicks.Any(n => ContainsToken(text, n))) ||
            settings.Keywords.Any(word => !string.IsNullOrWhiteSpace(word) && ContainsToken(text, word));
    }

    private static bool ContainsToken(string text, string token)
    {
        text = Fold(text); token = Fold(token);
        for (var offset = 0; offset <= text.Length - token.Length;)
        {
            var at = text.IndexOf(token, offset, StringComparison.Ordinal);
            if (at < 0) return false;
            var end = at + token.Length;
            if ((at == 0 || !IsNickCharacter(text[at - 1])) &&
                (end == text.Length || !IsNickCharacter(text[end]))) return true;
            offset = at + 1;
        }
        return false;
    }

    private static bool IsNickCharacter(char c) => char.IsLetterOrDigit(c) || "_-[]\\`^{}|~".Contains(c);
    // IRC's RFC1459 case mapping also equates square/curly brackets, slash/pipe and caret/tilde.
    internal static string Fold(string? text) => (text ?? "").ToLowerInvariant().Replace('[', '{').Replace(']', '}').Replace('\\', '|').Replace('^', '~');
}
