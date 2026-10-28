using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Downloads;

/// <summary>A passive hint only. DownloadManager still verifies the actual release listing.</summary>
public enum WantedReadiness { Unknown, TrustedComplete }

public sealed record WantedWindowSnapshot(string WishlistItemId, string AcquisitionKey,
    DateTimeOffset OpenedAtUtc, DateTimeOffset DecideAtUtc, int Candidates, int CompleteCandidates,
    string State, string? BestRelease, string? BestServerId, int? BestScore);

/// <summary>
/// Collects wanted variants across mounted sites without doing any network work.
/// Windows have a fixed opening time: repeated announces cannot postpone a decision.
/// </summary>
public sealed class WantedAcquisitionCoordinator
{
    internal const int MaximumWindows = 512;
    internal const int MaximumCandidatesPerWindow = 64;
    private readonly Lock _sync = new();
    private readonly WishlistStore _wishlist;
    private readonly DownloadConfig _config;
    private readonly Func<DateTimeOffset> _utcNow;
    public Func<string, string, bool>? CompletionStillValid { get; set; }
    public Func<string, double>? SourceSpeed { get; set; }
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WishlistMatcher> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<Candidate> _dispatching = new(ReferenceEqualityComparer.Instance);
    private int _processing;
    internal int DispatchingCount { get { lock (_sync) return _dispatching.Count; } }

    private sealed class Window(string itemId, string key, DateTimeOffset opened, DateTimeOffset due)
    {
        internal readonly string ItemId = itemId;
        internal readonly string Key = key;
        internal readonly DateTimeOffset Opened = opened;
        internal readonly DateTimeOffset Due = due;
        internal readonly Dictionary<string, Candidate> Candidates = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Candidate(WishlistMatcher Matcher, string Category, string Release, string Path,
        DateTimeOffset FirstSeen, DateTimeOffset? CompleteAt, bool FromIrc, long? SizeBytes, Func<bool>? AcquisitionAllowed)
    {
        internal bool Revoked { get; set; }
    }

    public WantedAcquisitionCoordinator(WishlistStore wishlist, DownloadConfig config,
        Func<DateTimeOffset>? utcNow = null, Func<string, string, bool>? readinessValidator = null)
    {
        _wishlist = wishlist;
        _config = config;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        CompletionStillValid = readinessValidator;
    }

    internal void RegisterMatcher(WishlistMatcher matcher)
    {
        WishlistMatcher? replaced;
        lock (_sync)
        {
            _sources.TryGetValue(matcher.ServerId, out replaced);
            _sources[matcher.ServerId] = matcher;
        }
        if (replaced != null && !ReferenceEquals(replaced, matcher)) replaced.DetachWantedCoordinator();
    }

    public void Offer(WishlistMatcher matcher, string category, string release, string remotePath,
        WantedReadiness readiness = WantedReadiness.Unknown, bool fromIrc = false, long? sizeBytes = null,
        DateTimeOffset? completeAtUtc = null, string? wantedItemId = null, Func<bool>? acquisitionAllowed = null)
    {
        if (!_config.WantedDecisionWindowEnabled || !_config.AutoDownloadWishlist || !SafeCandidate(release, remotePath)) return;
        var now = _utcNow();
        lock (_sync)
        {
            if (!_sources.TryGetValue(matcher.ServerId, out var registered) || !ReferenceEquals(registered, matcher)) return;
            Prune(now);
            foreach (var item in _wishlist.Items)
            {
                if (wantedItemId != null && item.Id != wantedItemId) continue;
                if (!Evaluate(item, release, sizeBytes).Accepted) continue;
                foreach (var key in WishlistAcquisition.Keys(item, release))
                {
                    var windowId = item.Id + "\n" + key;
                    if (!_windows.TryGetValue(windowId, out var window))
                    {
                        // Preserve existing decisions under load instead of evicting
                        // a wanted title to make room for a later announce.
                        if (_windows.Count >= MaximumWindows) continue;
                        _windows[windowId] = window = new(item.Id, key, now,
                            now.AddMinutes(Math.Clamp(_config.WantedDecisionWindowMinutes, 1, 60)));
                    }
                    var sourceKey = matcher.ServerId + "\n" + release;
                    window.Candidates.TryGetValue(sourceKey, out var existing);
                    if (existing == null && window.Candidates.Count >= MaximumCandidatesPerWindow) continue;
                    window.Candidates[sourceKey] = new(matcher, category, release, remotePath,
                        existing?.FirstSeen ?? now,
                        readiness == WantedReadiness.TrustedComplete ? completeAtUtc ?? now : existing?.CompleteAt,
                        fromIrc || existing?.FromIrc == true, sizeBytes is > 0 ? sizeBytes : existing?.SizeBytes,
                        readiness == WantedReadiness.TrustedComplete
                            ? existing?.CompleteAt != null && existing.AcquisitionAllowed == null ? null : acquisitionAllowed
                            : existing?.AcquisitionAllowed);
                }
            }
        }
    }

