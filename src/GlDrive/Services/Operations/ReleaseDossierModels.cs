using GlDrive.Services.Statistics;

namespace GlDrive.Services.Operations;

public sealed record DossierEvent(DateTimeOffset Time, string Stage, string ServerId, string Detail);
public sealed record DossierPrediction(string ServerId, string Stage, string Verdict, string Detail);
public sealed record ReleaseDossier(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Release,
    IReadOnlyList<DossierEvent> Timeline, IReadOnlyList<DossierPrediction> Predictions, TelemetryReadCoverage Coverage)
{
    public IReadOnlyList<string> Notes { get; } =
    [
        "Missing events mean not observed in the retained evidence, never absent from a site.",
        "Predictions evaluate local configuration only. Source presence, completion, metadata and current admission remain unverified.",
        "No race, download, login, provider request or site probe is started by this report."
    ];
}

internal sealed record DossierLocalSnapshot(IReadOnlyList<DossierEvent> Events,
    IReadOnlyList<GlDrive.Downloads.WishlistItem> Wishlist, bool Partial, bool WishlistPartial = true);
