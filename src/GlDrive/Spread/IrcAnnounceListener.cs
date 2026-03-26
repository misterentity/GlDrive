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
    private readonly HashSet<string> _recentAnnounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<string, string, string, bool>? ReleaseAnnounced; // serverId, section, releaseName, autoRace

    public IrcAnnounceListener(string serverId, IrcService ircService, List<IrcAnnounceRule> rules)
    {
        _serverId = serverId;
        _ircService = ircService;
        _rules = rules;

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

        if (_compiledRules.Count > 0)
        {
            _ircService.MessageReceived += OnMessage;
            Log.Information("IRC announce listener active for {Server} with {Count} rules",
                _serverId, _compiledRules.Count);
        }
    }

    private void OnMessage(string target, IrcMessageItem message)
    {
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice)
            return;

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

                var section = match.Groups["section"].Success ? match.Groups["section"].Value : "";
                var release = match.Groups["release"].Success ? match.Groups["release"].Value : "";

                if (string.IsNullOrEmpty(release)) continue;

                // Deduplicate — don't fire for same release within short window
                var dedupeKey = $"{section}|{release}";
                lock (_lock)
                {
                    if (!_recentAnnounces.Add(dedupeKey)) continue;

                    // Trim old entries (keep last 500)
                    if (_recentAnnounces.Count > 500)
                        _recentAnnounces.Clear();
                }

                Log.Information("IRC announce detected: [{Section}] {Release} (from {Channel} on {Server})",
                    section, release, target, _serverId);

                ReleaseAnnounced?.Invoke(_serverId, section, release, rule.AutoRace);
            }
            catch (RegexMatchTimeoutException) { }
            catch (Exception ex)
            {
                Log.Debug(ex, "IRC announce match error");
            }
        }
    }

    public void Dispose()
    {
        _ircService.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}
