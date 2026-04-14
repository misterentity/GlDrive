using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Listens to IRC messages and detects new release announces.
/// When a message matches an announce rule, fires ReleaseAnnounced.
///
/// Common glftpd announce patterns (configured per server):
///   (?&lt;section&gt;\w+) :: (?&lt;release&gt;\S+)
///   \[(?&lt;section&gt;[^\]]+)\]\s*(?&lt;release&gt;\S+)
///   NEW in (?&lt;section&gt;\w+): (?&lt;release&gt;\S+)
/// </summary>
public class IrcAnnounceListener : IDisposable
{
    private readonly string _serverId;
    private readonly IrcService _ircService;
    private readonly List<IrcAnnounceRule> _rules;
    private readonly Dictionary<string, Regex> _compiledRules = new();
    private readonly LinkedList<string> _recentAnnounceOrder = new();
    private readonly HashSet<string> _recentAnnounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly bool _defaultAutoRace;

    // Built-in pattern for common glftpd verbose announces:
    //   [ NEW ] in [ section ] Release.Name OK pred 2s ago.
    //   [ CHECKERED-FLAG ] in [ section ] Release.Name [ stats ]
    private static readonly Regex VerboseAnnouncePattern = new(
        @"\[ NEW \] in \[ (?<section>[^\]]+?) \] (?<release>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public event Action<string, string, string, bool>? ReleaseAnnounced; // serverId, section, releaseName, autoRace

    public IrcAnnounceListener(string serverId, IrcService ircService,
        List<IrcAnnounceRule> rules, bool defaultAutoRace)
    {
        _serverId = serverId;
        _ircService = ircService;
        _rules = rules;
        _defaultAutoRace = defaultAutoRace;

        // Pre-compile regex patterns
        foreach (var rule in rules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Pattern)))
        {
            try
            {
                _compiledRules[rule.Channel + "|" + rule.Pattern] = new Regex(rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Invalid IRC announce pattern: {Pattern}", rule.Pattern);
            }
        }

        // Always subscribe — the built-in verbose pattern works without any custom rules.
        // Without this, users must configure announce rules before the announce pipeline
        // becomes live, which defeats the purpose of shipping a built-in pattern.
        _ircService.MessageReceived += OnMessage;
        Log.Information("IRC announce listener active for {Server} ({Count} custom rules, builtin={Builtin})",
            _serverId, _compiledRules.Count, _defaultAutoRace ? "autoRace" : "logOnly");
    }

    private int _traceCount;

    private void OnMessage(string target, IrcMessageItem message)
    {
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice)
            return;

        // Trace first 5 messages per channel to diagnose matching issues
        if (_traceCount < 20 && target.StartsWith('#'))
        {
            _traceCount++;
            Log.Information("Announce trace [{Channel}] nick={Nick} text={Text}",
                target, message.Nick, message.Text[..Math.Min(150, message.Text.Length)]);
        }

        // Try built-in verbose pattern first: [ NEW ] in [ section ] Release.Name ...
        // Only match [ NEW ], skip [ CHECKERED-FLAG ], [ CROSSED STICKS ] etc.
        var verboseMatch = VerboseAnnouncePattern.Match(message.Text);
        if (verboseMatch.Success)
        {
            var section = verboseMatch.Groups["section"].Value.Trim();
            var release = verboseMatch.Groups["release"].Value;

            // If user has enabled rules for this channel, prefer their autoRace setting.
            // Otherwise fall back to the global default (from SpreadConfig.AutoRaceOnNotification)
            // so the built-in pattern is functional without rule configuration.
            var matchingRule = _rules.FirstOrDefault(r => r.Enabled &&
                (string.IsNullOrEmpty(r.Channel) || target.Equals(r.Channel, StringComparison.OrdinalIgnoreCase)));
            var autoRace = matchingRule?.AutoRace ?? _defaultAutoRace;

            if (!string.IsNullOrEmpty(release) && release.Length >= 5)
            {
                if (TryFireAnnounce(section, release, target, message.Text, autoRace))
                    return;
            }
        }

        foreach (var rule in _rules.Where(r => r.Enabled))
        {
            // Check channel match (empty = all channels)
            if (!string.IsNullOrEmpty(rule.Channel) &&
                !target.Equals(rule.Channel, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = rule.Channel + "|" + rule.Pattern;
            if (!_compiledRules.TryGetValue(key, out var regex))
                continue;

            try
            {
                var match = regex.Match(message.Text);
                if (!match.Success) continue;

                var section = match.Groups["section"].Success ? match.Groups["section"].Value.Trim() : "";
                var release = match.Groups["release"].Success ? match.Groups["release"].Value : "";

                if (string.IsNullOrEmpty(release)) continue;

                // Basic validation: release names shouldn't be common words
                if (release.Length < 5 || release is "in" or "the" or "from" or "to" or "by" or "at")
                {
                    Log.Debug("IRC announce skipped (invalid release name): [{Section}] {Release} from msg: {Msg}",
                        section, release, message.Text);
                    continue;
                }

                if (TryFireAnnounce(section, release, target, message.Text, rule.AutoRace))
                    return;
            }
            catch (RegexMatchTimeoutException) { }
            catch (Exception ex)
            {
                Log.Debug(ex, "IRC announce match error");
            }
        }
    }

    private bool TryFireAnnounce(string section, string release, string channel, string msgText, bool autoRace)
    {
        var dedupeKey = $"{section}|{release}";
        lock (_lock)
        {
            if (!_recentAnnounces.Add(dedupeKey)) return false;
            _recentAnnounceOrder.AddLast(dedupeKey);
            while (_recentAnnounces.Count > 500 && _recentAnnounceOrder.First != null)
            {
                _recentAnnounces.Remove(_recentAnnounceOrder.First.Value);
                _recentAnnounceOrder.RemoveFirst();
            }
        }

        Log.Information("IRC announce detected: [{Section}] {Release} (from {Channel}, msg: {Msg})",
            section, release, channel, msgText);

        ReleaseAnnounced?.Invoke(_serverId, section, release, autoRace);
        return true;
    }

    public void Dispose()
    {
        _ircService.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}
