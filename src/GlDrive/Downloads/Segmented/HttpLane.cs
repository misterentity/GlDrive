using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// One HTTP connection's worth of ranged GETs against a single URL. The HttpClient is shared per
/// network and not owned by the lane. Stopping a range early just drops the response, so HTTP
/// lanes can always stop early and never need to yield.
/// </summary>
public sealed class HttpLane(HttpClient http, Uri uri, NetworkPath network, string label) : ISegmentLane
{
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(300);

    private HttpResponseMessage? _open;

    public string Label => label;
    public string? NetworkId => network.Id;
    public bool CanStopEarly => true;
    public bool ShouldYield => false;

    public async Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct)
    {
        CloseOpen();

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var ranged = true;
        if (file.Size >= 0 && end > offset && end <= file.Size)
            request.Headers.Range = new RangeHeaderValue(offset, end - 1);
        else if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);
        else
            ranged = false;

        HttpResponseMessage response;
        using (request)
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        try
        {
            Check(response, file, offset, ranged);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            _open = response;
            return new Reader(this, response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void Check(HttpResponseMessage response, TransferFile file, long offset, bool ranged)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new LaneBackoffException(
                $"{label}: HTTP {(int)status} from {uri.Host} — backing off", BackoffDelay(response));

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{label}: HTTP {(int)status} {response.ReasonPhrase} from {uri.Host}",
                null, status);

        if (status == HttpStatusCode.PartialContent)
        {
            var from = response.Content.Headers.ContentRange?.From;
            if (from != offset)
                throw new IOException($"{label}: server returned range starting at {from?.ToString() ?? "?"}, asked for {offset}");
        }
        else if (ranged && offset > 0)
        {
            throw new IOException($"{label}: server ignored the Range header (HTTP {(int)status})");
        }

        if (file.Validator != null && HttpRangeProbe.ValidatorOf(response) is { } actual && actual != file.Validator)
            throw new IOException($"{label}: remote file changed ({file.Validator} -> {actual})");
    }

    internal static TimeSpan BackoffDelay(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        var retry = response.Headers.RetryAfter;
        TimeSpan? delay = retry?.Delta ?? (retry?.Date is { } date ? date - (now ?? DateTimeOffset.UtcNow) : null);
        if (delay == null) return DefaultBackoff;
        if (delay < MinBackoff) return MinBackoff;
        return delay > MaxBackoff ? MaxBackoff : delay.Value;
    }

    public void Fault(Exception ex) => CloseOpen();

    public ValueTask DisposeAsync()
    {
        CloseOpen();
        return ValueTask.CompletedTask;
    }

    private void CloseOpen() => Interlocked.Exchange(ref _open, null)?.Dispose();

    private void Closed(HttpResponseMessage response) => Interlocked.CompareExchange(ref _open, null, response);

    private sealed class Reader(HttpLane lane, HttpResponseMessage response, Stream stream) : ISegmentReader
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadAsync(buffer, ct);

        public async Task FinishAsync(bool reachedEof, CancellationToken ct)
        {
            try
            {
                if (reachedEof && await stream.ReadAsync(new byte[1], ct) > 0)
                    throw new IOException($"{lane.Label}: server sent more bytes than the range asked for");
            }
            finally
            {
                await DisposeAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            lane.Closed(response);
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
