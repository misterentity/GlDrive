using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Spread;
using GlDrive.Util;
using Serilog;

namespace GlDrive.AiAgent;

public sealed record OwnNukeAlert(string ServerId, string Release, string Reason, int Multiplier,
    DateTime NukedAtUtc, string Source, bool ReleaseTruncated);

public sealed record OwnNukeState(string ServerId, string Release, bool ReleaseIsExact, bool Active,
    DateTime FirstObservedAtUtc, DateTime LastObservedAtUtc, string Reason, int Multiplier, bool AlertedOwn,
    DateTime? LastUnnukedAtUtc = null);

/// <summary>Passive nuke evidence and durable per-episode alert suppression; it never changes a release.</summary>
public sealed class OwnNukeAlertTracker
{
    private const int MaximumEntries = 10000;
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly int _maximumEntries;
    private readonly Lock _sync = new();
    private Dictionary<string, OwnNukeState> _states = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>False when this session started from a quarantined, unreadable file (history was reset).</summary>
    public bool StateAvailable { get; private set; } = true;
    public bool PersistenceHealthy { get; private set; } = true;

    public OwnNukeAlertTracker(string stateFilePath, Func<DateTime>? utcNow = null)
        : this(stateFilePath, utcNow, MaximumEntries) { }

    internal OwnNukeAlertTracker(string stateFilePath, Func<DateTime>? utcNow, int maximumEntries)
    {
        _path = stateFilePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _maximumEntries = Math.Clamp(maximumEntries, 1, MaximumEntries);
        try
        {
            if (!File.Exists(_path)) return;
            var rows = JsonSerializer.Deserialize<OwnNukeState[]>(File.ReadAllText(_path));
            if (rows == null || rows.Length > _maximumEntries || rows.Any(row => !Valid(row.ServerId, row.Release) ||
                row.Reason == null || row.Reason.Length > 2048 || row.Multiplier is < 1 or > 9999 ||
                row.FirstObservedAtUtc.Kind != DateTimeKind.Utc || row.LastObservedAtUtc.Kind != DateTimeKind.Utc ||
                row.LastUnnukedAtUtc is { Kind: not DateTimeKind.Utc }))
                throw new JsonException("Invalid nuke state");
            _states = rows.ToDictionary(row => Key(row.ServerId, row.Release), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _states = new(StringComparer.OrdinalIgnoreCase);
            StateAvailable = false;
            PersistenceHealthy = false;
            Log.Warning(ex, "Nuke alert state is unreadable; quarantined it and starting with no nuke history");
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
        }
    }

    public IReadOnlyList<OwnNukeState> Snapshot()
    {
        lock (_sync) return _states.Values.Where(row => row.Active).OrderByDescending(row => row.LastObservedAtUtc).ToArray();
    }

    public bool IsNuked(string serverId, string release)
    {
        lock (_sync) return _states.TryGetValue(Key(serverId, release), out var state) && state.Active && state.ReleaseIsExact;
    }

    public OwnNukeAlert? ObservePoll(string serverId, string ownAccount, ParsedNuke nuke) =>
        Observe(serverId, nuke.Release, nuke.Reason, nuke.Multiplier, nuke.NukedAt,
            !nuke.ReleaseTruncated, OwnNukeAttribution.IsOwn(ownAccount, [nuke.Nukee ?? ""]),
            nuke.HasExactTimestamp, "SITE NUKES", _utcNow(), nuke.EarliestNukedAtUtc);

    /// <summary>Call only for events accepted by the configured trusted-bot parser.</summary>
    public OwnNukeAlert? ObserveSitebot(SitebotEvent fact, string ownAccount)
    {
        if (fact.Kind == SitebotEventKind.Unnuke)
        {
            ObserveUnnuke(fact.ServerId, fact.Release, fact.ObservedAtUtc);
            return null;
        }
        if (fact.Kind != SitebotEventKind.Nuke) return null;
        return Observe(fact.ServerId, fact.Release, fact.Reason ?? "Reason not supplied", fact.NukeFactor ?? 1,
            fact.ObservedAtUtc, true, OwnNukeAttribution.IsOwn(ownAccount, fact.Nukees), true, "IRC", fact.ObservedAtUtc);
    }

    public void ObserveUnnuke(string serverId, string release, DateTime? observedAtUtc = null)
    {
        if (!Valid(serverId, release)) return;
        var now = observedAtUtc ?? _utcNow();
        if (!ValidObservation(now)) return;
        lock (_sync)
        {
            var key = Key(serverId, release);
            if (_states.TryGetValue(key, out var old) && now < old.LastObservedAtUtc) return;
            if (old == null && !MakeRoom()) return;
            var next = old == null
                ? new OwnNukeState(serverId, release, IsExact(release), false, now, now, "", 1, false, now)
                : old with { Active = false, LastObservedAtUtc = now, LastUnnukedAtUtc = now };
            _states[key] = next;
            Save();
        }
    }

    private OwnNukeAlert? Observe(string serverId, string release, string reason, int multiplier, DateTime nukedAt,
        bool exactRelease, bool own, bool exactTime, string source, DateTime observed, DateTime? earliestNukedAtUtc = null)
    {
        if (!Valid(serverId, release) || !ValidObservation(observed) || reason == null || reason.Length > 2048 ||
            multiplier is < 1 or > 9999) return null;
        lock (_sync)
        {
            var key = Key(serverId, release);
            _states.TryGetValue(key, out var old);
            // Replayed age-only rows must not resurrect an explicit UNNUKE. A
            // later table row starts a new episode only if even its oldest possible
            // timestamp (including age-column rounding) is newer than the clear.
            var earliest = exactTime ? AsUtc(nukedAt) : earliestNukedAtUtc;
            if (old?.LastUnnukedAtUtc is { } cleared && (earliest == null || earliest <= cleared)) return null;
            if (old == null && !MakeRoom()) return null;
            var newEpisode = old?.LastUnnukedAtUtc is { } lastClear && earliest > lastClear;
            var alerted = !newEpisode && old?.AlertedOwn == true;
            var next = new OwnNukeState(serverId, release, exactRelease && IsExact(release), true,
                newEpisode || old == null ? observed : old.FirstObservedAtUtc, observed,
                reason, multiplier, alerted || own, newEpisode ? null : old?.LastUnnukedAtUtc);
            _states[key] = next;
            var saved = Save();
            if (!saved && own && !alerted) _states[key] = next with { AlertedOwn = false };
            return saved && own && !alerted
                ? new(serverId, release, reason, multiplier, AsUtc(nukedAt), source, !next.ReleaseIsExact) : null;
        }
    }

    private bool MakeRoom()
    {
        if (_states.Count < _maximumEntries) return true;
        // Every nuke on every site is recorded, so the store fills over months. Drop the
        // oldest tenth by last observation: those rows are far older than any site's
        // recent-nukes table, so they cannot re-alert. Refusing new rows instead made the
        // nuke state "unavailable", which held every race.
        var evict = Math.Max(1, _maximumEntries / 10);
        foreach (var key in _states.OrderBy(pair => pair.Value.LastObservedAtUtc).Take(evict)
                     .Select(pair => pair.Key).ToArray())
            _states.Remove(key);
        Log.Information("Nuke state capacity reached; dropped the {Count} oldest observations", evict);
        return true;
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(_states.Values.ToArray()));
            PersistenceHealthy = true;
            return true;
        }
        catch (Exception ex) { PersistenceHealthy = false; Log.Warning(ex, "Nuke alert state could not be persisted"); return false; }
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private bool ValidObservation(DateTime value) => value != default && value.Kind == DateTimeKind.Utc && value <= _utcNow().AddMinutes(5);
    private static bool Valid(string server, string release) => !string.IsNullOrWhiteSpace(server) && server.Length <= 256 &&
        !server.Any(char.IsControl) && !string.IsNullOrWhiteSpace(release) && release.Length <= 1024 &&
        !release.Any(c => char.IsControl(c) || c is '/' or '\\');
    private static bool IsExact(string release) => !release.Contains("...", StringComparison.Ordinal) &&
        !release.Contains('…') && !release.StartsWith('.') && FtpSearchService.IsReleaseDirectory(release);
    private static string Key(string server, string release) => server + '\0' + release;
}

internal static class OwnNukeAttribution
{
    internal static bool IsOwn(string account, IEnumerable<string> nukees) => IsExactAccount(account) &&
        nukees.Any(nukee => IsExactAccount(nukee) && string.Equals(nukee, account, StringComparison.OrdinalIgnoreCase));

    private static bool IsExactAccount(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        !value.Contains("...", StringComparison.Ordinal) && !value.Contains('…') &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '*' or '?' or ',' or '/' or '\\');
}
