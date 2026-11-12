using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class RequestFillerTests
{
    private const string Release = "Some.Show.S02E03.2160p.WEB.h265-GRP";
    private const string Requested = $"REQUESTED by user0 : [ 3:] {Release} : Please try to help user0 out.";
    private const string Filled = $"{Release} was REQFILLED thanks to user1!";

    private sealed class Fixture
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public List<string>? Listing = [$"REQUEST-{Release}"];
        public SearchResult? Source = new() { ReleaseName = Release, Category = "TV", RemotePath = $"/TV/{Release}", ServerId = "siteb" };
        public RaceStartOutcome Outcome = RaceStartOutcome.Queued;
        public readonly List<(string Section, string Release, string SourceId, string SourcePath, string Destination)> Fills = [];
        public readonly List<string> Cancelled = [];
        public readonly List<(string Channel, string Text)> Irc = [];
        public readonly List<string> SiteCommands = [];
        public bool Pending;
        public int Listings;
        public int Searches;

        public RequestFiller Create(RequestFillerConfig? config = null) => new("sitea",
            config ?? new RequestFillerConfig { Enabled = true }, new RequestFillerHost
            {
                FindSource = (_, _) => { Searches++; return Task.FromResult(Source); },
                ListDirectories = (_, _) => { Listings++; return Task.FromResult<IReadOnlyList<string>?>(Listing); },
                StartFill = (section, release, sourceId, sourcePath, destination) =>
                {
                    Fills.Add((section, release, sourceId, sourcePath, destination));
                    return new RaceStartResult(Outcome, section, release);
                },
                IsFillPending = _ => Pending,
                CancelFill = (_, release) => Cancelled.Add(release),
                SendIrc = (channel, text) => { Irc.Add((channel, text)); return Task.FromResult(true); },
                SendSiteCommand = (command, _) => { SiteCommands.Add(command); return Task.FromResult(true); },
                UtcNow = () => Now
            }, startTimer: false);
    }

    private static IrcMessageItem Bot(string text, string nick = "bot0") =>
        new() { Nick = nick, Text = text, Type = IrcMessageType.Normal };

    private static async Task Observe(RequestFiller filler, string text, string channel = "#chan1", string nick = "bot0")
    {
        filler.OnMessage(channel, Bot(text, nick));
        // OnMessage starts a pump; a second pump is a no-op while one runs, so
        // wait until the single-flight gate is free again.
        for (var i = 0; i < 200 && filler.Requests.Any(r => r.State == RequestState.Open && r.SearchAttempts == 0); i++)
            await Task.Delay(5);
        await filler.PumpAsync();
    }

    [Fact]
    public async Task Request_announce_fills_into_the_request_directory()
    {
        var f = new Fixture();
        using var filler = f.Create();

        await Observe(filler, Requested);

        var fill = Assert.Single(f.Fills);
        Assert.Equal(("TV", Release, "siteb", $"/TV/{Release}", $"/requests/REQUEST-{Release}/{Release}"), fill);
        Assert.Equal(RequestState.Filling, Assert.Single(filler.Requests).State);
    }

    [Fact]
    public async Task Completed_fill_says_reqfilled_in_the_request_channel()
    {
        var f = new Fixture();
        using var filler = f.Create();
        await Observe(filler, Requested);

        filler.OnFillEnded(Release, complete: true, "done");
        await filler.Announcing;

        Assert.Equal(("#chan1", $"!reqfilled {Release}"), Assert.Single(f.Irc));
        Assert.Equal(RequestState.Filled, Assert.Single(filler.Requests).State);
    }

    [Fact]
    public async Task Site_filled_command_goes_over_ftp_with_the_request_number()
    {
        var f = new Fixture();
        using var filler = f.Create(new RequestFillerConfig { Enabled = true, FilledCommand = "SITE REQFILLED {number}" });
        await Observe(filler, Requested);

        filler.OnFillEnded(Release, complete: true, "done");
        await filler.Announcing;

        Assert.Equal("SITE REQFILLED 3", Assert.Single(f.SiteCommands));
        Assert.Empty(f.Irc);
    }

    [Fact]
    public async Task Incomplete_fill_is_not_announced_and_is_retried()
    {
        var f = new Fixture();
        using var filler = f.Create();
        await Observe(filler, Requested);

        filler.OnFillEnded(Release, complete: false, "stopped");
        await filler.Announcing;

        Assert.Empty(f.Irc);
        var request = Assert.Single(filler.Requests);
        Assert.Equal(RequestState.Open, request.State);
        Assert.True(request.NextAttemptUtc > f.Now);
    }

    [Fact]
    public async Task Announce_without_a_request_directory_never_starts_a_fill()
    {
        var f = new Fixture { Listing = ["FILLED-Other.Release-GRP"] };
        using var filler = f.Create();

        await Observe(filler, Requested);

        Assert.Empty(f.Fills);
        Assert.Equal(0, f.Searches);
    }

    [Fact]
    public async Task Failed_listing_starts_nothing()
    {
        var f = new Fixture { Listing = null };
        using var filler = f.Create();

        await Observe(filler, Requested);

        Assert.Empty(f.Fills);
        Assert.Equal(RequestState.Open, Assert.Single(filler.Requests).State);
    }

    [Fact]
    public async Task Request_found_only_in_the_directory_is_filled()
    {
        var f = new Fixture();
        using var filler = f.Create();

        await filler.PumpAsync();

        Assert.Equal(Release, Assert.Single(f.Fills).Release);
    }

    [Fact]
    public async Task Missing_source_is_retried_when_the_release_appears()
    {
        var f = new Fixture { Source = null };
        using var filler = f.Create();
        await Observe(filler, Requested);
        Assert.Empty(f.Fills);
        Assert.True(Assert.Single(filler.Requests).NextAttemptUtc > f.Now);

        f.Source = new() { ReleaseName = Release, Category = "TV", RemotePath = $"/TV/{Release}", ServerId = "siteb" };
        f.Now = f.Now.AddMinutes(1);
        filler.OnReleaseSeen(Release);
        for (var i = 0; i < 200 && f.Fills.Count == 0; i++) await Task.Delay(5);

        Assert.Single(f.Fills);
    }

    [Fact]
    public async Task Filled_by_someone_else_withdraws_our_fill()
    {
        var f = new Fixture();
        using var filler = f.Create();
        await Observe(filler, Requested);

        filler.OnMessage("#chan1", Bot(Filled));
        filler.OnFillEnded(Release, complete: true, "late");
        await filler.Announcing;

        Assert.Equal([Release], f.Cancelled);
        Assert.Empty(f.Irc);
        Assert.Equal(RequestState.Closed, Assert.Single(filler.Requests).State);
    }

    [Fact]
    public async Task Interrupted_fill_the_engine_will_resume_stays_ours()
    {
        var f = new Fixture { Pending = true };
        using var filler = f.Create();
        await Observe(filler, Requested);

        filler.OnFillEnded(Release, complete: false, "waiting");
        Assert.Equal(RequestState.Filling, Assert.Single(filler.Requests).State);

        f.Pending = false;
        filler.OnFillEnded(Release, complete: true, "resumed");
        await filler.Announcing;

        Assert.Equal(("#chan1", $"!reqfilled {Release}"), Assert.Single(f.Irc));
    }

    [Fact]
    public async Task Fill_is_withdrawn_when_the_request_directory_disappears()
    {
        var f = new Fixture();
        using var filler = f.Create();
        await Observe(filler, Requested);

        f.Listing = [$"FILLED-{Release}"];
        f.Now = f.Now.AddMinutes(11);
        await filler.PumpAsync();

        Assert.Equal([Release], f.Cancelled);
        Assert.Equal(RequestState.Closed, Assert.Single(filler.Requests).State);
        Assert.Empty(f.Irc);
    }

    [Fact]
    public async Task Destination_uses_the_directory_exactly_as_the_site_spells_it()
    {
        var f = new Fixture { Listing = ["request-some.show.s02e03.2160p.web.h265-grp"] };
        using var filler = f.Create();

        await Observe(filler, Requested);

        Assert.Equal("/requests/request-some.show.s02e03.2160p.web.h265-grp/some.show.s02e03.2160p.web.h265-grp",
            Assert.Single(f.Fills).Destination);
    }

    [Fact]
    public async Task Request_recreated_after_being_filled_is_filled_again()
    {
        var f = new Fixture();
        using var filler = f.Create();
        await Observe(filler, Requested);
        filler.OnFillEnded(Release, complete: true, "done");
        await filler.Announcing;

        f.Now = f.Now.AddMinutes(11);
        await filler.PumpAsync();           // still listed until the site renames it
        Assert.Single(f.Fills);

        f.Listing = [];
        f.Now = f.Now.AddMinutes(11);
        await filler.PumpAsync();
        f.Listing = [$"REQUEST-{Release}"];
        f.Now = f.Now.AddHours(2);
        await filler.PumpAsync();
        await filler.PumpAsync();

        Assert.Equal(2, f.Fills.Count);
    }

    [Fact]
    public async Task Rejected_admission_leaves_the_request_open_for_a_retry()
    {
        var f = new Fixture { Outcome = RaceStartOutcome.AlreadyActive };
        using var filler = f.Create();

        await Observe(filler, Requested);

        Assert.Single(f.Fills);
        Assert.Equal(RequestState.Open, Assert.Single(filler.Requests).State);
    }

    [Fact]
    public async Task Bot_nick_and_channel_filters_apply()
    {
        var f = new Fixture { Listing = [] };
        using var filler = f.Create(new RequestFillerConfig { Enabled = true, BotNick = "bot0", Channel = "#chan1" });

        await Observe(filler, Requested, nick: "user1");
        await Observe(filler, Requested, channel: "#chan2");
        filler.OnMessage("user1", Bot(Requested));

        Assert.Empty(filler.Requests);
    }

    [Fact]
    public async Task Idle_filler_lists_the_requests_directory_only_at_the_reconcile_interval()
    {
        var f = new Fixture { Listing = [] };
        using var filler = f.Create();

        await filler.PumpAsync();
        await filler.PumpAsync();
        Assert.Equal(1, f.Listings);

        f.Now = f.Now.AddMinutes(11);
        await filler.PumpAsync();
        Assert.Equal(2, f.Listings);
    }

    [Theory]
    [InlineData("!request Some.Show.S02E03.2160p.WEB.h265-GRP", true)]
    [InlineData(Requested, true)]
    [InlineData(Filled, true)]
    [InlineData("NEW in requests >> REQUEST-Some.Show.S02E03.2160p.WEB.h265-GRP/Some.Show.S02E03.2160p.WEB.h265-GRP >> from user0", true)]
    [InlineData("NEW in tv >> Some.Show.S02E03.2160p.WEB.h265-GRP >> from user0", false)]
    [InlineData("NEW in mp3 >> VA-By_Special_Request-WEB-2026-GRP >> from user0", false)]
    public void Request_chatter_is_kept_out_of_the_announce_pipeline(string text, bool expected)
    {
        using var filler = new Fixture().Create();
        Assert.Equal(expected, filler.IsRequestTraffic(text));
    }
}