    /// <summary>Called by the application's existing scheduling lifetime; never opens a connection.</summary>
    public int ProcessDue()
    {
        if (Interlocked.Exchange(ref _processing, 1) != 0) return 0;
        try
        {
            List<(string Id, Window Window)> due;
            lock (_sync)
            {
                if (!_config.WantedDecisionWindowEnabled || !_config.AutoDownloadWishlist) { _windows.Clear(); return 0; }
                var now = _utcNow();
                Prune(now);
                due = _windows.Where(p => p.Value.Due <= now)
                    .OrderBy(p => p.Value.Due).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => (p.Key, p.Value)).ToList();
            }
            var admitted = 0;
            foreach (var (id, window) in due)
            {
                Candidate? chosen;
                lock (_sync)
                {
                    if (!_config.WantedDecisionWindowEnabled || !_config.AutoDownloadWishlist) { _windows.Clear(); break; }
                    if (!_windows.TryGetValue(id, out var current) || !ReferenceEquals(current, window)) continue;
                    chosen = Ranked(window, _utcNow()).FirstOrDefault().Candidate;
                    if (chosen == null) continue;
                    // Admission can call UI/store observers: never hold our lock.
                    // Remove before dispatch; durable wishlist admission is the
                    // authority for repeated or overlapping episode windows.
                    _windows.Remove(id);
                    _dispatching.Add(chosen);
                }
                try
                {
                    if (_config.WantedDecisionWindowEnabled && _config.AutoDownloadWishlist &&
                        chosen.Matcher.DispatchWanted(window.ItemId, chosen.Category, chosen.Release,
                            chosen.Path, chosen.FromIrc, chosen.SizeBytes, () => StillEligible(window.ItemId, chosen))) admitted++;
                }
                finally { lock (_sync) _dispatching.Remove(chosen); }
            }
            return admitted;
        }
        finally { Volatile.Write(ref _processing, 0); }
    }

    public IReadOnlyList<WantedWindowSnapshot> Snapshot()
    {
        lock (_sync)
        {
            var now = _utcNow();
            Prune(now);
            return _windows.Values.OrderBy(w => w.Due).Select(window =>
            {
                var ranked = Ranked(window, now).ToList();
                var best = ranked.FirstOrDefault();
                return new WantedWindowSnapshot(window.ItemId, window.Key, window.Opened, window.Due,
                    window.Candidates.Count, ranked.Count,
                    ranked.Count == 0 ? "waiting-for-completion" : now < window.Due ? "collecting-variants" : "ready",
                    best.Candidate?.Release, best.Candidate?.Matcher.ServerId,
                    best.Candidate == null ? null : best.Score);
            }).ToArray();
        }
    }

    public void RemoveServer(string serverId)
    {
        WishlistMatcher? removed;
        lock (_sync)
        {
            _sources.Remove(serverId, out removed);
            foreach (var window in _windows.Values)
                foreach (var entry in window.Candidates.Where(p => p.Value.Matcher.ServerId == serverId).ToArray())
                    window.Candidates.Remove(entry.Key);
            foreach (var entry in _windows.Where(p => p.Value.Candidates.Count == 0).ToArray()) _windows.Remove(entry.Key);
        }
        removed?.DetachWantedCoordinator();
    }

    public void Invalidate(string serverId, string release)
    {
        lock (_sync)
        {
            foreach (var candidate in _dispatching.Where(c => c.Matcher.ServerId == serverId &&
                         c.Release.Equals(release, StringComparison.OrdinalIgnoreCase))) candidate.Revoked = true;
            foreach (var window in _windows.Values)
                foreach (var pair in window.Candidates.Where(p => p.Value.Matcher.ServerId == serverId &&
                    p.Value.Release.Equals(release, StringComparison.OrdinalIgnoreCase)).ToArray())
                    window.Candidates[pair.Key] = pair.Value with { CompleteAt = null };
        }
    }

