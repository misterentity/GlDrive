namespace GlDrive.Spread;

public partial class SpreadManager
{
    public SitebotFactStore SitebotFacts { get; } = new();

    /// <summary>Called only by the trusted channel decoder; captures evidence without scheduling work.</summary>
    public void ObserveSitebot(SitebotEvent observation)
    {
        if (_disposed) return;
        if (!SitebotFacts.Observe(observation)) return;
        var changed = false;
        lock (_lock)
        {
            // Size observations refine existing requests only. They never admit
            // work, change a retry deadline, wake a parked probe or add a login.
            foreach (var queue in new[] { _raceQueue, _startingRequests })
                for (var i = 0; i < queue.Count; i++)
                    if (string.Equals(queue[i].ReleaseName, observation.Release, StringComparison.OrdinalIgnoreCase))
                    {
                        var updated = PredictRaceNoLock(queue[i]);
                        changed |= !ReferenceEquals(updated, queue[i]);
                        queue[i] = updated;
                    }
            foreach (var (job, request) in _activeRequests.ToArray())
                if (string.Equals(request.ReleaseName, observation.Release, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = PredictRaceNoLock(request);
                    changed |= !ReferenceEquals(updated, request);
                    _activeRequests[job] = updated;
                }
        }
        if (changed) PersistQueue();
    }
}
