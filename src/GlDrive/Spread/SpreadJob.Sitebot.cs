namespace GlDrive.Spread;

public partial class SpreadJob
{
    internal SitebotFactStore? SitebotFacts { get; set; }
    internal DateTime? QueuedAtUtc { get; set; }
    public double? QueueWaitSeconds => QueuedAtUtc.HasValue ? Math.Max(0, (StartedAt - QueuedAtUtc.Value).TotalSeconds) : null;
    // Only successful existing LISTs populate this map. Protected by _ownershipLock.
    private readonly Dictionary<string, DateTime> _sitebotListingStartedAtUtc = new(StringComparer.Ordinal);

    public List<RaceDestinationOutcome> DestinationOutcomes()
    {
        List<(string Id, string User, bool Complete, int Owned, DateTime? ListingAt)> destinations;
        lock (_ownershipLock)
        {
            var participants = _sitePathsRef?.Keys ?? (IEnumerable<string>)_siteProgress.Keys;
            destinations = participants.Where(id => IsReceivingSite(id) && !IsUnopenedFillOnlyNoLock(id))
                .Select(id => (id, _serverConfigs.GetValueOrDefault(id)?.Connection.Username ?? "",
                    _destStates.GetValueOrDefault(id) == DestState.Complete && _serverFileCount.GetValueOrDefault(id) > 0
                    && HasExpectedFilesNoLock(id) && !_destHasMissingStub.GetValueOrDefault(id),
                    _serverFileCount.GetValueOrDefault(id),
                    _sitebotListingStartedAtUtc.TryGetValue(id, out var at) ? (DateTime?)at : null)).ToList();
        }
        var now = DateTime.UtcNow;
        return destinations.Select(d => SitebotOutcomePolicy.Evaluate(d.Id, d.User,
            SitebotFacts?.TryGet(d.Id, ReleaseName), StartedAt, now, d.ListingAt, d.Complete, d.Owned,
            _spreadConfig.UseSitebotCompletionHints)).ToList();
    }
}
