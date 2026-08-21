using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Downloads;

public class PreDbClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>
    /// Shared by both endpoints: they are the same host, so a 503 on one is evidence about the
    /// other. See <see cref="PreDbBackoff"/> for the outage that made this necessary.
    /// </summary>
    internal PreDbBackoff Backoff { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PreDbClient()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.predb.net/") };
        var version = typeof(PreDbClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GlDrive/{version}");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<PreDbRelease[]> SearchAsync(string query, int count = 100, int page = 0, CancellationToken ct = default)
    {
        if (Backoff.ShouldSkip(DateTimeOffset.UtcNow)) return [];

        try
        {
            var url = $"?q={Uri.EscapeDataString(query)}&count={count}&page={page}";
            var json = await _http.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<PreDbResponse>(json, JsonOptions);
            Backoff.RecordSuccess();
            return resp?.Data ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled us; that is not evidence about the service.
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"PreDB search failed for: {query}");
            return [];
        }
    }

    public async Task<PreDbRelease[]> GetLatestAsync(int count = 100, CancellationToken ct = default)
    {
        if (Backoff.ShouldSkip(DateTimeOffset.UtcNow)) return [];

        try
        {
            var url = $"?count={count}";
            var json = await _http.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<PreDbResponse>(json, JsonOptions);
            Backoff.RecordSuccess();
            return resp?.Data ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "PreDB latest fetch failed");
            return [];
        }
    }

    /// <summary>
    /// One line per failure, and the exception only on the first of a run. The dashboard polls
    /// every 15 seconds; during the 2026-08-19 outage that produced 135 stack traces in a day
    /// for a fact that had not changed since the first one.
    /// </summary>
    private void LogFailure(Exception ex, string what)
    {
        var withException = Backoff.ShouldLogWithException;
        var delay = Backoff.RecordFailure(DateTimeOffset.UtcNow);

        if (withException)
            Log.Warning(ex, "{What} — backing off {Delay} before the next attempt", what, delay);
        else
            Log.Warning("{What}: {Reason} (failure {Count}) — backing off {Delay}",
                what, ex.Message, Backoff.ConsecutiveFailures, delay);
    }

    public void Dispose() => _http.Dispose();
}

public class PreDbResponse
{
    public string Status { get; set; } = "";
    public int Results { get; set; }
    public PreDbRelease[] Data { get; set; } = [];
}

public class PreDbRelease
{
    public int Id { get; set; }
    public string Release { get; set; } = "";
    public string Group { get; set; } = "";
    public string Section { get; set; } = "";
    public string Genre { get; set; } = "";
    public double Size { get; set; }
    public int Files { get; set; }
    [JsonPropertyName("pretime")]
    public long PreAt { get; set; }
    public int Status { get; set; }
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public DateTime PreTime => DateTimeOffset.FromUnixTimeSeconds(PreAt).LocalDateTime;
    [JsonIgnore]
    public bool IsNuked => Status == 3;

    public string SizeFormatted
    {
        get
        {
            if (Size <= 0) return "";
            double mb = Size;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024;
            return $"{gb:F2} GB";
        }
    }

    /// <summary>
    /// Classify section into a broad category for filtering.
    /// </summary>
    public string BroadCategory => Section.ToUpperInvariant() switch
    {
        var s when s.StartsWith("TV") => "TV",
        var s when s.Contains("X264") || s.Contains("X265") || s.Contains("XVID") ||
                   s.Contains("DVDR") || s.Contains("BLURAY") || s == "MBLURAY" ||
                   s == "MDVDR" => "Movies",
        var s when s.StartsWith("MP3") || s.StartsWith("FLAC") || s.StartsWith("MUSIC") ||
                   s == "AUDIOBOOK" => "Music",
        var s when s.Contains("GAME") || s == "NSW" || s == "PS4" || s == "PS5" ||
                   s == "XBOX360" || s == "XBOXONE" => "Games",
        var s when s.StartsWith("XXX") => "XXX",
        var s when s == "0DAY" || s.StartsWith("APP") || s == "PDA" => "Apps",
        var s when s == "SPORTS" => "Sports",
        var s when s.StartsWith("EBOOK") || s.StartsWith("BOOK") => "Books",
        var s when s.StartsWith("ANIME") => "Anime",
        var s when s.StartsWith("DOX") || s.StartsWith("DOC") => "Docs",
        var s when s.StartsWith("MVID") => "MusicVideo",
        _ => "Other"
    };

    public static string FormatTimeAgo(long preAt)
    {
        var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(preAt);
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        return DateTimeOffset.FromUnixTimeSeconds(preAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }
}
