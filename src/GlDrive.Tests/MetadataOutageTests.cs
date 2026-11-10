using System.Net;
using System.Net.Http;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class MetadataOutageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Artist-Album-16BIT-WEB-FLAC-2026-GROUP")]
    [InlineData("Alien_Syndrome_AGA_AMIGA-TRSi")]
    [InlineData("Utility.2026.WIN64-GROUP")]
    public async Task NonVideoFilterDoesNotContactMovieProvider(string release)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("{\"titles\":[]}")));
        using var omdb = new OmdbClient(() => "", handler);
        using var tv = new TvMazeClient();
        using var filter = new MetadataFilterService(new AppConfig(), omdb, tv);
        var result = await filter.EvaluateAsync(new MetadataFilterConfig { Enabled = true, MinImdbRating = 7 },
            release, SceneNameParser.Parse(release));
        Assert.True(result.Allowed);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EnabledFilterWithoutThresholdsMakesNoRequest()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("{\"titles\":[]}")));
        using var omdb = new OmdbClient(() => "", handler);
        using var tv = new TvMazeClient();
        using var filter = new MetadataFilterService(new AppConfig(), omdb, tv);
        const string release = "Film.2026.1080p.BluRay-GROUP";
        Assert.True((await filter.EvaluateAsync(new MetadataFilterConfig { Enabled = true },
            release, SceneNameParser.Parse(release))).Allowed);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DnsOutageCooldownIsSharedBySearchAndDetailAndExpires()
    {
        var now = T0;
        using var handler = new Handler((_, _) => throw new HttpRequestException("Synthetic DNS failure"));
        using var client = new OmdbClient(() => "", handler, () => now);
        Assert.Empty(await client.Search("Film"));
        Assert.Null(await client.GetById("tt1234567"));
        Assert.Empty(await client.Search("Other Film"));
        Assert.Equal(1, handler.Calls);
        now += TimeSpan.FromSeconds(60);
        Assert.Null(await client.GetById("tt1234567"));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpOutageHasBoundedRetry(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new OmdbClient(() => "", handler, () => T0);
        await client.Search("Film");
        await client.GetById("tt1234567");
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HealthyResponseResetsOutageAndNotFoundDoesNotOpenCooldown()
    {
        var now = T0; var reply = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++reply == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : reply == 2 ? Json("{\"titles\":[{\"id\":\"tt1234567\",\"primaryTitle\":\"Film\"}]}")
            : reply == 3 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("{\"titles\":[]}")));
        using var client = new OmdbClient(() => "", handler, () => now);
        await client.Search("Film");
        now += TimeSpan.FromMinutes(1);
        Assert.Single(await client.Search("Film"));
        Assert.Null(await client.GetById("tt0000000"));
        Assert.Empty(await client.Search("No match"));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task PaidOutageStillUsesHealthyFallbackWhilePaidCallsAreSuppressed()
    {
        var paid = 0; var free = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "www.omdbapi.com")
            {
                paid++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            free++;
            return Task.FromResult(Json(request.RequestUri.AbsolutePath.StartsWith("/search/")
                ? "{\"titles\":[{\"id\":\"tt1234567\",\"primaryTitle\":\"Film\"}]}"
                : "{\"id\":\"tt1234567\",\"primaryTitle\":\"Film\"}"));
        });
        using var client = new OmdbClient(() => "synthetic", handler, () => T0);
        Assert.Single(await client.Search("Film"));
        Assert.NotNull(await client.GetById("tt1234567"));
        Assert.Equal(1, paid);
        Assert.Equal(2, free);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutFallbackOrCooldown()
    {
        using var cts = new CancellationTokenSource();
        var cancel = true;
        using var handler = new Handler((_, token) =>
        {
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(Json("{\"Search\":[{\"Title\":\"Film\",\"imdbID\":\"tt1234567\"}]}"));
        });
        using var client = new OmdbClient(() => "synthetic", handler, () => T0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Search("Film", cts.Token));
        Assert.Equal(1, handler.Calls);
        cancel = false;
        Assert.Single(await client.Search("Film"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DetailCancellationPropagatesAndCooldownStillHonorsCancelledCallers()
    {
        using var cancelled = new CancellationTokenSource();
        using var handler = new Handler((_, token) =>
        {
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not passed to provider");
        });
        using var client = new OmdbClient(() => "synthetic", handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetById("tt1234567", cancelled.Token));
        Assert.Equal(1, handler.Calls);

        using var outageHandler = new Handler((_, _) => throw new HttpRequestException("Synthetic outage"));
        using var outage = new OmdbClient(() => "", outageHandler, () => T0);
        await outage.Search("Film");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outage.Search("Film", cancelled.Token));
        Assert.Equal(1, outageHandler.Calls);
    }

    [Fact]
    public async Task ChangedPaidKeyCanRecoverBeforeCooldownExpires()
    {
        var key = "old";
        using var handler = new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("apikey=new")
                ? Json("{\"Title\":\"Film\",\"imdbID\":\"tt1234567\"}")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new OmdbClient(() => key, handler, () => T0);
        Assert.Null(await client.GetById("tt1234567"));
        Assert.Equal(2, handler.Calls);
        key = "new";
        Assert.NotNull(await client.GetById("tt1234567"));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ProviderTimeoutDoesNotMasqueradeAsCallerCancellation()
    {
        using var handler = new Handler((_, _) => throw new TaskCanceledException("Synthetic HTTP timeout"));
        using var client = new OmdbClient(() => "", handler, () => T0);
        Assert.Empty(await client.Search("Film"));
        Assert.Null(await client.GetById("tt1234567"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DatedVideoUsesTvMetadataAndEndedShowThreshold()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            "[{\"show\":{\"name\":\"Daily Show\",\"status\":\"Ended\"}}]")));
        using var omdb = new OmdbClient(() => "", handler);
        using var tv = new TvMazeClient(new HttpClient(handler, disposeHandler: false)
            { BaseAddress = new Uri("https://fixture.invalid/") }, _ => Task.CompletedTask);
        using var filter = new MetadataFilterService(new AppConfig(), omdb, tv);
        const string release = "Daily.Show.2026.11.10.1080p.HDTV-GROUP";
        var verdict = await filter.EvaluateAsync(new MetadataFilterConfig { Enabled = true, SkipEndedShows = true },
            release, SceneNameParser.Parse(release));
        Assert.False(verdict.Allowed);
        Assert.Contains("ended", verdict.Reason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentOutageRequestsProduceOneNetworkAttempt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = new OmdbClient(() => "", handler, () => T0);
        var first = client.Search("Film");
        await entered.Task;
        var other = client.GetById("tt1234567");
        finish.SetResult();
        await Task.WhenAll(first, other);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task VideoThresholdStillAppliesAndSuccessfulMetadataIsCached()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/search/")
                ? "{\"titles\":[{\"id\":\"tt1234567\",\"primaryTitle\":\"Film\",\"startYear\":2026}]}"
                : "{\"id\":\"tt1234567\",\"primaryTitle\":\"Film\",\"rating\":{\"aggregate\":4}}")));
        using var omdb = new OmdbClient(() => "", handler);
        using var tv = new TvMazeClient();
        using var filter = new MetadataFilterService(new AppConfig(), omdb, tv);
        const string release = "Film.2026.1080p.BluRay-GROUP";
        var config = new MetadataFilterConfig { Enabled = true, MinImdbRating = 7 };
        var parsed = SceneNameParser.Parse(release);
        Assert.False((await filter.EvaluateAsync(config, release, parsed)).Allowed);
        Assert.False((await filter.EvaluateAsync(config, release, parsed)).Allowed);
        Assert.Equal(2, handler.Calls);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return send(request, cancellationToken);
        }
    }
}
