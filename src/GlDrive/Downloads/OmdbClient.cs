using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>
/// Movie metadata client. Prefers paid OMDB when an API key is configured,
/// otherwise falls back to free keyless imdbapi.dev (RaceTrade-style).
/// </summary>
public class OmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _freeHttp;
    private readonly Func<string> _apiKeySource;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ProviderState _paidState = new();
    private readonly ProviderState _freeState = new();
    internal static readonly TimeSpan OutageRetryDelay = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OmdbClient(string apiKey) : this(() => apiKey) { }

    /// <summary>Reads the key on every request, so one long-lived client follows Settings changes.</summary>
    internal OmdbClient(Func<string> apiKey, HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _apiKeySource = apiKey;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _freeHttp = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri("https://www.omdbapi.com/");
        _freeHttp.BaseAddress = new Uri("https://api.imdbapi.dev/");
        _http.Timeout = _freeHttp.Timeout = TimeSpan.FromSeconds(15);
    }

    private string ApiKey => _apiKeySource() ?? "";
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// True when either OMDB (paid) or imdbapi.dev (free) backend is available.
    /// Always true since imdbapi.dev requires no key.
    /// </summary>
    public bool IsAvailable => true;

    public async Task<OmdbMovie[]> Search(string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return [];
        var key = ApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            var url = $"?apikey={Uri.EscapeDataString(key)}&s={Uri.EscapeDataString(query)}&type=movie";
            var result = await Request<OmdbSearchResponse>(_http, _paidState, url, "OMDB", key, ct);
            if (result?.Search is { Length: > 0 }) return result.Search;
        }

        // Fallback: imdbapi.dev /search/titles?query=...
        var free = await Request<ImdbApiDevSearchResponse>(_freeHttp, _freeState,
            $"search/titles?query={Uri.EscapeDataString(query)}", "imdbapi.dev", "", ct);
        return free?.Titles == null ? [] : Array.ConvertAll(free.Titles, ImdbApiDevMapper.ToOmdbMovie);
    }

    public async Task<OmdbMovie?> GetById(string imdbId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(imdbId)) return null;
        var key = ApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            var url = $"?apikey={Uri.EscapeDataString(key)}&i={Uri.EscapeDataString(imdbId)}";
            var movie = await Request<OmdbMovie>(_http, _paidState, url, "OMDB", key, ct);
            if (movie != null && !string.IsNullOrEmpty(movie.Title)) return movie;
        }

        // Fallback: imdbapi.dev /titles/{id}
        var title = await Request<ImdbApiDevTitle>(_freeHttp, _freeState,
            $"titles/{Uri.EscapeDataString(imdbId)}", "imdbapi.dev", "", ct);
        return title != null ? ImdbApiDevMapper.ToOmdbMovie(title) : null;
    }

    private async Task<T?> Request<T>(HttpClient http, ProviderState state, string url,
        string provider, string credentialIdentity, CancellationToken ct) where T : class
    {
        // Search and detail share one gate per provider. Concurrent callers cannot
        // all probe an outage, and a changed paid key can recover immediately.
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (state.CredentialIdentity != credentialIdentity)
            {
                state.CredentialIdentity = credentialIdentity;
                state.NextAttempt = DateTimeOffset.MinValue;
                state.Failures = 0;
            }
            if (_utcNow() < state.NextAttempt) return null;
            try
            {
                using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    state.Failures = 0;
                    state.NextAttempt = DateTimeOffset.MinValue;
                    return null;
                }
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
                ct.ThrowIfCancellationRequested();
                state.Failures = 0;
                state.NextAttempt = DateTimeOffset.MinValue;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation is not provider failure and must not launch
                // the fallback or stop the next caller from using this provider.
                throw;
            }
            catch (Exception ex)
            {
                state.NextAttempt = _utcNow() + OutageRetryDelay;
                if (++state.Failures == 1)
                    Log.Warning(ex, "Metadata provider {Provider} unavailable; retrying after {Delay}", provider, OutageRetryDelay);
                else
                    Log.Warning("Metadata provider {Provider} remains unavailable ({FailureType}); retrying after {Delay}",
                        provider, ex.GetType().Name, OutageRetryDelay);
                return null;
            }
        }
        finally { state.Gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _freeHttp.Dispose();
    }

    private sealed class ProviderState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? CredentialIdentity { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public int Failures { get; set; }
    }
}

internal class ImdbApiDevSearchResponse
{
    public ImdbApiDevTitle[]? Titles { get; set; }
}

internal class ImdbApiDevTitle
{
    public string? Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("primaryTitle")]
    public string? PrimaryTitle { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("startYear")]
    public int? StartYear { get; set; }
    public ImdbApiDevImage? PrimaryImage { get; set; }
    public string? Plot { get; set; }
    public ImdbApiDevRating? Rating { get; set; }
    public string[]? Genres { get; set; }
}

internal class ImdbApiDevImage
{
    public string? Url { get; set; }
}

internal class ImdbApiDevRating
{
    public double? Aggregate { get; set; }
}

internal static class ImdbApiDevMapper
{
    public static OmdbMovie ToOmdbMovie(ImdbApiDevTitle t) => new()
    {
        Title = t.PrimaryTitle ?? "",
        Year = t.StartYear?.ToString() ?? "",
        ImdbID = t.Id,
        Poster = t.PrimaryImage?.Url,
        Plot = t.Plot,
        imdbRating = t.Rating?.Aggregate is { } r ? Math.Round(r, 1).ToString("F1") : null,
        Genre = t.Genres is { Length: > 0 } ? string.Join(", ", t.Genres) : null,
    };
}

public class OmdbSearchResponse
{
    public OmdbMovie[]? Search { get; set; }
    public string? TotalResults { get; set; }
    public string? Response { get; set; }
}

public class OmdbMovie
{
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string? ImdbID { get; set; }
    public string? Poster { get; set; }
    public string? Plot { get; set; }
    public string? imdbRating { get; set; }
    public string? Genre { get; set; }
    public string? Rated { get; set; }

    public int? YearParsed => int.TryParse(Year?.Split('–')[0], out var y) ? y : null;
}
