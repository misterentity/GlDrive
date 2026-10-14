using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace GlDrive.Downloads.Segmented;

/// <param name="Size">Total bytes, or -1 when the server did not say.</param>
/// <param name="Validator">ETag, else Last-Modified ("R" format), else null.</param>
public sealed record HttpProbeResult(Uri FinalUri, long Size, bool AcceptsRanges, string? Validator, string? FileName);

/// <summary>One-byte ranged GET that tells whether a URL can be fetched in parallel ranges.</summary>
public static class HttpRangeProbe
{
    public static async Task<HttpProbeResult> ProbeAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} probing {uri}", null, response.StatusCode);

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var partial = response.StatusCode == HttpStatusCode.PartialContent;
        var size = partial
            ? response.Content.Headers.ContentRange?.Length ?? -1
            : response.Content.Headers.ContentLength ?? -1;

        return new HttpProbeResult(finalUri, size, partial, ValidatorOf(response), FileNameOf(response, finalUri));
    }

    internal static string? ValidatorOf(HttpResponseMessage response)
    {
        if (response.Headers.ETag is { } etag) return etag.ToString();
        if (response.Content.Headers.LastModified is { } modified) return modified.ToString("R");
        return null;
    }

    private static string? FileNameOf(HttpResponseMessage response, Uri finalUri)
    {
        var cd = response.Content.Headers.ContentDisposition;
        var name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name) && finalUri.Segments.Length > 0)
            name = Uri.UnescapeDataString(finalUri.Segments[^1].TrimEnd('/'));
        // Never let a server-supplied name carry a directory part.
        name = string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