    private bool StillEligible(string itemId, Candidate candidate)
    {
        lock (_sync)
        {
            var item = _wishlist.GetById(itemId);
            return item != null && IsReady(candidate, _utcNow()) &&
                Evaluate(item, candidate.Release, candidate.SizeBytes).Accepted;
        }
    }

    private bool IsReady(Candidate candidate, DateTimeOffset now)
    {
        // Match B7's maximum fact lifetime, independently of the longer window
        // candidate retention. A newer state can revoke a completion at any time.
        return !candidate.Revoked && Allowed(candidate) && candidate.CompleteAt >= now.AddHours(-6) && candidate.CompleteAt <= now &&
            _sources.TryGetValue(candidate.Matcher.ServerId, out var registered) &&
            ReferenceEquals(registered, candidate.Matcher) &&
            (CompletionStillValid?.Invoke(candidate.Matcher.ServerId, candidate.Release) ?? true);
    }

    private static bool Allowed(Candidate candidate)
    {
        try { return candidate.AcquisitionAllowed?.Invoke() ?? true; }
        catch { return false; }
    }

    private IEnumerable<(Candidate Candidate, int Score)> Ranked(Window window, DateTimeOffset now)
    {
        var item = _wishlist.GetById(window.ItemId);
        if (item == null) return [];
        return window.Candidates.Values
            .Where(c => IsReady(c, now))
            .Select(c => (Candidate: c, Decision: Evaluate(item, c.Release, c.SizeBytes)))
            .Where(c => c.Decision.Accepted)
            .OrderByDescending(c => c.Decision.Score)
            .ThenByDescending(c => _config.PreferFastestWantedSource ? SafeSpeed(c.Candidate.Matcher.ServerId) : 0)
            .ThenBy(c => c.Candidate.FirstSeen)
            .ThenBy(c => c.Candidate.Release, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Candidate.Matcher.ServerId, StringComparer.Ordinal)
            .Select(c => (c.Candidate, c.Decision.Score));
    }

    private double SafeSpeed(string serverId)
    {
        var speed = SourceSpeed?.Invoke(serverId) ?? 0;
        return double.IsFinite(speed) && speed > 0 ? speed : 0;
    }

    private (bool Accepted, int Score) Evaluate(WishlistItem item, string release, long? sizeBytes)
    {
        if (_config.ScoredQualityEnabled || item.MonitorMode != SeriesMonitorMode.Legacy)
        {
            var result = _wishlist.EvaluateScoredRelease(item.Id, release, _config, sizeBytes);
            return (result.Accepted, result.Score);
        }
        if (item.Dead || item.Status != WishlistStatus.Watching) return (false, 0);
        var matches = item.Type == MediaType.Movie
            ? SceneNameParser.MatchesMovie(release, item.Title, item.Year, item.Quality)
            : SceneNameParser.MatchesTvEpisode(release, item.Title, item.Quality);
        if (!matches) return (false, 0);
        var keys = WishlistAcquisition.Keys(item, release);
        // In unscored mode, choose one accepted variant per movie/episode; an
        // arbitrary quality ranking would silently enable the B1 scorer.
        var alreadyGrabbed = _wishlist.Items.Where(other => WishlistAcquisition.SameTitle(item, other))
            .SelectMany(other => other.GrabbedReleases)
            .Any(previous => WishlistAcquisition.Keys(item, previous)
                .Any(previousKey => keys.Any(key => WishlistAcquisition.Overlaps(key, previousKey))));
        return (keys.Count != 0 && !alreadyGrabbed, 0);
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = now.AddHours(-Math.Clamp(_config.WantedCandidateMaxAgeHours, 1, 168));
        foreach (var pair in _windows.Where(p => p.Value.Opened < oldest ||
                     _wishlist.GetById(p.Value.ItemId) is not { Dead: false, Status: not WishlistStatus.Paused }).ToArray())
            _windows.Remove(pair.Key);
    }

    private static bool SafeCandidate(string release, string path) => !string.IsNullOrWhiteSpace(release) &&
        !string.IsNullOrWhiteSpace(path) && release.Length <= 1024 &&
        IrcAnnounceListener.IsPlausibleReleaseName(release) && !release.StartsWith('.') &&
        !release.Any(c => c is '/' or '\\' || char.IsControl(c)) &&
        path.StartsWith('/') && path.Length <= 4096 && !path.Any(char.IsControl) &&
        !path.Split('/').Any(segment => segment is "." or "..");
}
