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

    /// <summary>Apply buffered hints at normal discovery/scan boundaries, without issuing any command.</summary>
    internal int ApplyPendingSourceHints(Dictionary<string, string>? discoveryPaths = null, HashSet<string>? discoverySources = null)
    {
        if (!_spreadConfig.MergeRaceAnnounces || _cleanupOnly || _cts.IsCancellationRequested) return 0;
        var added = 0;
        foreach (var (id, hint) in _announcedSources.ToArray())
        {
            var config = ServerConfigResolver?.Invoke(id) ?? _serverConfigs.GetValueOrDefault(id);
            if (!RaceAnnounceMerge.CanSource(config)) { RemoveSourceHint(id, hint); continue; }
            var path = RaceAnnounceMerge.ResolveSourcePath(config!, hint, ReleaseName);
            if (path == null) continue;
            var mapping = SectionMapper.Resolve(config!.SpreadSite, hint.Section, ReleaseName);
            var action = _skiplist.EvaluateTiered(ReleaseName, true, false, mapping?.RemoteSection ?? hint.Section,
                config.SpreadSite.Skiplist, mapping?.TagRules ?? Array.Empty<SkiplistRule>(), _spreadConfig.GlobalSkiplist,
                [], SceneNameParser.Parse(ReleaseName));
            if (action == SkiplistAction.Deny) { RemoveSourceHint(id, hint); continue; }
            var pool = ResolveSpreadPool(id);
            if (pool == null) continue; // existing recovery owns reconnects
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
                    _serverConfigs[id] = config;
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
                        _siteProgress[id] = progress = new SiteProgress { ServerId = id, ServerName = config.Name };
                    progress.IsSource = true;
                }
                RemoveSourceHint(id, hint);
                added++;
                Log.Information("Spread: merged source hint for {Release} from {Server}; awaiting scheduled listing", ReleaseName, config.Name);
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
