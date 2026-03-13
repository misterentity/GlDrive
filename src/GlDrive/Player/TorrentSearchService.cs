using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Serilog;

namespace GlDrive.Player;

public record TorrentSearchResult(
    string Title,
    string DetailUrl,
    int Seeds,
    int Leeches,
    string Size,
    string Uploader);

public class TorrentSearchService : IDisposable
{
    private static readonly Regex SeedLeechRegex = new(
        @"<td\s+class=""coll-2 seeds"">(\d+)</td>\s*<td\s+class=""coll-3 leeches"">(\d+)</td>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MagnetRegex = new(
        @"href=""(magnet:\?[^""]+)""",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private string _baseUrl = "";
    private bool _initialized;

    // Try multiple domains — some may be blocked in certain regions
    private static readonly string[] Domains =
    [
        "https://www.1377x.to",
        "https://1377x.to",
        "https://www.1337x.to",
        "https://1337x.to"
    ];

    public TorrentSearchService()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        _http.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        _http.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        _http.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        _http.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _http.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        _http.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        _http.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        _http.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Finds a working domain by hitting the homepage. Stores cookies for future requests.
    /// </summary>
    private async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized && !string.IsNullOrEmpty(_baseUrl)) return;

        foreach (var domain in Domains)
        {
            try
            {
                Log.Debug("Trying torrent site domain: {Domain}", domain);
                var response = await _http.GetAsync($"{domain}/", ct);

                if (response.IsSuccessStatusCode)
                {
                    _baseUrl = domain;
                    _initialized = true;
                    Log.Information("Torrent search using domain: {Domain}", domain);
                    return;
                }

                Log.Debug("Domain {Domain} returned {Status}", domain, response.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Domain {Domain} failed", domain);
            }
        }

        Log.Warning("No torrent site domain accessible");
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<TorrentSearchResult>();

        try
        {
            await InitializeAsync(ct);
            if (string.IsNullOrEmpty(_baseUrl))
            {
                Log.Warning("Torrent search skipped — no accessible domain");
                return results;
            }

            var url = $"{_baseUrl}/search/{Uri.EscapeDataString(query)}/1/";
            Log.Debug("Torrent search: {Url}", url);

            // Set referer to look like we navigated from the homepage
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"{_baseUrl}/");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Torrent search returned {Status} from {Url}", response.StatusCode, url);

                // If we get blocked, reset and try another domain next time
                if (response.StatusCode == HttpStatusCode.Forbidden ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    _initialized = false;
                    _baseUrl = "";
                }
                return results;
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Debug: log the first 500 chars to see what we got
            Log.Debug("Torrent search response length: {Len}, starts with: {Start}",
                html.Length, html[..Math.Min(200, html.Length)]);

            // Parse the search results table
            var tableMatch = Regex.Match(html, @"<table[^>]*class=""table-list[^""]*""[^>]*>(.*?)</table>",
                RegexOptions.Singleline);
            if (!tableMatch.Success)
            {
                // Try tbody directly — some pages structure differently
                tableMatch = Regex.Match(html, @"<tbody>(.*?)</tbody>", RegexOptions.Singleline);
            }

            if (!tableMatch.Success)
            {
                Log.Debug("No results table found in torrent search response ({Len} bytes)", html.Length);
                return results;
            }

            var tableHtml = tableMatch.Groups[1].Value;

            // Parse each row
            var rowMatches = Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline);
            foreach (Match row in rowMatches)
            {
                var rowHtml = row.Groups[1].Value;

                // Extract title and detail URL — the second <a> in the name column has the title
                // Structure: <td class="coll-1 name"><a href="/sub/..." class="icon">...</a><a href="/torrent/...">Title</a></td>
                var linkMatch = Regex.Match(rowHtml,
                    @"<a\s+href=""(/torrent/[^""]+)""[^>]*>\s*(.*?)\s*</a>",
                    RegexOptions.Singleline);
                if (!linkMatch.Success) continue;

                var detailUrl = linkMatch.Groups[1].Value;
                // Strip any inner HTML tags from the title
                var rawTitle = linkMatch.Groups[2].Value;
                var title = Regex.Replace(rawTitle, @"<[^>]+>", "").Trim();
                title = WebUtility.HtmlDecode(title);

                if (string.IsNullOrEmpty(title)) continue;

                // Seeds and leeches
                var slMatch = SeedLeechRegex.Match(rowHtml);
                var seeds = slMatch.Success && int.TryParse(slMatch.Groups[1].Value, out var s) ? s : 0;
                var leeches = slMatch.Success && int.TryParse(slMatch.Groups[2].Value, out var l) ? l : 0;

                // Size — text content of the size column, before any <span>
                var sizeMatch = Regex.Match(rowHtml,
                    @"<td\s+class=""coll-4[^""]*""[^>]*>\s*([^<]+)",
                    RegexOptions.Singleline);
                var size = sizeMatch.Success
                    ? WebUtility.HtmlDecode(sizeMatch.Groups[1].Value).Trim()
                    : "";

                results.Add(new TorrentSearchResult(title, detailUrl, seeds, leeches, size, ""));
            }

            Log.Information("Torrent search for \"{Query}\" returned {Count} results", query, results.Count);
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Torrent search failed for \"{Query}\"", query);
        }

        return results;
    }

    public async Task<string?> GetMagnetLinkAsync(string detailPath, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_baseUrl))
                await InitializeAsync(ct);
            if (string.IsNullOrEmpty(_baseUrl))
                return null;

            var url = $"{_baseUrl}{detailPath}";
            Log.Debug("Fetching magnet link from {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"{_baseUrl}/");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Magnet page returned {Status}", response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var match = MagnetRegex.Match(html);
            if (match.Success)
            {
                var magnet = WebUtility.HtmlDecode(match.Groups[1].Value);
                Log.Debug("Found magnet link ({Len} chars)", magnet.Length);
                return magnet;
            }

            Log.Warning("No magnet link found on {Url}", url);
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get magnet link from {Path}", detailPath);
        }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
