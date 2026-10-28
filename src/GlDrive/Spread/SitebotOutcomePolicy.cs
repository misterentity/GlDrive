using System.Text.Json.Serialization;

namespace GlDrive.Spread;

public sealed record RaceDestinationOutcome
{
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "unknown";
    [JsonPropertyName("winnerUser")] public string? WinnerUser { get; init; }
    [JsonPropertyName("winnerObservedAtUtc")] public DateTime? WinnerObservedAtUtc { get; init; }
    [JsonPropertyName("completionVerified")] public bool CompletionVerified { get; init; }
    [JsonPropertyName("solo")] public bool Solo { get; init; }
    [JsonPropertyName("announcedBytes")] public long? AnnouncedBytes { get; init; }
    [JsonPropertyName("announcedFiles")] public int? AnnouncedFiles { get; init; }
    [JsonPropertyName("channel")] public string? Channel { get; init; }
    [JsonPropertyName("inventoryMatchesAnnounce")] public bool? InventoryMatchesAnnounce { get; init; }
}

/// <summary>Final explicit bot winners are account identities, never legacy server-id winners.</summary>
public static class SitebotOutcomePolicy
{
    public static bool HasObservedWinner(RaceDestinationOutcome outcome) => outcome.CompletionVerified
        && !string.IsNullOrWhiteSpace(outcome.WinnerUser) && outcome.WinnerObservedAtUtc.HasValue
        && outcome.Outcome is "won" or "lost" or "solo";
    public static bool IsOwnWin(RaceDestinationOutcome outcome) => HasObservedWinner(outcome) && outcome.Outcome is "won" or "solo";

    public static RaceDestinationOutcome Evaluate(string serverId, string? accountName, SitebotReleaseFacts? facts,
        DateTime raceStartedAtUtc, DateTime nowUtc, DateTime? listingStartedAtUtc, bool completeFromListing,
        int ownedFiles, bool compareAnnouncedInventory = false)
    {
        var complete = facts?.Complete;
        // A new confirming LIST must have begun after the COMPLETE hint. Existing
        // scheduled scans provide this evidence; the hint never schedules a scan.
        var verified = completeFromListing && listingStartedAtUtc.HasValue;
        var result = new RaceDestinationOutcome
        {
            ServerId = serverId, CompletionVerified = verified,
            AnnouncedBytes = complete?.Bytes, AnnouncedFiles = complete?.Files,
            Channel = complete?.Channel,
            InventoryMatchesAnnounce = compareAnnouncedInventory && complete?.Files is > 0 && listingStartedAtUtc >= complete.ObservedAtUtc
                ? ownedFiles >= complete.Files : null
        };
        if (!verified || complete == null || facts!.ConflictingWinner || complete.Kind != SitebotEventKind.Complete
            || complete.ObservedAtUtc < raceStartedAtUtc || complete.ObservedAtUtc > nowUtc
            || listingStartedAtUtc < complete.ObservedAtUtc || string.IsNullOrWhiteSpace(complete.Winner)
            || string.IsNullOrWhiteSpace(accountName)) return result;
        var own = string.Equals(complete.Winner, accountName, StringComparison.OrdinalIgnoreCase);
        return result with
        {
            WinnerUser = complete.Winner, WinnerObservedAtUtc = complete.ObservedAtUtc, Solo = complete.Solo,
            Outcome = own ? complete.Solo ? "solo" : "won" : "lost"
        };
    }
}
