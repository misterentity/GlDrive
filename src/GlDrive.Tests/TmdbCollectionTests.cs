using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class TmdbCollectionTests
{
    private const string Movie = """{"id":42,"belongs_to_collection":{"id":9,"name":"Set"}}""";
    private const string Collection = """{"id":9,"name":"Set","parts":[{"id":42,"title":"Example Movie","release_date":"2024-01-02"},{"id":43,"name":"Another Movie","media_type":"movie","release_date":"2025-02-03"}]}""";

    [Fact]
    public async Task Movie_collection_uses_exactly_two_bounded_gets_and_supports_both_provider_title_shapes()
    {
        var paths = new List<string>();
        using var client = Client((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            paths.Add(request.RequestUri!.AbsolutePath);
            return Response(paths.Count == 1 ? Movie : Collection);
        });
        var result = await client.GetMovieCollection(42);
        Assert.Equal(new[] { "/3/movie/42", "/3/collection/9" }, paths);
        Assert.Equal("available", result.Status);
        Assert.Equal(new[] { "Example Movie", "Another Movie" }, result.Collection!.Films.Select(f => f.Title));
        Assert.Equal(new int?[] { 2024, 2025 }, result.Collection.Films.Select(f => f.Year));
    }

    [Theory]
    [InlineData("{}", "unknown")]
    [InlineData("{\"id\":42}", "unknown")]
    [InlineData("{\"id\":42,\"belongs_to_collection\":null}", "no-collection")]
    [InlineData("{\"id\":42,\"belongs_to_collection\":false}", "unknown")]
    [InlineData("{\"id\":42,\"belongs_to_collection\":{\"id\":\"9\"}}", "unknown")]
    [InlineData("{\"id\":8,\"belongs_to_collection\":{\"id\":9}}", "unknown")]
    [InlineData("[]", "unknown")]
    [InlineData("{bad", "unknown")]
    public async Task Only_explicit_null_proves_no_collection_and_bad_evidence_never_follows_up(string json, string expected)
    {
        var calls = 0;
        using var client = Client((_, _) => { calls++; return Response(json); });
        var result = await client.GetMovieCollection(42);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Collection);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"wrong\"")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"wrong\"}")]
    [InlineData("null")]
    public void Bad_optional_collection_field_does_not_void_movie_details(string optional)
    {
        var detail = JsonSerializer.Deserialize<TmdbMovieDetail>("{\"Id\":42,\"Title\":\"Example\",\"belongs_to_collection\":" + optional + "}")!;
        Assert.Equal("Example", detail.Title);
        Assert.Null(detail.CollectionId);
    }

    [Fact]
    public async Task Optional_malformed_dates_and_metadata_keep_valid_members_and_invalid_members_are_partial()
    {
        const string json = """{"id":9,"name":false,"parts":[null,{"id":1,"title":"First","release_date":{}},{"id":2,"title":7,"original_name":"Second","release_date":"invalid","overview":[]},{"id":3,"name":"Third","release_date":"2025-02-30"},{"id":4,"title":false}]}""";
        using var client = Client((_, _) => Response(json));
        var result = await client.GetCollection(9);
        Assert.Equal("partial", result.Status);
        Assert.Equal(new[] { "First", "Second", "Third" }, result.Collection!.Films.Select(f => f.Title));
        Assert.All(result.Collection.Films, f => Assert.Null(f.Year));
        Assert.Equal("Collection 9", result.Collection.Name);
    }

    [Fact]
    public async Task Large_collection_is_bounded_and_reports_partial_instead_of_claiming_complete()
    {
        var json = JsonSerializer.Serialize(new { id = 9, name = "Large", parts = Enumerable.Range(1, 205).Select(i => new { id = i, title = "Movie " + i }) });
        var calls = 0;
        using var client = Client((_, _) => { calls++; return Response(json); });
        var result = await client.GetCollection(9);
        Assert.Equal("partial", result.Status);
        Assert.Equal(200, result.Collection!.Films.Count);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"id\":8,\"parts\":[]}")]
    [InlineData("{\"id\":9,\"parts\":{}}")]
    [InlineData("{\"id\":9}")]
    public async Task Missing_collection_identity_or_member_array_is_unknown(string json)
    {
        using var client = Client((_, _) => Response(json));
        Assert.Equal("unknown", (await client.GetCollection(9)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Provider_failure_is_unknown(HttpStatusCode status)
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        Assert.Equal("unknown", (await client.GetMovieCollection(42)).Status);
        Assert.Equal("unknown", (await client.GetCollection(9)).Status);
    }

    [Fact]
    public async Task Keyless_or_invalid_identity_does_no_request()
    {
        var calls = 0;
        using var keyless = new TmdbClient("", new Handler((_, _) => { calls++; return Response(Movie); }));
        using var keyed = Client((_, _) => { calls++; return Response(Movie); });
        Assert.Equal("unknown", (await keyless.GetMovieCollection(42)).Status);
        Assert.Equal("unknown", (await keyless.GetCollection(9)).Status);
        Assert.Equal("unknown", (await keyed.GetMovieCollection(0)).Status);
        Assert.Equal("unknown", (await keyed.GetCollection(-1)).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Oversized_stream_without_length_header_is_still_bounded()
    {
        var body = new NonSeekableStream(new byte[4 * 1024 * 1024]);
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        Assert.Equal("unknown", (await client.GetCollection(9)).Status);
        Assert.True(body.ReadBytes > 2 * 1024 * 1024);
        Assert.True(body.ReadBytes <= 2 * 1024 * 1024 + 8192);
    }

    [Fact]
    public async Task Body_deadline_and_whole_lookup_deadline_degrade_to_unknown()
    {
        using var bodyClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }));
        bodyClient.CollectionRequestTimeout = TimeSpan.FromMilliseconds(30);
        Assert.Equal("unknown", (await bodyClient.GetCollection(9).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        var calls = 0;
        using var wholeClient = Client(async (_, ct) =>
        {
            calls++;
            if (calls == 1) return await Response(Movie);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        wholeClient.CollectionLookupTimeout = TimeSpan.FromMilliseconds(50);
        wholeClient.CollectionRequestTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal("unknown", (await wholeClient.GetMovieCollection(42).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_provider_evidence()
    {
        using var stop = new CancellationTokenSource();
        using var client = Client(async (_, ct) => { stop.Cancel(); await Task.Delay(Timeout.Infinite, ct); return await Response(Movie); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMovieCollection(42, stop.Token));
    }

    private static TmdbClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new("synthetic", new Handler(send));
    private static Task<HttpResponseMessage> Response(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class PendingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadBytes { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (read.IsCompletedSuccessfully) ReadBytes += read.Result;
            return read;
        }
    }
}