public class RequestFillQueueTests
{
    private static PersistedRace Race(string release, DateTime queued, bool urgent = false) =>
        new("TV", release, ["sitea", "siteb"], SpreadMode.Race, null, null, queued)
        {
            Urgent = urgent,
            DestinationPaths = urgent ? new() { ["sitea"] = $"/requests/REQUEST-{release}/{release}" } : null
        };

    [Fact]
    public void Request_fill_takes_the_next_slot_ahead_of_older_races()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var queue = new[]
        {
            (Race("Old.Release-GRP", now.AddHours(-5)), 0),
            (Race("Older.Release-GRP", now.AddHours(-9)), 1),
            (Race("Requested.Release-GRP", now, urgent: true), 2)
        };

        var order = RaceQueuePolicy.Order(queue, now, enabled: true, maxAgeMinutes: 120);

        Assert.Equal(2, order[0]);
    }

    [Theory]
    [InlineData("/requests/REQUEST-A.B-GRP/A.B-GRP", true)]
    [InlineData("requests/REQUEST-A.B-GRP/A.B-GRP", false)]
    [InlineData("/requests/../etc", false)]
    [InlineData("/requests//x", false)]
    [InlineData(@"/requests\x", false)]
    [InlineData("", false)]
    public void Request_destination_must_be_a_clean_absolute_path(string path, bool expected) =>
        Assert.Equal(expected, RaceLifetimePolicy.IsSafeAbsolutePath(path));
}
