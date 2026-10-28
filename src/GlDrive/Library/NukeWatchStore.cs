using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Library;

public sealed record OwnedReleaseObservation(string ReleaseName, string LocalPath, DateTime OwnedAtUtc, string? WishlistItemId = null);
public sealed record NukeWatchEvidence(string ReleaseName, string Source, string Reason, DateTime ObservedAtUtc, bool Active = true);
public sealed record NukeFixAttempt(string OwnedRelease, string CandidateRelease, string DownloadId, DateTime AttemptedAtUtc, bool Completed = false);
public sealed record OwnedNukeEntry(OwnedReleaseObservation Owned, bool Flagged, bool QualityNuke, string? Reason,
    IReadOnlyList<string> Sources, IReadOnlyList<NukeFixAttempt> Attempts);

/// <summary>Bounded passive bookkeeping. Missing durability never gates an engine or queue.</summary>
public sealed class NukeWatchStore
{
    private sealed class State
    {
        public List<OwnedReleaseObservation> Owned { get; set; } = [];
        public List<NukeWatchEvidence> Evidence { get; set; } = [];
        public List<NukeFixAttempt> Attempts { get; set; } = [];
    }
    private readonly Lock _sync = new();
    private readonly string _path;
    private readonly Func<NukeWatchConfig> _settings;
    private readonly Func<DateTime> _now;
    private State _state = new();
    private bool _warned;
    public bool PersistenceHealthy { get; private set; } = true;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public NukeWatchStore(string path, Func<NukeWatchConfig> settings, Func<DateTime>? now = null)
    {
        _path = path; _settings = settings; _now = now ?? (() => DateTime.UtcNow);
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException("Nuke watch state is oversized.");
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? throw new JsonException("Empty nuke watch state");
                if (_state.Owned == null || _state.Evidence == null || _state.Attempts == null ||
                    _state.Owned.Any(o => o == null || !ValidRelease(o.ReleaseName)) ||
                    _state.Evidence.Any(e => e == null || !ValidRelease(e.ReleaseName) || string.IsNullOrWhiteSpace(e.Source) || e.Source.Length > 160) ||
                    _state.Attempts.Any(a => a == null || !ValidRelease(a.OwnedRelease) || !ValidRelease(a.CandidateRelease)))
                    throw new JsonException("Invalid nuke watch state");
                _state.Evidence = _state.Evidence.Where(e => e.Source != "predb" || e.Active)
                    .Select(e => e with { Reason = e.Reason ?? "" }).ToList();
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            _state = new();
            Log.Warning(ex, "Nuke watch state unreadable; starting with empty passive observations");
        }
    }

    public void ObserveOwned(OwnedReleaseObservation owned)
    {
        if (!ValidRelease(owned.ReleaseName) || string.IsNullOrWhiteSpace(owned.LocalPath) || owned.LocalPath.Length > 32767 ||
            owned.OwnedAtUtc > _now().AddMinutes(1) || owned.OwnedAtUtc < _now().AddDays(-Days)) return;
        lock (_sync)
        {
            Prune();
            var old = _state.Owned.Find(o => Equal(o.ReleaseName, owned.ReleaseName));
            if (old != null && (old.OwnedAtUtc >= owned.OwnedAtUtc || SameLocalPath(old.LocalPath, owned.LocalPath)))
            {
                if (string.IsNullOrWhiteSpace(old.WishlistItemId) && !string.IsNullOrWhiteSpace(owned.WishlistItemId))
                {
                    _state.Owned.Remove(old);
                    _state.Owned.Add(old with { WishlistItemId = owned.WishlistItemId });
                    Save();
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(owned.WishlistItemId) && old != null)
                owned = owned with { WishlistItemId = old.WishlistItemId };
            _state.Owned.RemoveAll(o => Equal(o.ReleaseName, owned.ReleaseName));
            _state.Owned.Add(owned);
            Prune(); Save();
        }
    }

    public void ObserveNuke(NukeWatchEvidence evidence)
    {
        if (!ValidRelease(evidence.ReleaseName) || string.IsNullOrWhiteSpace(evidence.Source) || evidence.Source.Length > 160 ||
            evidence.Reason?.Length > 1024 || evidence.ObservedAtUtc > _now().AddMinutes(1) || evidence.Source == "predb" && !evidence.Active) return;
        evidence = evidence with { Reason = evidence.Reason ?? "" };
        lock (_sync)
        {
            var old = _state.Evidence.Find(e => Equal(e.ReleaseName, evidence.ReleaseName) && e.Source == evidence.Source);
            if (old != null && (old.ObservedAtUtc > evidence.ObservedAtUtc ||
                old.Active == evidence.Active && old.Reason == evidence.Reason &&
                (evidence.Source == "predb" || old.ObservedAtUtc == evidence.ObservedAtUtc))) return;
            _state.Evidence.RemoveAll(e => Equal(e.ReleaseName, evidence.ReleaseName) && e.Source == evidence.Source);
            _state.Evidence.Add(evidence);
            Prune(); Save();
        }
    }

    public void RecordAttempt(NukeFixAttempt attempt)
    {
        if (!ValidRelease(attempt.OwnedRelease) || !ValidRelease(attempt.CandidateRelease) || string.IsNullOrWhiteSpace(attempt.DownloadId)) return;
        lock (_sync)
        {
            var previous = _state.Attempts.Find(a => Equal(a.OwnedRelease, attempt.OwnedRelease) && Equal(a.CandidateRelease, attempt.CandidateRelease));
            if (previous != null)
            {
                if (!attempt.Completed || previous.Completed) return;
                _state.Attempts.Remove(previous);
            }
            _state.Attempts.Add(attempt);
            Prune(); Save();
        }
    }

    public bool WasAttempted(string ownedRelease, string candidate)
    { lock (_sync) return _state.Attempts.Any(a => Equal(a.OwnedRelease, ownedRelease) && Equal(a.CandidateRelease, candidate)); }

    public bool IsCandidateNuked(string serverId, string release)
    { lock (_sync) return _state.Evidence.Any(e => e.Active && Equal(e.ReleaseName, release) &&
        (e.Source == "predb" || e.Source == "site:" + serverId)); }

    public IReadOnlyList<OwnedNukeEntry> Snapshot()
    {
        lock (_sync)
        {
            Prune();
            var evidenceByRelease = _state.Evidence.Where(e => e.Active)
                .GroupBy(e => e.ReleaseName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(e => e.ObservedAtUtc).ToArray(), StringComparer.OrdinalIgnoreCase);
            var attemptsByRelease = _state.Attempts.GroupBy(a => a.OwnedRelease, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            return _state.Owned.OrderByDescending(o => o.OwnedAtUtc).Select(owned =>
            {
                var evidence = evidenceByRelease.GetValueOrDefault(owned.ReleaseName) ?? [];
                return new OwnedNukeEntry(owned, evidence.Length > 0, evidence.Any(e => NukeWatchPolicy.IsQualityNuke(e.Reason)),
                    evidence.FirstOrDefault()?.Reason, evidence.Select(e => e.Source).ToArray(),
                    attemptsByRelease.GetValueOrDefault(owned.ReleaseName) ?? []);
            }).ToArray();
        }
    }

    private int Days => Math.Clamp(_settings().WatchDays, 1, 90);
    private void Prune()
    {
        var capacity = Math.Clamp(_settings().HistoryCapacity, 1, 10000);
        _state.Owned.RemoveAll(o => o.OwnedAtUtc < _now().AddDays(-Days));
        _state.Owned = _state.Owned.OrderByDescending(o => o.OwnedAtUtc).Take(capacity).ToList();
        _state.Evidence = _state.Evidence.OrderByDescending(e => e.ObservedAtUtc).Take(capacity).ToList();
        _state.Attempts = _state.Attempts.OrderByDescending(a => a.AttemptedAtUtc).Take(capacity).ToList();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(_state, Json));
            PersistenceHealthy = true; _warned = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PersistenceHealthy = false;
            if (!_warned) Log.Warning(ex, "Nuke watch save failed; retaining passive state in memory");
            _warned = true;
        }
    }

    internal static bool ValidRelease(string? release) => !string.IsNullOrWhiteSpace(release) && release.Length <= 1024 &&
        !release.Any(c => char.IsControl(c) || c is '/' or '\\') && !release.StartsWith('.') && !release.EndsWith("...", StringComparison.Ordinal);
    internal static bool Equal(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool SameLocalPath(string first, string second)
    {
        try { return Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
