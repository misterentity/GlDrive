using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Irc;

/// <summary>Local attention only; none of these decisions affect announce delivery.</summary>
internal sealed class IrcAttentionPolicy(IrcConfig config)
{
    private readonly AnnounceMatcher _announces = new(config.AnnounceRules, false);

    public bool IsAutomated(string target, string sender, string text)
    {
        if (config.Channels.Any(c => !string.IsNullOrEmpty(c.Sitebot?.BotNick) &&
            IrcHighlightMatcher.SameNick(c.Sitebot.BotNick, sender))) return true;
        if (!IrcLogStore.IsChannelName(target)) return false;
        // An explicit channel-scoped announce rule identifies an announce feed.
        // Global rules still allow ordinary human conversation in other channels.
        if (config.AnnounceRules.Any(r => r.Enabled && !string.IsNullOrEmpty(r.Channel) &&
            IrcHighlightMatcher.SameNick(r.Channel, target))) return true;
        return _announces.Enumerate(target, text).Any();
    }
}

/// <summary>One local notice per conversation per interval, retaining the suppressed count.</summary>
internal sealed class IrcAttentionThrottle
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime LastNotice, DateTime LastSeen, int Pending)> _targets = new(StringComparer.Ordinal);
    private readonly int _capacity;

    internal IrcAttentionThrottle(int capacity = 512) => _capacity = Math.Max(1, capacity);

    public int Record(string target, DateTime now)
    {
        target = IrcHighlightMatcher.Fold(target);
        lock (_gate)
        {
            if (!_targets.TryGetValue(target, out var state))
            {
                if (_targets.Count >= _capacity) _targets.Remove(_targets.MinBy(p => p.Value.LastSeen).Key);
                _targets[target] = (now, now, 0);
                return 1;
            }
            var count = state.Pending == int.MaxValue ? int.MaxValue : state.Pending + 1;
            if (now - state.LastNotice < Interval)
            {
                _targets[target] = (state.LastNotice, now, count);
                return 0;
            }
            _targets[target] = (now, now, 0);
            return count;
        }
    }
}
