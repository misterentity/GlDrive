using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class MovieCalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Dates = """
        {"results":[{"iso_3166_1":"US","release_dates":[
          {"type":1,"release_date":"2026-01-01T00:00:00Z"},
          {"type":2,"release_date":"2026-03-02T00:00:00Z"},
          {"type":3,"release_date":"2026-03-20T00:00:00Z"},
          {"type":4,"release_date":"2026-05-02T00:00:00Z"},
          {"type":5,"release_date":"2026-06-02T00:00:00Z"},
          {"type":6,"release_date":"2026-07-02T00:00:00Z"}
        ]},{"iso_3166_1":"GB","release_dates":[{"type":4,"release_date":"2026-04-01T00:00:00Z"}]}]}
        """;

    [Fact]
    public void RegionAndReleaseTypesAreAdvisoryAndDistinct()
    {
        var dates = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        Assert.Equal(new DateOnly(2026, 3, 2), dates.Theatrical);
        Assert.Equal(new DateOnly(2026, 5, 2), dates.Digital);
        Assert.Equal(new DateOnly(2026, 6, 2), dates.Physical);
        Assert.Equal("Theatrical / Digital / Physical", dates.ReleaseType);
        Assert.Equal(new DateOnly(2026, 4, 1), MovieReleaseDates.ParseTmdb(Dates, "GB", Now).Digital);
        Assert.Equal("unknown", MovieReleaseDates.ParseTmdb(Dates, "CA", Now).Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{bad")]
    public void MalformedResponseIsUnavailable(string json) => Assert.Equal("unavailable", MovieReleaseDates.ParseTmdb(json, "US", Now).Status);

    [Fact]
    public void BadOptionalEntriesDoNotLoseTheValidDates()
    {
        const string json = """
            {"results":[null,{}, {"iso_3166_1":"US","release_dates":[null,[],
              {"type":"4","release_date":"2026-05-02"}, {"type":5,"release_date":{}},
              {"type":4,"release_date":"impossible"}, {"type":4,"release_date":"2026-05-02Zbad"},
              {"type":4,"release_date":"2026-05-02T23:00:00-08:00"}, {"type":5,"release_date":"2026-06-02"}]}]}
            """;
        var dates = MovieReleaseDates.ParseTmdb(json, "US", Now);
        Assert.Equal(new DateOnly(2026, 5, 2), dates.Digital);
        Assert.Equal(new DateOnly(2026, 6, 2), dates.Physical);
        Assert.Null(dates.Theatrical);
    }

    [Theory]
    [InlineData("\"wrong\"")]
    [InlineData("{\"theatrical\":{},\"digital\":\"invalid\",\"physical\":\"2026-06-02\",\"region\":7}")]
    public void MalformedOptionalCalendarDoesNotVoidWishlist(string calendar)
    {
        var store = new WishlistStore(() => "[{\"id\":\"a\",\"title\":\"Example Movie\",\"releaseDates\":" + calendar + "}]", _ => { });
        var item = Assert.Single(store.Items);
        Assert.Equal("Example Movie", item.Title);
        if (item.ReleaseDates != null) Assert.Equal(new DateOnly(2026, 6, 2), item.ReleaseDates.Physical);
    }

    [Fact]
    public void CalendarRoundTripsWithoutComputedFields()
    {
        var dates = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        var json = JsonSerializer.Serialize(dates);
        Assert.DoesNotContain("releaseType", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(dates, JsonSerializer.Deserialize<MovieReleaseDates>(json));
    }

    [Fact]
    public async Task MissingKeyDoesNoNetworkWork()
    {
        var calls = 0;
        using var client = Client("", (_, _) => { calls++; throw new Exception("should not send"); });
        Assert.Equal("unavailable", (await client.GetReleaseDates(1)).Status);
        Assert.Null(await client.FindMovieByImdbId("tt1234567"));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task UnavailableProviderIsDistinguishedFromUnknownDates(HttpStatusCode status)
    {
        using var client = Client("synthetic", (_, _) => Task.FromResult(new HttpResponseMessage(status)));
        Assert.Equal("unavailable", (await client.GetReleaseDates(1)).Status);
    }

    [Fact]
    public async Task ImdbFindIgnoresMalformedOptionalResultsAndUsesMovieOnly()
    {
        using var client = Client("synthetic", (request, _) =>
        {
            Assert.Equal("/3/find/tt1234567", request.RequestUri!.AbsolutePath);
            Assert.Contains("external_source=imdb_id", request.RequestUri.Query);
            return Response("{\"movie_results\":[null,{}, {\"id\":\"bad\"},{\"id\":42}],\"tv_results\":[{\"id\":8}]}");
        });
        Assert.Equal(42, await client.FindMovieByImdbId("tt1234567"));
    }

    [Fact]
    public async Task AmbiguousMovieIdsAreNotGuessed()
    {
        using var client = Client("synthetic", (_, _) => Response("{\"movie_results\":[{\"id\":42},{\"id\":43}]}"));
        Assert.Null(await client.FindMovieByImdbId("tt1234567"));
    }

    [Fact]
    public async Task SingleMovieRefreshStoresResolvedIdAndDatesWithoutChangingWantedStatus()
    {
        var item = Movie();
        var store = Store(item);
        var calendar = new MovieCalendar(new(), store, () => Client("synthetic", (request, _) =>
            Response(request.RequestUri!.AbsolutePath.Contains("/find/") ? "{\"movie_results\":[{\"id\":42}]}" : Dates)));
        var result = await calendar.RefreshAsync(item.Id);
        Assert.Equal("known", result.Status);
        Assert.Equal(42, item.TmdbId);
        Assert.Equal(new DateOnly(2026, 5, 2), item.ReleaseDates?.Digital);
        Assert.Equal(WishlistStatus.Watching, item.Status);
    }

    [Fact]
    public async Task UserChangingMovieIdentityDuringLookupDiscardsOldResponse()
    {
        var item = Movie(); item.TmdbId = 42;
        var store = Store(item);
        var calendar = new MovieCalendar(new(), store, () => Client("synthetic", (_, _) =>
        {
            item.Title = "Another Movie"; store.Update(item);
            return Response(Dates);
        }));
        Assert.Equal("item-changed", (await calendar.RefreshAsync(item.Id)).Status);
        Assert.Null(item.ReleaseDates);
    }

    [Fact]
    public async Task CallerCancellationDoesNotErasePriorDates()
    {
        var item = Movie(); item.TmdbId = 42;
        item.ReleaseDates = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        var prior = item.ReleaseDates;
        var store = Store(item);
        using var stop = new CancellationTokenSource();
        var calendar = new MovieCalendar(new(), store, () => Client("synthetic", (_, _) =>
        {
            stop.Cancel(); return Response(Dates);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calendar.RefreshAsync(item.Id, stop.Token));
        Assert.Same(prior, item.ReleaseDates);
    }

    [Fact]
    public async Task BodyDeadlineReturnsUnavailableAfterHeadersArrive()
    {
        using var client = Client("synthetic", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new PendingStream()) }));
        client.CalendarRequestTimeout = TimeSpan.FromMilliseconds(30);
        var result = await client.GetReleaseDates(42).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("unavailable", result.Status);
    }

    [Theory]
    [InlineData("keyless")]
    [InlineData("missing-id")]
    [InlineData("http")]
    [InlineData("malformed")]
    public async Task Failed_refresh_retains_saved_dates_and_reports_lookup_failure(string failure)
    {
        var item = Movie(); item.TmdbId = failure == "missing-id" ? null : 42;
        if (failure == "missing-id") item.ImdbId = null;
        var prior = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        item.ReleaseDates = prior;
        var store = Store(item);
        var writes = 0;
        var calendar = new MovieCalendar(new(), store, () => Client(failure == "keyless" ? "" : "synthetic", (_, _) =>
        {
            writes++;
            return failure == "malformed" ? Response("{bad") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var result = await calendar.RefreshAsync(item.Id);
        Assert.Equal("unavailable", result.Status);
        Assert.Same(prior, item.ReleaseDates);
        Assert.Same(prior, result.Dates);
        Assert.Equal(failure is "keyless" or "missing-id" ? 0 : 1, writes);
    }

    [Fact]
    public async Task Successful_empty_regional_response_replaces_old_dates_as_unknown()
    {
        var item = Movie(); item.TmdbId = 42;
        item.ReleaseDates = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        var store = Store(item);
        var calendar = new MovieCalendar(new(), store, () => Client("synthetic", (_, _) => Response("{\"results\":[]}")));
        Assert.Equal("unknown", (await calendar.RefreshAsync(item.Id)).Status);
        Assert.Null(item.ReleaseDates?.Digital);
    }

    [Fact]
    public async Task CalendarWriteFailureRetainsMemory()
    {
        var item = Movie(); item.TmdbId = 42;
        var store = new WishlistStore(() => null, _ => throw new IOException("synthetic unavailable store"));
        store.Add(item);
        var calendar = new MovieCalendar(new(), store, () => Client("synthetic", (_, _) => Response(Dates)));
        Assert.Equal("known", (await calendar.RefreshAsync(item.Id)).Status);
        Assert.NotNull(item.ReleaseDates?.Digital);
    }

    [Fact]
    public void FutureCalendarDatesNeverBlockAValidRelease()
    {
        var item = Movie(); item.ReleaseDates = new(Digital: new DateOnly(2099, 1, 1));
        var store = Store(item); var count = 0;
        var matcher = new WishlistMatcher(store, _ => { count++; return true; }, new(), "site", "Site");
        matcher.OnNewRelease("movies", "Example.Movie.2026.1080p.WEB.H264-GRP", "/movies/release");
        Assert.Equal(1, count);
    }

    [Fact]
    public void PerItemSourceAllowanceWorksWithRecommendedProfileWithoutMutatingIt()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.Id = "custom";
        var config = new DownloadConfig { ScoredQualityEnabled = true, Profiles = [profile] };
        var item = Movie(); item.ProfileId = profile.Id; item.AllowLowQualitySources = true;
        var store = Store(item);
        var allowed = store.EvaluateScoredRelease(item.Id, "Example.Movie.2026.1080p.CAM.H264-GRP", config);
        Assert.True(allowed.Accepted); Assert.False(allowed.MeetsCutoff);
        Assert.False(profile.AllowLowQualitySources);
        Assert.False(store.EvaluateScoredRelease(item.Id, "Example.Movie.2026.720p.CAM.H264-GRP", config).Accepted);
    }

    [Fact]
    public void UpcomingDatesNotifyBindingsAndRemainUnknownUntilExplicitlyRefreshed()
    {
        var movie = new UpcomingMovieVm();
        Assert.Equal("Unknown", movie.ReleaseType);
        var changed = new List<string?>(); movie.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        movie.ReleaseDates = MovieReleaseDates.ParseTmdb(Dates, "US", Now);
        Assert.Contains(nameof(UpcomingMovieVm.DigitalDate), changed);
        Assert.Equal("2026-05-02", movie.DigitalDate);
    }

    private static WishlistItem Movie() => new() { Title = "Example Movie", Type = MediaType.Movie, Year = 2026, ImdbId = "tt1234567" };
    private static WishlistStore Store(WishlistItem item) { var store = new WishlistStore(() => null, _ => { }); store.Add(item); return store; }
    private static TmdbClient Client(string key, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(key, new Handler(send));
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
}
