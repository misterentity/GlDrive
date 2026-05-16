using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.AiAgent;

/// <summary>
/// Validates AI-suggested changes to a server's IRC request-filler config.
/// Three sub-paths supported:
///   /irc/requestFiller/enabled  — bool
///   /irc/requestFiller/pattern  — regex (must compile; must contain ?&lt;release&gt; capture)
///   /irc/requestFiller/channel  — string (channel name, may be empty)
/// </summary>
public sealed class RequestFillerValidator : IChangeValidator
{
    public string Category => AgentCategories.RequestFiller;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/irc/requestFiller", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);

        return trailing switch
        {
            "/enabled" => ValidateEnabled(change, resolver),
            "/pattern" => ValidatePattern(change, resolver),
            "/channel" => ValidateChannel(change, resolver),
            _ => new(false, "target-shape-unsupported", null),
        };
    }

    private static ValidationResult ValidateEnabled(AgentChange change, Func<AppConfig, ServerConfig?> resolver)
    {
        var afterStr = change.After?.ToString()?.Trim('"').ToLowerInvariant() ?? "";
        if (afterStr != "true" && afterStr != "false")
            return new(false, "after-not-bool", null);
        var newValue = afterStr == "true";
        return new(true, null, cfg =>
        {
            var s = resolver(cfg);
            if (s is null) return;
            s.Irc.RequestFiller.Enabled = newValue;
        });
    }

    private static ValidationResult ValidatePattern(AgentChange change, Func<AppConfig, ServerConfig?> resolver)
    {
        var pattern = change.After?.ToString()?.Trim('"') ?? "";
        if (string.IsNullOrWhiteSpace(pattern))
            return new(false, "after-empty", null);

        // Pattern must compile.
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch { return new(false, "regex-invalid", null); }

        // Pattern must contain a named "release" capture group — the runtime
        // request-filler reads match.Groups["release"].Value.
        if (!pattern.Contains("(?<release>", StringComparison.OrdinalIgnoreCase) &&
            !pattern.Contains("(?'release'", StringComparison.OrdinalIgnoreCase))
            return new(false, "missing-release-capture", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg);
            if (s is null) return;
            s.Irc.RequestFiller.Pattern = pattern;
        });
    }

    private static ValidationResult ValidateChannel(AgentChange change, Func<AppConfig, ServerConfig?> resolver)
    {
        // Channel can be empty (= all channels) but must not contain whitespace
        // or shell-y characters. IRC channels typically start with # or &.
        var chan = change.After?.ToString()?.Trim('"') ?? "";
        if (chan.Length > 64 || chan.IndexOfAny([' ', '\t', '\n', '\r']) >= 0)
            return new(false, "channel-malformed", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg);
            if (s is null) return;
            s.Irc.RequestFiller.Channel = chan;
        });
    }
}
