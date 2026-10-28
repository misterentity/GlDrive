namespace GlDrive.Spread;

public enum SitebotEventKind { New, Complete, Progress, Halfway, Race, Nuke, Unnuke, Metadata }

/// <summary>Advisory observations from an explicitly trusted channel bot, never filesystem ownership.</summary>
public sealed record SitebotEvent
{
    public string ServerId { get; init; } = "";
    public string Channel { get; init; } = "";
    public string BotNick { get; init; } = "";
    public string UserHost { get; init; } = "";
    public DateTime ObservedAtUtc { get; init; }
    public SitebotEventKind Kind { get; init; }
    public string Section { get; init; } = "";
    public string Release { get; init; } = "";
    public long? Bytes { get; init; }
    public int? Files { get; init; }
    public double? SpeedBytesPerSecond { get; init; }
    public string? Leader { get; init; }
    public string? Winner { get; init; }
    public bool Solo { get; init; }
    public string? Actor { get; init; }
    public IReadOnlyList<string> Nukees { get; init; } = [];
    public int? NukeFactor { get; init; }
    public string? Reason { get; init; }
    public string? ImdbId { get; init; }
    public string? Network { get; init; }
    public string? ShowStatus { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public double? ImdbRating { get; init; }
    public int? ImdbVotes { get; init; }
}

public sealed class SitebotChannelConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Exact bot nick. Empty leaves passive decoding inactive.</summary>
    public string BotNick { get; set; } = "";
    /// <summary>Optional user@host wildcard; only * and ? are special.</summary>
    public string UserHostPattern { get; set; } = "";
    public List<SitebotTemplate> Templates { get; set; } = [];
}

public sealed class SitebotTemplate
{
    public SitebotEventKind Kind { get; set; }
    /// <summary>Anchored nonbacktracking regex with section/release captures; optional fields are documented.</summary>
    public string Pattern { get; set; } = "";
}
