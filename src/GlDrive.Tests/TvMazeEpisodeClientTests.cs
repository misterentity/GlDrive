using System.Net;
using System.Net.Http;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class TvMazeEpisodeClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    [Fact]
    public async Task EndpointsUseListsAndRequestGateWithoutNetwork()
    {
        var paths = new List<string>(); var gated = 0;
        using var handler = new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\":1,\"season\":1,\"number\":1,\"airstamp\":\"2026-01-01T20:00:00Z\"},{\"id\":2,\"season\":1,\"number\":2}]")
            });
        });
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://metadata.invalid/") },
            _ => { gated++; return Task.CompletedTask; });
        Assert.Equal(2, (await client.GetEpisodes(123))!.Length);
        Assert.Equal(2, (await client.GetEpisodesByDate(123, new(2026, 1, 1)))!.Length);
        Assert.Equal(["/shows/123/episodes", "/shows/123/episodesbydate?date=2026-01-01"], paths);
        Assert.Equal(2, gated);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailureIsDistinctFromEmptyInventory(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://metadata.invalid/") }, _ => Task.CompletedTask);
        Assert.Null(await client.GetEpisodes(123));
    }

    [Fact]
    public async Task CancellationDoesNotBecomeEmptySuccess()
    {
        using var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var client = new TvMazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://metadata.invalid/") }, _ => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetEpisodes(123, cancellation.Token));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(200, 400)]
    [InlineData(600, 0)]
    [InlineData(2000, 0)]
    [InlineData(-1000, 600)]
    public void RequestStartsAreSpacedAtLeastSixHundredMilliseconds(int elapsedMs, int delayMs)
    {
        var now = SeriesMonitorPolicyTests.Now;
        Assert.Equal(TimeSpan.FromMilliseconds(delayMs), TvMazeClient.RequestDelay(now.AddMilliseconds(-elapsedMs), now));
        Assert.Equal(TimeSpan.Zero, TvMazeClient.RequestDelay(null, now));
    }
}
