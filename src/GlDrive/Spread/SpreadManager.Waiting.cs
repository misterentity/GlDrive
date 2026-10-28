using GlDrive.Ftp;
using GlDrive.Services;

namespace GlDrive.Spread;

public partial class SpreadManager
{
    public IReadOnlyDictionary<string, WaitingState> GetQueuedWaitingStates()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var durations = _history.Items.Where(h => h.CleanComplete && h.CompletedAt > h.StartedAt)
                .Select(h => (h.CompletedAt - h.StartedAt).TotalSeconds).Order().ToArray();
            double? median = durations.Length == 0 ? null : durations[durations.Length / 2];
            var slots = Math.Max(1, _config.Spread.MaxConcurrentRaces);
            var positions = EligibleQueueIndicesNoLock(now).Select((index, position) => (index, position: position + 1))
                .ToDictionary(p => p.index, p => p.position);
            var result = new Dictionary<string, WaitingState>();
            for (var index = 0; index < _raceQueue.Count; index++)
            {
                var race = _raceQueue[index];
                int? position = positions.TryGetValue(index, out var queuePosition) ? queuePosition : null;
                var constraints = race.ServerIds.Select(id => _config.Servers.FirstOrDefault(s => s.Id == id))
                    .OfType<GlDrive.Config.ServerConfig>().Select(server =>
                    {
                        var gate = ServerLoginGateRegistry.TryGet(server.Connection.Host, server.Connection.Port, server.Connection.Username);
                        return gate == null ? null : $"{server.Name}: transfer ceiling {gate.PriorityLimit}, login slots {gate.Held}/{gate.Limit}";
                    }).OfType<string>();
                var reason = race.CleanupOnly ? "owned-partial-cleanup" : race.NextProbeAtUtc > now ? "race-recheck" :
                    position == null ? "race-site-availability" : "race-slot";
                var detail = (race.WaitReason ?? $"Race slots {_activeJobs.Count + _startingRequests.Count}/{slots}") +
                    "; " + string.Join("; ", constraints);
                // A retry deadline says when to check, not when the source will be complete.
                var eta = race.NextProbeAtUtc != null || position == null ? null :
                    EstimateQueueReady(now, position.Value, _activeJobs.Count + _startingRequests.Count, slots, median);
                result[race.RequestId] = new(reason, detail.TrimEnd(' ', ';'), race.NextProbeAtUtc, eta, position);
            }
            return result;
        }
    }

    internal static DateTimeOffset? EstimateQueueReady(DateTimeOffset now, int position, int active, int slots, double? medianSeconds)
    {
        if (position < 1 || slots < 1 || medianSeconds is not > 0 || !double.IsFinite(medianSeconds.Value)) return null;
        var waves = (active + position - 1) / slots;
        var seconds = waves * medianSeconds.Value;
        return double.IsFinite(seconds) && seconds <= (DateTimeOffset.MaxValue - now).TotalSeconds
            ? now.AddSeconds(seconds) : null;
    }
}
