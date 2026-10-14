using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

/// <summary>Serves a fixed byte array with real Range semantics, or a canned response.</summary>
internal sealed class RangeStubHandler(byte[] content) : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    public bool IgnoreRange { get; set; }
    public string? ETag { get; set; } = "\"v1\"";
    public long? ExtraBytes { get; set; }
    public List<RangeHeaderValue?> Ranges { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Ranges.Add(request.Headers.Range);
        var response = Override?.Invoke(request) ?? Serve(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        HttpResponseMessage response;
        var range = request.Headers.Range?.Ranges.Single();
        if (range == null || IgnoreRange)
        {
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }
        else
        {
            var from = range.From ?? 0;
            var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
            var body = content[(int)from..(int)(to + 1)];
            if (ExtraBytes is { } extra) body = [.. body, .. new byte[extra]];
            response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        }
        if (ETag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
        return response;
    }
}

public class HttpLaneTests
{
    private static readonly Uri Url = new("http://example.test/files/update.zip");
    private static readonly byte[] Content = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

    private static (HttpLane lane, RangeStubHandler handler) NewLane()
    {
        var handler = new RangeStubHandler(Content);
        var lane = new HttpLane(new HttpClient(handler), Url, NetworkPath.Default, "Ethernet");
        return (lane, handler);
    }

    private static TransferFile File(string? validator = "\"v1\"", long size = 1000) =>
        new(Url.ToString(), @"C:\dl\update.zip", size, validator);

    private static async Task<byte[]> Drain(ISegmentReader reader)
    {
        var ms = new MemoryStream();
        var buf = new byte[64];
        int n;
        while ((n = await reader.ReadAsync(buf, CancellationToken.None)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    [Fact]
    public async Task Ranged_open_reads_exactly_the_requested_bytes()
    {
        var (lane, handler) = NewLane();
        await using var reader = await lane.OpenAsync(File(), 100, 300, CancellationToken.None);

        var bytes = await Drain(reader);
        await reader.FinishAsync(reachedEof: true, CancellationToken.None);

        Assert.Equal(Content[100..300], bytes);
        var range = Assert.Single(Assert.Single(handler.Ranges)!.Ranges);
        Assert.Equal(100, range.From);
        Assert.Equal(299, range.To);
        Assert.Equal(NetworkPath.DefaultId, lane.NetworkId);
        Assert.True(lane.CanStopEarly);
        Assert.False(lane.ShouldYield);
    }

    [Fact]
    public async Task Unknown_size_at_offset_zero_sends_no_range()
    {
        var (lane, handler) = NewLane();
        await using var reader = await lane.OpenAsync(File(size: -1), 0, long.MaxValue, CancellationToken.None);
        Assert.Equal(Content, await Drain(reader));
        Assert.Null(Assert.Single(handler.Ranges));
    }

    [Fact]
    public async Task Server_ignoring_the_range_is_detected()
    {
        var (lane, handler) = NewLane();
        handler.IgnoreRange = true;
        await Assert.ThrowsAsync<IOException>(() => lane.OpenAsync(File(), 500, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task Full_body_200_is_fine_at_offset_zero()
    {
        var (lane, handler) = NewLane();
        handler.IgnoreRange = true;
        await using var reader = await lane.OpenAsync(File(), 0, 1000, CancellationToken.None);
        Assert.Equal(Content, await Drain(reader));
    }

    [Fact]
    public async Task Wrong_content_range_start_is_rejected()
    {
        var (lane, handler) = NewLane();
        handler.Override = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Content[..10]) };
            r.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 9, 1000);
            return r;
        };
        await Assert.ThrowsAsync<IOException>(() => lane.OpenAsync(File(), 500, 1000, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Throttling_statuses_back_off_using_retry_after(HttpStatusCode status)
    {
        var (lane, handler) = NewLane();
        handler.Override = _ =>
        {
            var r = new HttpResponseMessage(status);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        };
        var ex = await Assert.ThrowsAsync<LaneBackoffException>(() => lane.OpenAsync(File(), 0, 1000, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(42), ex.Delay);
    }

    [Fact]
    public void Backoff_defaults_to_60s_and_clamps_retry_after()
    {
        static HttpResponseMessage With(RetryConditionHeaderValue? retry)
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = retry;
            return r;
        }
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(60), HttpLane.BackoffDelay(With(null), now));
        Assert.Equal(TimeSpan.FromSeconds(5), HttpLane.BackoffDelay(With(new(TimeSpan.FromSeconds(1))), now));
        Assert.Equal(TimeSpan.FromSeconds(300), HttpLane.BackoffDelay(With(new(TimeSpan.FromHours(1))), now));
        Assert.Equal(TimeSpan.FromSeconds(90), HttpLane.BackoffDelay(With(new(now.AddSeconds(90))), now));
    }

    [Fact]
    public async Task Other_failures_throw_http_request_exception()
    {
        var (lane, handler) = NewLane();
        handler.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => lane.OpenAsync(File(), 0, 1000, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Finish_at_eof_rejects_trailing_bytes()
    {
        var (lane, handler) = NewLane();
        handler.ExtraBytes = 5;
        var reader = await lane.OpenAsync(File(), 0, 100, CancellationToken.None);
        var buf = new byte[100];
        var read = 0;
        while (read < 100) read += await reader.ReadAsync(buf.AsMemory(read), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => reader.FinishAsync(reachedEof: true, CancellationToken.None));
    }

    [Fact]
    public async Task Finish_early_just_drops_the_response()
    {
        var (lane, _) = NewLane();
        var reader = await lane.OpenAsync(File(), 0, 1000, CancellationToken.None);
        await reader.ReadAsync(new byte[10], CancellationToken.None);
        await reader.FinishAsync(reachedEof: false, CancellationToken.None);

        // The lane is reusable afterwards.
        await using var again = await lane.OpenAsync(File(), 10, 20, CancellationToken.None);
        Assert.Equal(Content[10..20], await Drain(again));
    }

    [Fact]
    public async Task Changed_validator_is_rejected()
    {
        var (lane, handler) = NewLane();
        handler.ETag = "\"v2\"";
        var ex = await Assert.ThrowsAsync<IOException>(() => lane.OpenAsync(File("\"v1\""), 0, 1000, CancellationToken.None));
        Assert.Contains("remote file changed", ex.Message);
    }

    [Fact]
    public async Task Null_validator_skips_the_check()
    {
        var (lane, handler) = NewLane();
        handler.ETag = "\"v2\"";
        await using var reader = await lane.OpenAsync(File(validator: null), 0, 10, CancellationToken.None);
        Assert.Equal(Content[..10], await Drain(reader));
    }

    [Fact]
    public async Task Bound_client_connects_from_the_bound_address()
    {
        // Any 127.x address is loopback on Windows, so binding 127.0.0.2 and seeing it as the
        // peer address proves the ConnectCallback actually bound the socket.
        var bind = IPAddress.Parse("127.0.0.2");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            var stream = client.GetStream();
            var buf = new byte[4096];
            var request = "";
            while (!request.Contains("\r\n\r\n"))
            {
                var n = await stream.ReadAsync(buf);
                if (n == 0) break;
                request += Encoding.ASCII.GetString(buf, 0, n);
            }
            var body = "hello";
            var reply = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
            return (peer, request);
        });

        try
        {
            var path = new NetworkPath("loop", "Loopback", bind, null);
            using var http = NetworkHttpClients.Create(path, TimeSpan.FromSeconds(10));
            var text = await http.GetStringAsync($"http://127.0.0.1:{port}/x");

            var (peer, request) = await server;
            Assert.Equal("hello", text);
            Assert.Equal(bind, peer);
            Assert.Contains("User-Agent: GlDrive", request);
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class HttpRangeProbeTests
{
    private static readonly byte[] Content = new byte[5000];

    [Fact]
    public async Task Partial_response_gives_size_ranges_and_etag()
    {
        var handler = new RangeStubHandler(Content);
        var result = await HttpRangeProbe.ProbeAsync(new HttpClient(handler),
            new Uri("http://example.test/dl/GlDrive%20Setup.zip?x=1"), CancellationToken.None);

        Assert.Equal(5000, result.Size);
        Assert.True(result.AcceptsRanges);
        Assert.Equal("\"v1\"", result.Validator);
        Assert.Equal("GlDrive Setup.zip", result.FileName);
        var range = Assert.Single(Assert.Single(handler.Ranges)!.Ranges);
        Assert.Equal(0, range.From);
        Assert.Equal(0, range.To);
    }

    [Fact]
    public async Task Full_response_gives_content_length_and_last_modified()
    {
        var modified = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        var handler = new RangeStubHandler(Content) { IgnoreRange = true, ETag = null };
        handler.Override = req =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) };
            r.Content.Headers.LastModified = modified;
            r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"..\\evil\\real.zip\"" };
            r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.test/final/real.zip");
            return r;
        };

        var result = await HttpRangeProbe.ProbeAsync(new HttpClient(handler), new Uri("http://example.test/dl"), CancellationToken.None);

        Assert.Equal(5000, result.Size);
        Assert.False(result.AcceptsRanges);
        Assert.Equal(modified.ToString("R"), result.Validator);
        Assert.Equal("real.zip", result.FileName);
        Assert.Equal(new Uri("https://cdn.example.test/final/real.zip"), result.FinalUri);
    }

    [Fact]
    public async Task Failure_status_throws()
    {
        var handler = new RangeStubHandler(Content) { Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HttpRangeProbe.ProbeAsync(new HttpClient(handler), new Uri("http://example.test/x"), CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}
