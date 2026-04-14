using System.Text.RegularExpressions;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Watches IRC channel messages over time, identifies bot announce patterns,
/// and suggests regex rules for auto-racing.
///
/// Detection strategy:
/// 1. Buffer messages per channel per nick
/// 2. Identify bots: nicks that post frequently with consistent structure
/// 3. Find messages containing scene release names (Word.Word-GROUP or Word-Word-GROUP)
/// 4. Extract the fixed framing around the release name
/// 5. Build a regex with named groups for section and release
/// </summary>
public class IrcPatternDetector : IDisposable
{
    private readonly IrcService _ircService;
    private readonly string _serverId;
    private readonly Lock _lock = new();

    // channel -> nick -> message queue
    private readonly Dictionary<string, Dictionary<string, Queue<string>>> _buffer = new(StringComparer.OrdinalIgnoreCase);

    // Scene release name pattern: at least 3 segments with dots/dashes, ends with -GROUP
    private static readonly Regex SceneNameRegex = new(
        @"(?<release>(?:[A-Za-z0-9]+[\._-]){2,}[A-Za-z0-9]+(?:-[A-Za-z0-9]+)?)",
        RegexOptions.Compiled);

    // Common section names in glftpd
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP3", "FLAC", "0DAY", "APPS", "GAMES", "TV", "TV-HD", "TV-SD", "TV-X264", "TV-X265",
        "MOVIES", "MOVIE", "X264", "X265", "XVID", "DVDR", "BLURAY", "BLU-RAY",
        "XXX", "EBOOK", "AUDIOBOOK", "MVID", "MDVDR", "NSW", "PS5", "XBOX",
        "ISO", "LINUX", "MAC", "PDA", "SUBPACK", "SUBS", "ANIME",
        "PRE", "REQUEST", "REQUESTS", "NUKE", "NUKES"
    };

    private const int MinMessagesForDetection = 20;
    private const int MaxBufferPerNick = 100;
    private const int MaxNicksPerChannel = 50;

    public event Action<string, List<DetectedPattern>>? PatternsDetected; // channel, patterns

    public IrcPatternDetector(IrcService ircService, string serverId = "")
    {
        _ircService = ircService;
        _serverId = serverId;
        _ircService.MessageReceived += OnMessage;

        // Rehydrate the in-memory buffer from today's persistent log so
        // pattern detection has history immediately on startup (instead of
        // waiting for 20+ fresh announces after reconnect).
        if (!string.IsNullOrEmpty(_serverId))
        {
            try { RehydrateFromLog(); }
            catch (Exception ex) { Log.Debug(ex, "IrcPatternDetector: rehydrate failed"); }
        }
    }

    private void RehydrateFromLog()
    {
        var lines = IrcLogStore.ReadRecent(_serverId, 500);
        if (lines.Count == 0) return;

        lock (_lock)
        {
            foreach (var line in lines)
            {
                // Format: HH:mm:ss\t#channel\t<nick>\ttext
                var parts = line.Split('\t', 4);
                if (parts.Length < 4) continue;
                var channel = parts[1];
                var nick = parts[2];
                var text = parts[3];
                if (!channel.StartsWith('#') || string.IsNullOrEmpty(nick)) continue;

                if (!_buffer.TryGetValue(channel, out var channelBuffer))
                {
                    channelBuffer = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
                    _buffer[channel] = channelBuffer;
                }
                if (channelBuffer.Count >= MaxNicksPerChannel && !channelBuffer.ContainsKey(nick))
                    continue;
                if (!channelBuffer.TryGetValue(nick, out var msgs))
                {
                    msgs = new Queue<string>();
                    channelBuffer[nick] = msgs;
                }
                msgs.Enqueue(text);
                while (msgs.Count > MaxBufferPerNick) msgs.Dequeue();
            }
        }
        Log.Information("IrcPatternDetector: rehydrated {Count} messages from disk for {Server}",
            lines.Count, _serverId);
    }

    private void OnMessage(string target, IrcMessageItem message)
    {
        // Only track channel messages (not PMs)
        if (!target.StartsWith('#')) return;
        if (message.Type != IrcMessageType.Normal && message.Type != IrcMessageType.Notice) return;
        if (string.IsNullOrWhiteSpace(message.Text)) return;

        // Persist to disk so AI Setup + restarted sessions still have context
        IrcLogStore.Append(_serverId, target, message.Nick, message.Text);

        lock (_lock)
        {
            if (!_buffer.TryGetValue(target, out var channelBuffer))
            {
                channelBuffer = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
                _buffer[target] = channelBuffer;
            }

            if (channelBuffer.Count >= MaxNicksPerChannel && !channelBuffer.ContainsKey(message.Nick))
                return;

            if (!channelBuffer.TryGetValue(message.Nick, out var msgs))
            {
                msgs = new Queue<string>();
                channelBuffer[message.Nick] = msgs;
            }

            msgs.Enqueue(message.Text);
            if (msgs.Count > MaxBufferPerNick)
                msgs.Dequeue();
        }
    }

    /// <summary>
    /// Analyze buffered messages and return detected announce patterns.
    /// Call this after the bot has been in channels for a while (e.g. 5-10 minutes).
    /// </summary>
    /// <summary>
    /// Returns up to <paramref name="maxMessages"/> recent raw channel messages,
    /// flattened across all channels and nicks. Used by AI Setup to give the
    /// model context when pattern detection hasn't run yet.
    /// </summary>
    public List<string> GetRecentMessages(int maxMessages = 60)
    {
        var results = new List<string>();
        lock (_lock)
        {
            foreach (var (channel, nicks) in _buffer)
            {
                foreach (var (nick, msgs) in nicks)
                {
                    foreach (var m in msgs.TakeLast(5))
                    {
                        results.Add($"{channel} <{nick}> {m}");
                        if (results.Count >= maxMessages) return results;
                    }
                }
            }
        }
        return results;
    }

    public List<DetectedPattern> Analyze()
    {
        var results = new List<DetectedPattern>();

        Dictionary<string, Dictionary<string, List<string>>> snapshot;
        lock (_lock)
        {
            snapshot = _buffer.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(
                    nk => nk.Key,
                    nk => nk.Value.ToList()));
        }

        foreach (var (channel, nicks) in snapshot)
        {
            foreach (var (nick, messages) in nicks)
            {
                if (messages.Count < MinMessagesForDetection) continue;

                // Find messages containing scene release names
                var releaseMessages = new List<(string msg, string release, int releaseStart)>();
                foreach (var msg in messages)
                {
                    var match = SceneNameRegex.Match(msg);
                    if (match.Success && match.Groups["release"].Length >= 10)
                    {
                        releaseMessages.Add((msg, match.Groups["release"].Value, match.Index));
                    }
                }

                if (releaseMessages.Count < messages.Count * 0.3)
                    continue; // Less than 30% have releases — probably not a bot

                // Try to detect section from messages
                var sectionDetected = TryDetectSection(releaseMessages);

                // Find the common framing (prefix before release, suffix after)
                var pattern = BuildPattern(releaseMessages, sectionDetected, channel, nick);
                if (pattern != null)
                    results.Add(pattern);
            }
        }

        if (results.Count > 0)
        {
            foreach (var p in results)
                Log.Information("IRC pattern detected in {Channel} from {Nick}: {Pattern}",
                    p.Channel, p.BotNick, p.SuggestedPattern);

            PatternsDetected?.Invoke(results[0].Channel, results);
        }

        return results;
    }

    private static string? TryDetectSection(List<(string msg, string release, int releaseStart)> messages)
    {
        // Look for known section names appearing consistently before the release name
        var sectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (msg, release, start) in messages)
        {
            var prefix = start > 0 ? msg[..start] : "";
            foreach (var section in KnownSections)
            {
                if (prefix.Contains(section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionCounts.TryGetValue(section, out var count);
                    sectionCounts[section] = count + 1;
                }
            }
        }

        if (sectionCounts.Count == 0) return null;

        // Find the most common section
        var top = sectionCounts.OrderByDescending(kv => kv.Value).First();
        var threshold = messages.Count * 0.5;

        // If multiple different sections appear frequently, the section varies per message
        // — return null so BuildPattern creates a section capture group
        var frequentSections = sectionCounts.Count(kv => kv.Value >= threshold);
        if (frequentSections > 1) return null;

        // If one section appears in 80%+ of messages, it's a fixed channel section
        if (top.Value >= messages.Count * 0.8)
            return top.Key;

        return null;
    }

    private static DetectedPattern? BuildPattern(
        List<(string msg, string release, int releaseStart)> messages,
        string? fixedSection, string channel, string nick)
    {
        if (messages.Count < 5) return null;

        // Find common prefix (text before the release name)
        var prefixes = messages.Select(m => m.releaseStart > 0 ? m.msg[..m.releaseStart].TrimEnd() : "").ToList();
        var commonPrefix = FindCommonStructure(prefixes);

        // Find common suffix (text after the release name)
        var suffixes = messages.Select(m =>
        {
            var endIdx = m.releaseStart + m.release.Length;
            return endIdx < m.msg.Length ? m.msg[endIdx..].TrimStart() : "";
        }).ToList();
        var commonSuffix = FindCommonStructure(suffixes);

        // Check if there's a section-like token in the prefix
        bool hasSectionInPrefix = false;
        var sectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in prefixes)
        {
            foreach (var section in KnownSections)
            {
                if (prefix.Contains(section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionTokens.Add(section);
                    hasSectionInPrefix = true;
                }
            }
        }

        // Build regex pattern
        string pattern;
        if (fixedSection != null && hasSectionInPrefix)
        {
            // Fixed section channel — embed literal section and capture release
            var escapedSection = Regex.Escape(fixedSection);
            var samplePrefix = prefixes.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
            if (samplePrefix.Contains("::"))
                pattern = $"(?<section>{escapedSection})\\s*::\\s*(?<release>\\S+)";
            else if (samplePrefix.Contains('[') && samplePrefix.Contains(']'))
                pattern = $"\\[(?<section>{escapedSection})\\]\\s*(?<release>\\S+)";
            else
                pattern = $"(?<section>{escapedSection})\\s+(?<release>\\S+)";
        }
        else if (hasSectionInPrefix && sectionTokens.Count > 1)
        {
            // Multiple different sections seen — the section varies
            var samplePrefix = prefixes.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
            pattern = BuildPatternWithSection(samplePrefix, commonPrefix, commonSuffix);
        }
        else if (!string.IsNullOrEmpty(commonPrefix))
        {
            // No section detected, just match release after common prefix
            var escapedPrefix = Regex.Escape(commonPrefix).Replace("\\ ", "\\s+");
            pattern = $"{escapedPrefix}\\s*(?<release>\\S+)";
        }
        else
        {
            // Fallback: just match scene release names
            pattern = @"(?<release>(?:[A-Za-z0-9]+[\._-]){2,}[A-Za-z0-9]+-[A-Za-z0-9]+)";
        }

        return new DetectedPattern
        {
            Channel = channel,
            BotNick = nick,
            SuggestedPattern = pattern,
            FixedSection = fixedSection,
            SampleMessages = messages.Take(3).Select(m => m.msg).ToList(),
            MessageCount = messages.Count,
            Confidence = CalculateConfidence(messages.Count, commonPrefix, hasSectionInPrefix || fixedSection != null)
        };
    }

    private static string BuildPatternWithSection(string samplePrefix, string commonPrefix, string commonSuffix)
    {
        // Try common glftpd announce formats:
        // "SECTION :: release" -> "(?<section>\w+) :: (?<release>\S+)"
        // "[SECTION] release" -> "\[(?<section>[^\]]+)\] (?<release>\S+)"
        // "NEW in SECTION: release" -> "NEW in (?<section>\w+):\s*(?<release>\S+)"

        if (samplePrefix.Contains("::"))
            return @"(?<section>\w[\w-]*)\s*::\s*(?<release>\S+)";

        if (samplePrefix.Contains('[') && samplePrefix.Contains(']'))
            return @"\[(?<section>[^\]]+)\]\s*(?<release>\S+)";

        if (samplePrefix.Contains(':'))
            return @"(?<section>\w[\w-]*)\s*:\s*(?<release>\S+)";

        if (samplePrefix.Contains(" - "))
            return @"(?<section>\w[\w-]*)\s+-\s+(?<release>\S+)";

        // Generic: word before release is section
        return @"(?<section>\w[\w-]*)\s+(?<release>\S+)";
    }

    private static string FindCommonStructure(List<string> strings)
    {
        if (strings.Count == 0) return "";

        // Find longest common prefix across all strings
        var first = strings[0];
        var commonLen = first.Length;

        foreach (var s in strings.Skip(1))
        {
            commonLen = Math.Min(commonLen, s.Length);
            for (int i = 0; i < commonLen; i++)
            {
                if (s[i] != first[i])
                {
                    commonLen = i;
                    break;
                }
            }
        }

        return first[..commonLen].TrimEnd();
    }

    private static double CalculateConfidence(int messageCount, string commonPrefix, bool hasSection)
    {
        var score = 0.0;
        if (messageCount >= 50) score += 0.3;
        else if (messageCount >= 30) score += 0.2;
        else score += 0.1;

        if (!string.IsNullOrEmpty(commonPrefix)) score += 0.3;
        if (hasSection) score += 0.2;
        if (commonPrefix.Length > 5) score += 0.2;

        return Math.Min(score, 1.0);
    }

    public void Dispose()
    {
        _ircService.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}

public class DetectedPattern
{
    public string Channel { get; set; } = "";
    public string BotNick { get; set; } = "";
    public string SuggestedPattern { get; set; } = "";
    public string? FixedSection { get; set; }
    public List<string> SampleMessages { get; set; } = [];
    public int MessageCount { get; set; }
    public double Confidence { get; set; }
}
