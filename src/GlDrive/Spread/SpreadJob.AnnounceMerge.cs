using System.Collections.Concurrent;
using GlDrive.Config;
using GlDrive.Downloads;
using Serilog;

namespace GlDrive.Spread;

public partial class SpreadJob
{
    private readonly ConcurrentDictionary<string, RaceSourceHint> _announcedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unverifiedAnnouncedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attemptedAnnouncedSources = new(StringComparer.Ordinal);
    internal Func<string, string, CancellationToken, Task<bool>>? SourceHintDirectoryExists { get; set; }

    // Admission may hold the manager lock. Buffer only: no pool/config callbacks
    // or job locks here (source migration takes those in the opposite order).
    internal void QueueSourceHints(IEnumerable<RaceSourceHint> hints)
    {
        foreach (var hint in hints.Take(100))
            if (_announcedSources.Count < 100 || _announcedSources.ContainsKey(hint.ServerId))
                _announcedSources.AddOrUpdate(hint.ServerId, hint,
                    (_, _) => hint); // manager already chose the canonical, best available hint
    }

    internal IReadOnlyList<RaceSourceHint> PendingSourceHints => _announcedSources.Values.ToArray();

    private void RemoveSourceHint(string id, RaceSourceHint hint) =>
        ((ICollection<KeyValuePair<string, RaceSourceHint>>)_announcedSources).Remove(new(id, hint));

    private bool CanApplySourceHint(ServerConfig? config, RaceSourceHint hint)
    {
        if (!RaceAnnounceMerge.CanSource(config)) return false;
        var mapping = SectionMapper.Resolve(config!.SpreadSite, hint.Section, ReleaseName);
        return _skiplist.EvaluateTiered(ReleaseName, true, false, mapping?.RemoteSection ?? hint.Section,
            config.SpreadSite.Skiplist, mapping?.TagRules ?? Array.Empty<SkiplistRule>(), _spreadConfig.GlobalSkiplist,
            [], SceneNameParser.Parse(ReleaseName)) != SkiplistAction.Deny;
    }

    // A mapping or announcement is a candidate, not proof the directory exists.
    // Keep the ordinary watch-path fallback for both queued and active hints.
    internal static IReadOnlyList<string> SourceHintCandidatePaths(ServerConfig config, RaceSourceHint? hint,
        string section, string release)
    {
        if (string.IsNullOrWhiteSpace(release) || release.StartsWith('.') || release.Contains('/') ||
            release.Contains('\\') || release.Any(char.IsControl)) return [];
        var preferred = hint == null ? null : RaceAnnounceMerge.ResolveSourcePath(config, hint, release);
        return (preferred == null ? Enumerable.Empty<string>() : new[] { preferred })
            .Concat(CandidateBasePaths(config, hint?.Section ?? section).Select(p => p.TrimEnd('/') + "/" + release))
            .Distinct(StringComparer.Ordinal).Take(100).ToArray();
    }

    /// <summary>Verify buffered hints during the existing scan cadence before pinning a source.</summary>
    internal async Task<int> ApplyPendingSourceHintsAsync(CancellationToken ct = default,
        Dictionary<string, string>? discoveryPaths = null, HashSet<string>? discoverySources = null)
    {
        if (!_spreadConfig.MergeRaceAnnounces || _cleanupOnly || _cts.IsCancellationRequested) return 0;
        var added = 0;
        foreach (var (id, hint) in _announcedSources.ToArray())
        {
            var config = ServerConfigResolver?.Invoke(id) ?? _serverConfigs.GetValueOrDefault(id);
            if (!CanApplySourceHint(config, hint)) { RemoveSourceHint(id, hint); continue; }
            var pool = ResolveSpreadPool(id);
            if (pool == null) continue; // existing recovery owns reconnects
            lock (_migrationLock)
            {
                if ((discoveryPaths ?? _sitePathsRef)?.ContainsKey(id) == true || _extraSourcePaths.ContainsKey(id))
                {
                    RemoveSourceHint(id, hint);
                    continue;
                }
            }
            string? path = null;
            try
            {
                using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token,
                    _backgroundReads?.StopToken ?? default);
                probeBudget.CancelAfter(TimeSpan.FromSeconds(15));
                var candidates = SourceHintCandidatePaths(config!, hint, Section, ReleaseName);
                if (SourceHintDirectoryExists is { } probe)
                {
                    foreach (var candidate in candidates)
                        if (await probe(id, candidate, probeBudget.Token)) { path = candidate; break; }
                }
                else
                    path = await FindExistingSourcePath(id, candidates, probeBudget.Token, _backgroundReads?.StopToken ?? default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (_backgroundReads?.StopToken.IsCancellationRequested == true) { return added; }
            catch (Exception ex)
            {
                Log.Information("Spread: announced source probe deferred for {Server} ({Type}); retrying on the scan cadence", config!.Name, ex.GetType().Name);
                continue;
            }
            if (path == null) { RemoveSourceHint(id, hint); continue; }
            if (!_announcedSources.TryGetValue(id, out var current) || current != hint) continue;
            config = ServerConfigResolver?.Invoke(id) ?? _serverConfigs.GetValueOrDefault(id);
            if (!CanApplySourceHint(config, hint)) { RemoveSourceHint(id, hint); continue; }
            pool = ResolveSpreadPool(id);
            if (pool == null) continue;
            lock (_migrationLock)
            {
                if (_migrationClosed || _cts.IsCancellationRequested) return added;
                // Paths already participating in this attempt are pinned, including
                // restored owned destinations. A duplicate cannot repoint a writer.
                if ((discoveryPaths ?? _sitePathsRef)?.ContainsKey(id) == true || _extraSourcePaths.ContainsKey(id))
                {
                    RemoveSourceHint(id, hint);
                    continue;
                }
                lock (_ownershipLock)
                {
                    _serverConfigs[id] = config!;
                    _unverifiedAnnouncedSources.Add(id);
                    if (discoveryPaths != null)
                    {
                        discoveryPaths[id] = path;
                        discoverySources!.Add(id);
                    }
                    else
                    {
                        _extraSourcePaths[id] = path;
                        _sourceServersField.Add(id);
                    }
                }
                UpdatePools(new(_pools) { [id] = pool });
                lock (_progressLock)
                {
                    if (!_siteProgress.TryGetValue(id, out var progress))
                        _siteProgress[id] = progress = new SiteProgress { ServerId = id, ServerName = config!.Name };
                    progress.IsSource = true;
                }
                RemoveSourceHint(id, hint);
                added++;
                Log.Information("Spread: verified source hint for {Release} from {Server}; awaiting scheduled listing", ReleaseName, config!.Name);
            }
        }
        return added;
    }

    private bool HasUnverifiedAnnounceSources()
    {
        lock (_ownershipLock) return _unverifiedAnnouncedSources.Count > 0;
    }

    // A new hint gets one chance in the ordinary source scan cadence before a
    // waiting race is parked. It neither forces a LIST nor extends the lifetime.
    internal bool AwaitingFirstAnnounceScan()
    {
        lock (_ownershipLock) return _unverifiedAnnouncedSources.Any(id => !_attemptedAnnouncedSources.Contains(id));
    }

    internal void NoteAnnounceScanAttempt(string serverId)
    {
        lock (_ownershipLock)
            if (_unverifiedAnnouncedSources.Contains(serverId)) _attemptedAnnouncedSources.Add(serverId);
    }
}
