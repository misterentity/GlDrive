namespace GlDrive.Spread;

/// <summary>Nonpreemptive single-slot ordering; eligibility and resource admission remain with the manager.</summary>
internal static class RaceQueuePolicy
{
    internal static IReadOnlyList<int> Order(IEnumerable<(PersistedRace Request, int Index)> eligible,
        DateTime nowUtc, bool enabled, int maxAgeMinutes)
    {
        var maximumWait = TimeSpan.FromMinutes(Math.Clamp(maxAgeMinutes > 0 ? maxAgeMinutes : 120, 1, 1440));
        return eligible.Select(p => new
            {
                p.Index,
                Priority = RaceLifetimePolicy.QueuePriority(p.Request),
                Wait = WaitSeconds(p.Request.QueuedAtUtc, nowUtc),
                Ratio = ResponseRatio(p.Request.QueuedAtUtc, nowUtc, p.Request.PredictedSeconds)
            })
            // A5 cleanup deadlines retain priority; fresh work continues to outrank ordinary resumes.
            .OrderBy(p => p.Priority)
            .ThenByDescending(p => enabled && p.Wait >= maximumWait.TotalSeconds)
            .ThenByDescending(p => enabled && p.Wait >= maximumWait.TotalSeconds ? p.Wait : 0)
            .ThenByDescending(p => enabled && p.Wait < maximumWait.TotalSeconds ? p.Ratio : 0)
            .ThenBy(p => p.Index)
            .Select(p => p.Index).ToArray();
    }

    internal static double ResponseRatio(DateTime queuedAtUtc, DateTime nowUtc, double? predictedSeconds) =>
        1 + WaitSeconds(queuedAtUtc, nowUtc) / RaceWorkEstimator.ClampSeconds(predictedSeconds);

    private static double WaitSeconds(DateTime queuedAtUtc, DateTime nowUtc) => Math.Max(0, (nowUtc - queuedAtUtc).TotalSeconds);
}
