using System.IO;
using System.Text.Json;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Services;

public sealed record AnnounceFeedObservation(string ServerId, string Channel, bool Connected, bool Joined,
    bool? TrustedBotPresent = null, int DecryptFailureStreak = 0);

public sealed record AnnounceFeedNotice(string ServerId, string Channel, bool Recovered, DateTime ObservedAtUtc,
    string Diagnosis, double ExpectedAnnounces);

public sealed record AnnounceFeedSnapshot(string ServerId, string Channel, string State, DateTime? LastAnnounceAtUtc,
    DateTime? OutageStartedAtUtc, double ObservedSilenceMinutes, double ExpectedAnnounces, bool BaselineReady,
    string Diagnosis, bool Connected, bool Joined, DateTime? LastRecoveryAttemptUtc);

/// <summary>Passive channel health. Time is learned only while this process observes a joined feed.</summary>
public sealed class AnnounceFeedHealthMonitor
{
    private const int MaximumChannels = 512;
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Feed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadFailed;
    public bool PersistenceHealthy { get; private set; } = true;

    public AnnounceFeedHealthMonitor(string stateFilePath, Func<DateTime>? utcNow = null)
    {
        _path = stateFilePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 8 * 1024 * 1024) throw new JsonException("Announce health state exceeds limit");
            var rows = JsonSerializer.Deserialize<AnnounceFeedState[]>(File.ReadAllText(_path));
            if (rows == null || rows.Length > MaximumChannels || rows.Any(row => !Valid(row)))
                throw new JsonException("Invalid announce health state");
            foreach (var row in rows)
                if (!_feeds.TryAdd(Key(row.ServerId, row.Channel), new Feed(row)))
                    throw new JsonException("Duplicate announce health channel");
        }
        catch (Exception ex)
        {
            _feeds.Clear();
            _loadFailed = true;
            PersistenceHealthy = false;
            Log.Warning(ex, "Announce health state unreadable; preserving file and suppressing notices");
        }
    }

    /// <summary>Call at least once a minute for configured announce channels, including disconnected ones.</summary>
    public IReadOnlyList<AnnounceFeedNotice> Tick(IEnumerable<AnnounceFeedObservation> observations)
    {
        var now = _utcNow();
        if (!Utc(now)) return [];
        var notices = new List<AnnounceFeedNotice>();
        lock (_sync)
        {
            var current = observations.Where(o => ValidIdentity(o.ServerId, o.Channel))
                .DistinctBy(o => Key(o.ServerId, o.Channel), StringComparer.OrdinalIgnoreCase).Take(MaximumChannels).ToArray();
            var active = current.Select(o => Key(o.ServerId, o.Channel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, feed) in _feeds)
                if (!active.Contains(key)) { feed.LastTick = null; feed.Observation = null; }
            foreach (var observation in current)
            {
                var feed = Get(observation.ServerId, observation.Channel);
                if (feed == null) continue;
                Advance(feed, now);
                feed.Observation = observation;
                feed.LastTick = now;
                if (feed.State.RecoveryPending)
                {
                    feed.State.RecoveryPending = false;
                    if (SaveNoLock()) notices.Add(new(feed.State.ServerId, feed.State.Channel, true, now,
                        "Matched announces resumed", 0));
                    else { feed.State.RecoveryPending = true; continue; }
                }
                if (feed.State.OutageStartedAtUtc == null && AnnounceFeedHealthPolicy.IsSilent(
                        feed.State.SilenceSeconds, feed.State.ExpectedAnnounces))
                    feed.State.OutageStartedAtUtc = now;
                if (feed.State.OutageStartedAtUtc != null && !feed.State.OutageNoticeSent)
                {
                    feed.State.OutageNoticeSent = true;
                    if (SaveNoLock()) notices.Add(Notice(feed, false, now));
                    else feed.State.OutageNoticeSent = false;
                }
            }
        }
        return notices;
    }

    /// <summary>Input must have passed the listener's channel trust and release plausibility checks.</summary>
    public AnnounceFeedNotice? ObserveAnnounce(string serverId, string channel, DateTime observedAtUtc)
    {
        var now = _utcNow();
        if (!ValidIdentity(serverId, channel) || !Utc(observedAtUtc) || observedAtUtc > now.AddMinutes(1) ||
            now - observedAtUtc > TimeSpan.FromMinutes(2)) return null;
        lock (_sync)
        {
            var feed = Get(serverId, channel);
            if (feed == null || feed.State.LastAnnounceAtUtc > observedAtUtc) return null;
            Advance(feed, observedAtUtc);
            var state = feed.State;
            var wasNotified = state.OutageNoticeSent || state.RecoveryPending;
            var expected = state.ExpectedAnnounces;
            // No replayed/offline timestamp may create observed exposure or inflate a baseline.
            if (feed.LastTick is { } tick && observedAtUtc >= tick && observedAtUtc - tick <= AnnounceFeedHealthPolicy.MaximumTickGap &&
                feed.Observation is { Connected: true, Joined: true })
            {
                var hour = AnnounceFeedHealthPolicy.HourOfWeek(observedAtUtc);
                state.Counts[hour] = Math.Min(1e9, state.Counts[hour] + 1);
            }
            state.LastAnnounceAtUtc = observedAtUtc;
            state.SilenceSeconds = 0;
            state.ExpectedAnnounces = 0;
            state.OutageStartedAtUtc = null;
            state.OutageNoticeSent = false;
            state.RecoveryPending = false;
            // Advance already accounted for any live interval up to the message.
            if (feed.LastTick != null && observedAtUtc >= feed.LastTick) feed.LastTick = observedAtUtc;
            if (!wasNotified) return null;
            if (SaveNoLock()) return new(serverId, channel, true, observedAtUtc, "Matched announces resumed", expected);
            state.RecoveryPending = true;
            return null;
        }
    }

    /// <summary>Durable server-wide cooldown. The caller must still use the existing pooled invite/join path.</summary>
    public bool TryBeginRecovery(string serverId, string channel)
    {
        lock (_sync)
        {
            if (!_feeds.TryGetValue(Key(serverId, channel), out var feed)) return false;
            var now = _utcNow();
            var last = _feeds.Values.Where(f => f.State.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.State.LastRecoveryAttemptUtc).Max();
            if (feed.State.OutageStartedAtUtc == null || feed.Observation is not { Connected: true, Joined: false } ||
                !AnnounceFeedHealthPolicy.RecoveryDue(now, last)) return false;
            var previous = feed.State.LastRecoveryAttemptUtc;
            feed.State.LastRecoveryAttemptUtc = now;
            if (SaveNoLock()) return true;
            feed.State.LastRecoveryAttemptUtc = previous;
            return false;
        }
    }

    public IReadOnlyList<AnnounceFeedSnapshot> Snapshot()
    {
        lock (_sync) return _feeds.Values.Select(feed =>
        {
            var state = feed.State;
            var ready = AnnounceFeedHealthPolicy.Rate(state.Counts, state.ExposureSeconds, AnnounceFeedHealthPolicy.HourOfWeek(_utcNow())) > 0;
            return new AnnounceFeedSnapshot(state.ServerId, state.Channel,
                state.OutageStartedAtUtc != null ? "Down" : ready ? "Healthy" : "Learning", state.LastAnnounceAtUtc,
                state.OutageStartedAtUtc, state.SilenceSeconds / 60, state.ExpectedAnnounces, ready,
                AnnounceFeedHealthPolicy.Diagnose(feed.Observation), feed.Observation?.Connected == true,
                feed.Observation?.Joined == true, state.LastRecoveryAttemptUtc);
        }).OrderBy(s => s.ServerId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Channel, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool Save() { lock (_sync) return SaveNoLock(); }

    private void Advance(Feed feed, DateTime now)
    {
        if (feed.LastTick is not { } last || now <= last || now - last > AnnounceFeedHealthPolicy.MaximumTickGap) return;
        var state = feed.State;
        state.SilenceSeconds = Math.Min(TimeSpan.FromDays(365).TotalSeconds, state.SilenceSeconds + (now - last).TotalSeconds);
        state.ExpectedAnnounces = Math.Min(1e9, state.ExpectedAnnounces +
            AnnounceFeedHealthPolicy.Expected(state.Counts, state.ExposureSeconds, last, now));
        if (state.OutageStartedAtUtc == null && feed.Observation is { Connected: true, Joined: true, DecryptFailureStreak: < 3 }
            && feed.Observation.TrustedBotPresent != false)
            AnnounceFeedHealthPolicy.AddExposure(state.Counts, state.ExposureSeconds, last, now);
        feed.LastTick = now;
    }

    private Feed? Get(string serverId, string channel)
    {
        var key = Key(serverId, channel);
        if (_feeds.TryGetValue(key, out var feed)) return feed;
        if (_feeds.Count >= MaximumChannels) return null;
        _feeds.Add(key, feed = new(new AnnounceFeedState { ServerId = serverId, Channel = channel }));
        return feed;
    }

    private bool SaveNoLock()
    {
        if (_loadFailed) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(_feeds.Values.Select(f => f.State).ToArray()));
            PersistenceHealthy = true;
            return true;
        }
        catch (Exception ex) { PersistenceHealthy = false; Log.Warning(ex, "Announce health state save failed"); return false; }
    }

    private static AnnounceFeedNotice Notice(Feed feed, bool recovered, DateTime now) => new(feed.State.ServerId,
        feed.State.Channel, recovered, now, AnnounceFeedHealthPolicy.Diagnose(feed.Observation), feed.State.ExpectedAnnounces);

    private static bool Valid(AnnounceFeedState row) => row != null && ValidIdentity(row.ServerId, row.Channel) &&
        row.Counts is { Length: 168 } && row.ExposureSeconds is { Length: 168 } &&
        row.Counts.All(v => double.IsFinite(v) && v is >= 0 and <= 1e9) &&
        row.ExposureSeconds.All(v => double.IsFinite(v) && v is >= 0 and <= 86400 * 24) &&
        double.IsFinite(row.SilenceSeconds) && row.SilenceSeconds is >= 0 and <= 86400 * 365 &&
        double.IsFinite(row.ExpectedAnnounces) && row.ExpectedAnnounces is >= 0 and <= 1e9 &&
        (row.LastAnnounceAtUtc == null || Utc(row.LastAnnounceAtUtc.Value)) &&
        (row.OutageStartedAtUtc == null || Utc(row.OutageStartedAtUtc.Value)) &&
        (row.LastRecoveryAttemptUtc == null || Utc(row.LastRecoveryAttemptUtc.Value)) &&
        (!row.OutageNoticeSent || row.OutageStartedAtUtc != null) &&
        (!row.RecoveryPending || row.OutageStartedAtUtc == null);
    private static bool Utc(DateTime value) => value != default && value.Kind == DateTimeKind.Utc;
    private static bool ValidIdentity(string server, string channel) => !string.IsNullOrWhiteSpace(server) && server.Length <= 256 &&
        !server.Any(char.IsControl) && !string.IsNullOrWhiteSpace(channel) && channel.Length <= 256 &&
        channel[0] is '#' or '&' && !channel.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    private static string Key(string server, string channel) => server + '\0' + channel;
    private sealed class Feed(AnnounceFeedState state)
    {
        internal AnnounceFeedState State { get; } = state;
        internal DateTime? LastTick { get; set; }
        internal AnnounceFeedObservation? Observation { get; set; }
    }
}

