namespace GlDrive.AiAgent;

public sealed class RacesDigester
{
    public RacesDigest Build(IEnumerable<RaceOutcomeEvent> events)
    {
        var list = events.ToList();
        var d = new RacesDigest { TotalRaces = list.Count, KnownWinnerRaces = list.Count(HasObservedWinner) };
        if (list.Count == 0) return d;

        // Missing winners are unavailable observations, not losses. Reporting
        // fabricated 0% rates caused the agent to disable a healthy destination.
        var serverParticipation = list
            .SelectMany(r => r.Participants.Select(p => p.ServerId).Distinct(StringComparer.Ordinal).Select(id => (ServerId: id,
                known: HasObservedWinner(r, id), won: IsOwnWin(r, id))))
            .GroupBy(x => x.ServerId);
        foreach (var g in serverParticipation)
        {
            var observed = g.Where(x => x.known).ToList();
            d.ParticipationByServer[g.Key] = g.Count();
            d.KnownWinnerSamplesByServer[g.Key] = observed.Count;
            if (observed.Count > 0)
                d.WinRateByServer[g.Key] = (double)observed.Count(x => x.won) / observed.Count;
        }

        // Final ownership includes contributions from other racers; it shows
        // destination payloads, not a winner or exact bytes uploaded by us.
        foreach (var g in list.SelectMany(r => r.Participants).Where(p => p.Role == "dst").GroupBy(p => p.ServerId))
        {
            d.DestinationRacesWithFilesByServer[g.Key] = g.Count(p => p.Files > 0);
            d.DestinationFilesObservedByServer[g.Key] = g.Sum(p => (long)Math.Max(0, p.Files));
        }

        // Per-route kbps average (src->dst pairs)
        var routeKbps = new Dictionary<string, List<double>>();
        foreach (var r in list)
        {
            var srcId = r.Participants.FirstOrDefault(p => p.Role == "src")?.ServerId;
            if (string.IsNullOrEmpty(srcId)) continue;
            foreach (var p in r.Participants.Where(p => p.Role == "dst"))
            {
                var key = $"{srcId}->{p.ServerId}";
                if (!routeKbps.TryGetValue(key, out var bag)) routeKbps[key] = bag = new List<double>();
                if (p.AvgKbps > 0) bag.Add(p.AvgKbps);
            }
        }
        foreach (var (k, v) in routeKbps)
            d.KbpsByRoute[k] = v.Count == 0 ? 0 : v.Average();

        // Abort reason histogram (counts non-null AbortReason values across participants)
        foreach (var g in list.SelectMany(r => r.Participants)
                              .Where(p => !string.IsNullOrEmpty(p.AbortReason))
                              .GroupBy(p => p.AbortReason!))
            d.AbortReasonHistogram[g.Key] = g.Count();

        // Completion rate per section
        foreach (var g in list.GroupBy(r => r.Section))
        {
            var items = g.ToList();
            var complete = items.Count(r => r.Result == "complete");
            d.CompletionRateBySection[g.Key] = items.Count == 0 ? 0 : (double)complete / items.Count;
        }

        return d;
    }

    internal static bool HasObservedWinner(RaceOutcomeEvent race)
        => HasLegacyWinner(race) || race.Participants.Any(p => HasObservedWinner(race, p.ServerId));

    internal static bool HasObservedWinner(RaceOutcomeEvent race, string serverId)
    {
        var destination = ObservedDestination(race, serverId);
        return destination != null || HasLegacyWinner(race);
    }

    internal static bool IsOwnWin(RaceOutcomeEvent race, string serverId)
    {
        var destination = ObservedDestination(race, serverId);
        return destination != null ? GlDrive.Spread.SitebotOutcomePolicy.IsOwnWin(destination)
            : HasLegacyWinner(race) && race.Winner == serverId;
    }

    private static GlDrive.Spread.RaceDestinationOutcome? ObservedDestination(RaceOutcomeEvent race, string serverId)
    {
        if (!race.Participants.Any(p => p.ServerId == serverId && p.Role == "dst")) return null;
        var outcomes = (race.DestinationOutcomes ?? []).Where(d => d.ServerId == serverId).ToArray();
        return outcomes.Length == 1 && GlDrive.Spread.SitebotOutcomePolicy.HasObservedWinner(outcomes[0]) ? outcomes[0] : null;
    }

    private static bool HasLegacyWinner(RaceOutcomeEvent race)
        => !string.IsNullOrWhiteSpace(race.Winner)
           && race.Participants.Any(p => p.ServerId == race.Winner);
}
