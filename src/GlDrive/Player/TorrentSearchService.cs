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
    private static readonly Regex RowRegex = new(
        @"<td\s+class=""coll-1 name"">.*?<a\s+href=""(/torrent/[^""]+)""[^>]*>([^<]+)</a>.*?</td>" +
        @"\s*<td\s+class=""coll-date"">([^<]*)</td>" +
        @"\s*<td\s+class=""coll-4 size[^""]*"">([^<]*)<",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SeedLeechRegex = new(
        @"<td\s+class=""coll-2 seeds"">(\d+)</td>\s*<td\s+class=""coll-3 leeches"">(\d+)</td>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MagnetRegex = new(
        @"href=""(magnet:\?[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex UploaderRegex = new(
        @"<td\s+class=""coll-5 uploader"">.*?(?:>([^<]+)<)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly HttpClient _http;
    private const string BaseUrl = "https://www.1377x.to";

    public TorrentSearchService()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<TorrentSearchResult>();

        try
        {
            var url = $"{BaseUrl}/search/{Uri.EscapeDataString(query)}/1/";
            Log.Debug("Torrent search: {Url}", url);

            var html = await _http.GetStringAsync(url, ct);

            // Parse the search results table
            // 1377x uses a table with class "table-list" containing rows
            var tableMatch = Regex.Match(html, @"<table\s+class=""table-list[^""]*"">(.*?)</table>",
                RegexOptions.Singleline);
            if (!tableMatch.Success)
            {
                Log.Debug("No results table found in 1377x response");
                return results;
            }

            var tableHtml = tableMatch.Groups[1].Value;

            // Parse each row
            var rowMatches = Regex.Matches(tableHtml, @"<tr>(.*?)</tr>", RegexOptions.Singleline);
            foreach (Match row in rowMatches)
            {
                var rowHtml = row.Groups[1].Value;

                // Extract title and detail URL
                var linkMatch = Regex.Match(rowHtml,
                    @"<a\s+href=""(/torrent/[^""]+)""[^>]*>\s*([^<]+)\s*</a>",
                    RegexOptions.Singleline);
                if (!linkMatch.Success) continue;

                var detailUrl = linkMatch.Groups[1].Value;
                var title = System.Net.WebUtility.HtmlDecode(linkMatch.Groups[2].Value).Trim();

                // Seeds and leeches
                var slMatch = SeedLeechRegex.Match(rowHtml);
                var seeds = slMatch.Success ? int.Parse(slMatch.Groups[1].Value) : 0;
                var leeches = slMatch.Success ? int.Parse(slMatch.Groups[2].Value) : 0;

                // Size — in the size column, text before <span>
                var sizeMatch = Regex.Match(rowHtml,
                    @"<td\s+class=""coll-4 size[^""]*"">\s*([^<]+)",
                    RegexOptions.Singleline);
                var size = sizeMatch.Success
                    ? System.Net.WebUtility.HtmlDecode(sizeMatch.Groups[1].Value).Trim()
                    : "";

                // Uploader
                var uploaderMatch = UploaderRegex.Match(rowHtml);
                var uploader = uploaderMatch.Success
                    ? System.Net.WebUtility.HtmlDecode(uploaderMatch.Groups[1].Value).Trim()
                    : "";

                results.Add(new TorrentSearchResult(title, detailUrl, seeds, leeches, size, uploader));
            }

            Log.Information("Torrent search for \"{Query}\" returned {Count} results", query, results.Count);
        }
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
            var url = $"{BaseUrl}{detailPath}";
            Log.Debug("Fetching magnet link from {Url}", url);

            var html = await _http.GetStringAsync(url, ct);
            var match = MagnetRegex.Match(html);
            if (match.Success)
            {
                var magnet = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                Log.Debug("Found magnet link: {Magnet}", magnet[..Math.Min(80, magnet.Length)]);
                return magnet;
            }

            Log.Warning("No magnet link found on {Url}", url);
        }
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