internal sealed class AnnounceFeedState
{
    public string ServerId { get; set; } = "";
    public string Channel { get; set; } = "";
    public double[] Counts { get; set; } = new double[168];
    public double[] ExposureSeconds { get; set; } = new double[168];
    public DateTime? LastAnnounceAtUtc { get; set; }
    public double SilenceSeconds { get; set; }
    public double ExpectedAnnounces { get; set; }
    public DateTime? OutageStartedAtUtc { get; set; }
    public bool OutageNoticeSent { get; set; }
    public bool RecoveryPending { get; set; }
    public DateTime? LastRecoveryAttemptUtc { get; set; }
}

internal static class AnnounceFeedHealthPolicy
{
    internal static readonly TimeSpan MaximumTickGap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(30);
    internal static int HourOfWeek(DateTime utc) => (int)utc.DayOfWeek * 24 + utc.Hour;
    internal static double Rate(double[] counts, double[] exposure, int hour) => exposure[hour] >= 7200 && counts[hour] >= 20 &&
        counts[hour] * 3600 / exposure[hour] >= 1 ? counts[hour] / exposure[hour] : 0;
    internal static bool IsSilent(double seconds, double expected) => seconds >= 3600 && expected >= -Math.Log(0.001);
    internal static bool RecoveryDue(DateTime now, DateTime? last) => last == null || now - last >= RecoveryInterval;
    internal static bool RecoveryAllowed(bool connected, bool autoJoin, bool joined, bool standingRetry,
        DateTime now, DateTime? last) => connected && autoJoin && !joined && !standingRetry && RecoveryDue(now, last);
    internal static string Diagnose(AnnounceFeedObservation? observation) => observation switch
    {
        null => "Waiting for local IRC state",
        { Connected: false } => "IRC disconnected",
        { Joined: false } => "Channel not joined",
        { DecryptFailureStreak: >= 3 } => "FiSH decryption failures observed",
        { TrustedBotPresent: false } => "Configured sitebot absent from channel snapshot",
        _ => "No matched announces; cause unknown"
    };

    internal static double Expected(double[] counts, double[] exposure, DateTime from, DateTime until)
    {
        double total = 0;
        Split(from, until, (hour, seconds) => total += Rate(counts, exposure, hour) * seconds);
        return total;
    }

    internal static void AddExposure(double[] counts, double[] exposure, DateTime from, DateTime until) =>
        Split(from, until, (hour, seconds) =>
        {
            exposure[hour] += seconds;
            // Bound memory of old seasons without manufacturing observations or exposure.
            const double maximum = 24 * 3600;
            if (exposure[hour] > maximum)
            {
                counts[hour] *= maximum / exposure[hour];
                exposure[hour] = maximum;
            }
        });

    private static void Split(DateTime from, DateTime until, Action<int, double> part)
    {
        while (from < until)
        {
            var hourEnd = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            var end = until < hourEnd ? until : hourEnd;
            part(HourOfWeek(from), (end - from).TotalSeconds);
            from = end;
        }
    }
}
