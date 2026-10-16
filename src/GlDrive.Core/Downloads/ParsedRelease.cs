namespace GlDrive.Downloads;

public enum QualityProfile { Any, SD, Q720p, Q1080p, Q2160p }

public record ParsedRelease(
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    QualityProfile Quality,
    string? Group,
    bool IsSeasonPack,
    string? Source = null);
