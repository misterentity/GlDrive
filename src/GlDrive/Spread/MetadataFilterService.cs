using System.Collections.Concurrent;
using GlDrive.Config;
using GlDrive.Downloads;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// RaceTrade-style metadata filtering. Reuses existing OmdbClient (with free
/// imdbapi.dev fallback) and TvMazeClient — no new API clients or keys needed.
///
/// Fails OPEN: if lookup times out or errors, the release is allowed through.
/// Rationale: network flakiness must not block racing.
/// </summary>
public class MetadataFilterService : IDisposable
{
    private readonly AppConfig _appConfig;
    private readonly OmdbClient _omdb;
    private readonly TvMazeClient _tvMaze;
    private readonly SitebotFactStore? _sitebotFacts;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    public MetadataFilterService(AppConfig appConfig, SitebotFactStore? sitebotFacts = null)
    {
        _appConfig = appConfig;
        _sitebotFacts = sitebotFacts;
        _omdb = new OmdbClient(appConfig.Downloads.ResolveOmdbKey());
        _tvMaze = new TvMazeClient();
    }

    public readonly record struct FilterVerdict(bool Allowed, string Reason);

    public async Task<FilterVerdict> EvaluateAsync(
        MetadataFilterConfig config,
        string releaseName,
        ParsedRelease parsed,
        CancellationToken ct = default, string? sourceServerId = null)
    {
        if (!config.Enabled)
            return new FilterVerdict(true, "filter disabled");

        // Only a trusted, exact server/release fact may replace the HTTP lookup,
        // and only when it contains all fields needed by the active filters.
        if (_appConfig.Spread.UseSitebotMetadata && sourceServerId != null &&
            _sitebotFacts?.TryGet(sourceServerId, releaseName)?.Metadata is { } metadata &&
            EvaluateSitebotMetadata(config, metadata, releaseName) is { } hinted)
            return hinted;

        var cacheKey = $"{parsed.Title}|{parsed.Year}|{parsed.Season}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.IsFresh)
            return ApplyThresholds(config, cached.Metadata, releaseName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.LookupTimeoutSeconds)));

        try
        {
            var meta = parsed.Season is > 0 || parsed.Episode is > 0
                ? await LookupTv(parsed, cts.Token)
                : await LookupMovie(parsed, cts.Token);

            if (meta is null)
            {
                // No metadata found — fail open with a note
                return new FilterVerdict(true, "no metadata found (fail-open)");
            }

            _cache[cacheKey] = new CacheEntry(meta, DateTime.UtcNow);
            return ApplyThresholds(config, meta, releaseName);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Metadata filter lookup timed out for {Release}", releaseName);
            return new FilterVerdict(true, "lookup timed out (fail-open)");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Metadata filter lookup failed for {Release}", releaseName);
            return new FilterVerdict(true, "lookup error (fail-open)");
        }
    }

    private async Task<ReleaseMetadata?> LookupMovie(ParsedRelease parsed, CancellationToken ct)
    {
        var results = await _omdb.Search(parsed.Title, ct);
        if (results.Length == 0) return null;

        // Prefer year match when available
        var match = parsed.Year.HasValue
            ? results.FirstOrDefault(r => r.YearParsed == parsed.Year)
            : results[0];
        match ??= results[0];

        // OMDB search returns minimal data — for rating/genre we need GetById
        if (!string.IsNullOrEmpty(match.ImdbID))
        {
            var detailed = await _omdb.GetById(match.ImdbID, ct);
            if (detailed != null) match = detailed;
        }

        double? rating = null;
        if (double.TryParse(match.imdbRating, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r))
            rating = r;

        var genres = string.IsNullOrEmpty(match.Genre)
            ? Array.Empty<string>()
            : match.Genre.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ReleaseMetadata(
            Title: match.Title,
            Rating: rating,
            Votes: null,
            Genres: genres,
            IsEnded: false,
            Source: "imdb");
    }

    private async Task<ReleaseMetadata?> LookupTv(ParsedRelease parsed, CancellationToken ct)
    {
        var shows = await _tvMaze.Search(parsed.Title, ct);
        if (shows.Length == 0) return null;

        var show = shows[0];
        var rating = show.Rating?.Average;
        var genres = show.Genres ?? Array.Empty<string>();
        var status = show.Status ?? "";
        var isEnded = status.Equals("Ended", StringComparison.OrdinalIgnoreCase) ||
                      status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                      status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

        return new ReleaseMetadata(
            Title: show.Name ?? parsed.Title,
            Rating: rating,
            Votes: null,
            Genres: genres,
            IsEnded: isEnded,
            Source: "tvmaze");
    }

    internal static FilterVerdict? EvaluateSitebotMetadata(MetadataFilterConfig config, SitebotEvent metadata, string releaseName)
    {
        if (metadata.Kind != SitebotEventKind.Metadata ||
            !string.Equals(metadata.Release, releaseName, StringComparison.OrdinalIgnoreCase)) return null;
        if (config.MinImdbRating > 0 && metadata.ImdbRating is not (>= 0 and <= 10)) return null;
        if (config.MinVotes > 0 && metadata.ImdbVotes is not >= 0) return null;
        var genres = metadata.Genres;
        if ((!string.IsNullOrWhiteSpace(config.AllowGenres) || !string.IsNullOrWhiteSpace(config.DenyGenres)) && genres.Count == 0)
            return null;
        var status = metadata.ShowStatus?.Trim().ToLowerInvariant();
        if (config.SkipEndedShows && status is not ("running" or "ended" or "canceled" or "cancelled" or "in development" or "to be determined"))
            return null;
        return ApplyThresholds(config, new ReleaseMetadata(releaseName, metadata.ImdbRating, metadata.ImdbVotes,
            genres, status is "ended" or "canceled" or "cancelled", "trusted sitebot"), releaseName);
    }

    private static FilterVerdict ApplyThresholds(
        MetadataFilterConfig config, ReleaseMetadata meta, string releaseName)
    {
        // Rating threshold
        if (config.MinImdbRating > 0)
        {
            if (meta.Rating is null)
                return new FilterVerdict(true, "no rating data (fail-open)");
            if (meta.Rating < config.MinImdbRating)
                return new FilterVerdict(false,
                    $"rating {meta.Rating:F1} < {config.MinImdbRating:F1}");
        }

        // Votes threshold (only if provider reports it — OMDB does, imdbapi.dev currently doesn't)
        if (config.MinVotes > 0 && meta.Votes is { } votes && votes < config.MinVotes)
            return new FilterVerdict(false, $"votes {votes} < {config.MinVotes}");

        // Deny-genres take precedence
        var deny = SplitList(config.DenyGenres);
        if (deny.Count > 0 && meta.Genres.Any(g => deny.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            var matched = meta.Genres.First(g => deny.Contains(g, StringComparer.OrdinalIgnoreCase));
            return new FilterVerdict(false, $"denied genre '{matched}'");
        }

        // Allow-genres: if set, at least one must match
        var allow = SplitList(config.AllowGenres);
        if (allow.Count > 0 && !meta.Genres.Any(g => allow.Contains(g, StringComparer.OrdinalIgnoreCase)))
            return new FilterVerdict(false,
                $"no matching genres (have: {string.Join(", ", meta.Genres)})");

        // Skip ended shows
        if (config.SkipEndedShows && meta.IsEnded)
            return new FilterVerdict(false, "show is ended/cancelled");

        return new FilterVerdict(true,
            $"pass (rating={meta.Rating?.ToString("F1") ?? "?"}, genres={string.Join(",", meta.Genres)}, src={meta.Source})");
    }

    private static List<string> SplitList(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public void Dispose()
    {
        _omdb.Dispose();
        _tvMaze.Dispose();
        GC.SuppressFinalize(this);
    }

    private record ReleaseMetadata(
        string Title, double? Rating, int? Votes,
        IReadOnlyList<string> Genres, bool IsEnded, string Source);

    private record CacheEntry(ReleaseMetadata Metadata, DateTime At)
    {
        public bool IsFresh => DateTime.UtcNow - At < CacheTtl;
    }
}
