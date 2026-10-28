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
    private readonly IrcService? _ircService;
    private readonly List<IrcAnnounceRule> _rules;
    private readonly AnnounceMatcher _matcher;
    private readonly LinkedList<string> _recentAnnounceOrder = new();
    private readonly HashSet<string> _recentAnnounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recentChannelAnnounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentChannelOrder = new();
    private readonly Lock _lock = new();
    private readonly bool _defaultAutoRace;
    private readonly IReadOnlyList<IrcChannelConfig> _channels;
    private readonly Action<GlDrive.AiAgent.MatchedAnnounceEvent> _recordMatchedAnnounce;

    /// <summary>Passive source-health annotation; does not change announce matching or admission.</summary>
    public Func<bool>? SourceDown { get; set; }

    internal static bool IsPlausibleReleaseName(string? candidate) =>
        AnnounceMatcher.IsPlausibleReleaseName(candidate);

    public event Action<string, string, string, bool>? ReleaseAnnounced; // serverId, section, releaseName, autoRace
    public event Action<SitebotEvent>? SitebotEventReceived;
    /// <summary>Plausible matched traffic, retaining channel identity independently of race deduplication.</summary>
    public event Action<string, string, DateTime>? ChannelAnnounceObserved;

    public IrcAnnounceListener(string serverId, IrcService ircService,
        List<IrcAnnounceRule> rules, bool defaultAutoRace, IReadOnlyList<IrcChannelConfig>? channels = null)
        : this(serverId, rules, defaultAutoRace, channels)
    {
        _ircService = ircService;
        _ircService.MessageReceived += OnMessage;
    }

    // A pure receive seam avoids constructing IRC stores or sockets in parser tests.
    internal IrcAnnounceListener(string serverId, List<IrcAnnounceRule> rules, bool defaultAutoRace,
        IReadOnlyList<IrcChannelConfig>? channels = null,
        Action<GlDrive.AiAgent.MatchedAnnounceEvent>? recordMatchedAnnounce = null)
    {
        _serverId = serverId;
        _rules = rules;
        _defaultAutoRace = defaultAutoRace;
        _channels = channels ?? [];
        _recordMatchedAnnounce = recordMatchedAnnounce ?? (row =>
            App.TelemetryRecorder?.Record(GlDrive.AiAgent.TelemetryStream.MatchedAnnounces, row));

        _matcher = new AnnounceMatcher(rules, defaultAutoRace);
        foreach (var pattern in _matcher.InvalidPatterns)
            Log.Warning("Invalid IRC announce pattern: {Pattern}", pattern);

        // Always subscribe — the built-in verbose pattern works without any custom rules.
        // Without this, users must configure announce rules before the announce pipeline
        // becomes live, which defeats the purpose of shipping a built-in pattern.
        Log.Information("IRC announce listener active for {Server} ({Count} custom rules, builtin={Builtin})",
            _serverId, _matcher.CompiledCount, _defaultAutoRace ? "autoRace" : "logOnly");
    }

    private int _traceCount;

    internal void OnMessage(string target, IrcMessageItem message)
    {
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice)
            return;

        // Announces only ever arrive on channels. Ignore PMs (and the "*" status window):
        // a bracketed FiSH-decrypted PM could otherwise false-trigger an auto-race or, via
        // the no-match path below, write PM plaintext into the plaintext ai-data telemetry.
        if (!IrcLogStore.IsChannelName(target))
            return;

        var channel = _channels.FirstOrDefault(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (channel?.Sitebot is { Enabled: true } sitebot && !string.IsNullOrWhiteSpace(sitebot.BotNick))
        {
            // Configuring channel trust also protects its legacy NEW rules. Empty trust
            // retains existing configuration behavior without learning a sender implicitly.
            if (!SitebotEventParser.IsTrusted(sitebot, message.Nick, message.UserHost)) return;
            var observation = SitebotEventParser.ParseTrusted(_serverId, target, message.Nick, message.UserHost,
                message.Text, sitebot, message.Timestamp.ToUniversalTime());
            if (observation != null)
            {
                if (IsPlausibleReleaseName(observation.Release))
                    ObserveChannelActivity(target, observation.Kind.ToString(), observation.Section, observation.Release, observation.ObservedAtUtc);
                try { SitebotEventReceived?.Invoke(observation); }
                catch (Exception ex) { Log.Warning(ex, "Sitebot event consumer failed for {Server}", _serverId); }
            }
            // Completion, metadata and removal notices must not become NEW races
            // through a broad legacy section/release regex.
            if (observation is { Kind: not SitebotEventKind.New } ||
                SitebotEventParser.IsNonNewMessage(message.Text, sitebot.Templates)) return;
        }

        // IrcService has already removed IRC formatting. Reject removal events
        // before either matcher, deduplication or announce-learning telemetry.
        if (AnnounceMatcher.IsRemoval(message.Text))
        {
            Log.Debug("IRC announce: ignoring removal event from {Channel}", target);
            return;
        }

        // Trace first 5 messages per channel to diagnose matching issues
        if (_traceCount < 20 && target.StartsWith('#'))
        {
            _traceCount++;
            Log.Information("Announce trace [{Channel}] nick={Nick} text={Text}",
                target, message.Nick, message.Text[..Math.Min(150, message.Text.Length)]);
        }

        foreach (var candidate in _matcher.Enumerate(target, message.Text))
        {
            if (candidate.RuleSource == "builtin")
            {
                if (TryFireAnnounce(candidate.Section, candidate.Release, target, message.Text,
                    candidate.AutoRace, candidate.RuleSource, message.Timestamp.ToUniversalTime())) return;
            }
            else
            {
                try
                {
                    if (TryFireAnnounce(candidate.Section, candidate.Release, target, message.Text,
                        candidate.AutoRace, candidate.RuleSource, message.Timestamp.ToUniversalTime())) return;
                }
                catch (Exception ex) { Log.Debug(ex, "IRC announce match error"); }
            }
        }

        // No rule matched — emit telemetry if the message looks announce-y so the nightly
        // agent can detect regex drift over time.
        if (LooksAnnouncey(message.Text))
        {
            try
            {
                var (near, dist) = NearestRule(message.Text, _rules);
                App.TelemetryRecorder?.Record(GlDrive.AiAgent.TelemetryStream.AnnouncesNoMatch,
                    new GlDrive.AiAgent.AnnounceNoMatchEvent
                    {
                        ServerId = _serverId,
                        Channel = target,
                        BotNick = message.Nick ?? "",
                        Message = message.Text.Length > 512 ? message.Text[..512] : message.Text,
                        NearestRulePattern = near,
                        NearestRuleDistance = dist
                    });
            }
            catch (Exception ex) { Log.Debug(ex, "AnnounceNoMatch emit failed"); }
        }
    }

    private static bool LooksAnnouncey(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message;
        return (m.Contains('[') && m.Contains(']')) ||
               m.Contains("NEW ", StringComparison.OrdinalIgnoreCase) ||
               m.Contains(" pre'd", StringComparison.OrdinalIgnoreCase) ||
               m.Contains(" pre ", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("Release", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? pattern, int? distance) NearestRule(
        string message, IReadOnlyList<IrcAnnounceRule> rules)
    {
        if (rules.Count == 0) return (null, null);
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            if (string.IsNullOrEmpty(r.Pattern)) continue;
            var d = LevenshteinLimited(message, r.Pattern, 200);
            if (d < bestDist) { bestDist = d; best = r.Pattern; }
        }
        return (best, bestDist == int.MaxValue ? null : bestDist);
    }

    private static int LevenshteinLimited(string a, string b, int max)
    {
        if (a.Length == 0) return Math.Min(b.Length, max);
        if (b.Length == 0) return Math.Min(a.Length, max);
        var dp = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) dp[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            var prev = dp[0]; dp[0] = i; var rowMin = dp[0];
            for (int j = 1; j <= b.Length; j++)
            {
                var tmp = dp[j];
                dp[j] = a[i - 1] == b[j - 1]
                    ? prev
                    : 1 + Math.Min(prev, Math.Min(dp[j], dp[j - 1]));
                prev = tmp; rowMin = Math.Min(rowMin, dp[j]);
            }
            if (rowMin >= max) return max;
        }
        return dp[b.Length];
    }

    private bool TryFireAnnounce(string section, string release, string channel, string msgText, bool autoRace, string ruleSource, DateTime observedAtUtc)
    {
        // Final validation gate — covers both the built-in verbose pattern and custom rules.
        // Rejects junk like "brings", "leads", "racing" that loose regexes capture out of
        // racing-chatter messages (e.g. "TeRRaNoVA brings the goods @ 8.39MB/s").
        if (!IsPlausibleReleaseName(release))
        {
            Log.Debug("IRC announce: rejecting implausible release name '{Release}' from {Channel}: {Msg}",
                release, channel, msgText);
            return false;
        }

        var dedupeKey = $"{section}|{release}";
        ObserveChannelActivity(channel, nameof(SitebotEventKind.New), section, release, observedAtUtc);
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

        // section→folder learning: record every MATCHED announce as a learning signal
        // (mirror of the AnnounceNoMatch emit above). Captures the parsed release shape
        // so the nightly agent can aggregate (release-type, section) -> observed folder.
        // Wrapped so telemetry can never break announce handling.
        try
        {
            var parsed = GlDrive.Downloads.SceneNameParser.Parse(release);
            // ParsedRelease has no explicit category field — derive a coarse type from
            // its structure: season/episode => tv, year-only => movie, else other.
            var parsedType = parsed.Season != null || parsed.Episode != null || parsed.IsSeasonPack
                ? "tv"
                : parsed.Year != null ? "movie" : "other";
            _recordMatchedAnnounce(
                new GlDrive.AiAgent.MatchedAnnounceEvent
                {
                    ServerId = _serverId,
                    Channel = channel,
                    Section = section,
                    Release = release,
                    ParsedType = parsedType,
                    Quality = parsed.Quality.ToString(),
                    Source = parsed.Source ?? "",
                    Group = parsed.Group ?? "",
                    RuleSource = ruleSource,
                    AutoRace = autoRace,
                    SourceDown = IsSourceDown()
                });
        }
        catch (Exception ex) { Log.Debug(ex, "MatchedAnnounce emit failed"); }

        ReleaseAnnounced?.Invoke(_serverId, section, release, autoRace);
        return true;
    }

    private bool IsSourceDown()
    {
        try { return SourceDown?.Invoke() == true; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Announce source health annotation unavailable");
            return false;
        }
    }

    private void ObserveChannelActivity(string channel, string kind, string? section, string release, DateTime observedAtUtc)
    {
        lock (_lock)
        {
            // One received message can match both typed and legacy NEW parsers.
            // Later progress/completion traffic still proves the channel is alive.
            var key = $"{channel}|{kind}|{section}|{release}|{observedAtUtc.Ticks}";
            if (!_recentChannelAnnounces.Add(key)) return;
            _recentChannelOrder.Enqueue(key);
            while (_recentChannelOrder.Count > 2000) _recentChannelAnnounces.Remove(_recentChannelOrder.Dequeue());
        }
        var observers = ChannelAnnounceObserved;
        if (observers == null) return;
        foreach (Action<string, string, DateTime> observer in observers.GetInvocationList())
            try { observer(_serverId, channel, observedAtUtc); }
            catch (Exception ex) { Log.Warning(ex, "Announce health observer failed for {Server}", _serverId); }
    }

    public void Dispose()
    {
        if (_ircService != null) _ircService.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}
