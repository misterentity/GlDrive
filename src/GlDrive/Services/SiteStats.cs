namespace GlDrive.Services;

/// <summary>
/// Per-server stat snapshot scraped from glftpd's SITE STATS reply.
/// Both fields nullable — heuristic regex may fail on exotic site themes.
/// </summary>
public sealed record SiteStats(string? Credits, string? Ratio, DateTime UpdatedAt);
