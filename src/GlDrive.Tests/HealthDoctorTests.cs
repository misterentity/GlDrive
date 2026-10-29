using GlDrive.Config;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class HealthDoctorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-02T12:00:00Z");
    internal static AppConfig Config() => new()
    {
        Servers = [new() { Id = "server-a", Name = "Example", Mount = new() { MountDrive = false } }]
    };
    internal static DoctorContext Context(AppConfig config) => new(config, [], [], [], []);
    private static DoctorServer Runtime(SiteHealthSnapshot? health = null) =>
        new("server-a", health, false, false, "", null, false, [], []);
    private static IReadOnlyList<HealthProblem> Check(DoctorContext context) => HealthDoctor.Evaluate(context, Now).Problems;

    [Theory]
    [InlineData("Verbose", true)]
    [InlineData("Debug", true)]
    [InlineData("Information", false)]
    [InlineData("Warning", false)]
    public void LoggingCheckPreservesExplicitSetting(string level, bool expected)
    {
        var config = Config();
        config.Logging.Level = level;
        var rows = Check(Context(config));
        Assert.Equal(expected, rows.Any(p => p.Title == "Verbose left on"));
        Assert.Equal(level, config.Logging.Level);
        if (expected)
        {
            var fix = Assert.Single(rows).Fix;
            Assert.NotNull(fix);
            Assert.Equal("/logging/level", fix.Path);
            Assert.Equal("Information", fix.After!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(SiteFailureKind.Unreachable, true)]
    [InlineData(SiteFailureKind.Timeout, true)]
    [InlineData(SiteFailureKind.BncCooldown, false)]
    [InlineData(SiteFailureKind.LoginLimit, false)]
    [InlineData(SiteFailureKind.LocalCapacity, false)]
    [InlineData(SiteFailureKind.Unknown, false)]
    public void DeadSiteNeedsPositiveOutageEvidence(SiteFailureKind kind, bool expected)
    {
        var state = new SiteHealthSnapshot("server-a", SiteHealthState.Down, kind, Now.AddDays(-2), 4, 2,
            Now.AddHours(1), null, null, true, "Observed login failure");
        var context = Context(Config()) with { Servers = [Runtime(state)] };
        Assert.Equal(expected, Check(context).Any(p => p.Id.StartsWith("site-outage:")));
        Assert.DoesNotContain(Check(context with { Servers = [Runtime(state) with { Connected = true }] }), p => p.Id.StartsWith("site-outage:"));
        Assert.DoesNotContain(Check(context with { Servers = [Runtime(state with { FirstFailureUtc = null })] }), p => p.Id.StartsWith("site-outage:"));
        Assert.All(Check(context), p => Assert.False(p.FixAvailable));
        Assert.True(context.Config.Servers[0].Mount.AutoMountOnStart);
    }

    [Theory]
    [InlineData(SiteFailureKind.Timeout, 0, 0, true, false)]
    [InlineData(SiteFailureKind.Unreachable, 29, 0, true, false)]
    [InlineData(SiteFailureKind.Timeout, 29, 1, true, false)]
    [InlineData(SiteFailureKind.Timeout, 120, 0, true, false)]
    [InlineData(SiteFailureKind.BncCooldown, 0, 0, true, false)]
    [InlineData(SiteFailureKind.BncCooldown, 120, 2, true, false)]
    [InlineData(SiteFailureKind.Timeout, 120, 2, false, false)]
    [InlineData(SiteFailureKind.Timeout, 30, 1, true, true)]
    [InlineData(SiteFailureKind.Unreachable, 120, 2, true, true)]
    public void OnlySustainedEscalatedOutagesAreReportedAndNeverDisableMounting(
        SiteFailureKind kind, int minutes, int escalation, bool enabled, bool expected)
    {
        var config = Config();
        config.Health.Enabled = enabled;
        var state = new SiteHealthSnapshot("server-a", SiteHealthPolicy.StateFor(kind, TimeSpan.FromMinutes(minutes)),
            kind, Now.AddMinutes(-minutes), escalation + 1, escalation, Now.AddMinutes(15), null, null, true, "Login failure");
        var rows = Check(Context(config) with { Servers = [Runtime(state)] });
        Assert.Equal(expected, rows.Any(p => p.Id.StartsWith("site-outage:")));
        Assert.All(rows, row => { Assert.False(row.FixAvailable); Assert.Null(row.Fix); });
        Assert.True(config.Servers[0].Mount.AutoMountOnStart);
    }

    [Fact]
    public void MissingDriveAndUnknownReadAreDistinct()
    {
        var result = HealthDoctor.Evaluate(Context(Config()) with
        {
            Drives = [new("X:\\", "Missing"), new("Y:\\", "Unavailable"), new("Z:\\", "Ready")]
        }, Now);
        Assert.True(result.Partial);
        Assert.Equal(2, result.Problems.Count);
        Assert.Single(result.Problems, p => p.Title == "Download drive is missing");
        Assert.Single(result.Problems, p => p.Title == "Download drive status unknown");
        Assert.All(result.Problems, p => Assert.False(p.FixAvailable));
    }

    [Theory]
    [InlineData("x:\\media", "X:\\")]
    [InlineData("X:/media", "X:\\")]
    [InlineData("\\\\example.invalid\\media", null)]
    [InlineData("X:relative", null)]
    [InlineData("relative", null)]
    [InlineData("\\\\?\\X:\\media", null)]
    public void LocalRootExtractionCannotTurnRemotePathIntoProbe(string path, string? expected) =>
        Assert.Equal(expected, ProblemsService.LocalDriveRoot(path));

    [Theory]
    [InlineData("/", "/incoming/films", 2, true)]
    [InlineData("/", "/incoming/films", 1, false)]
    [InlineData("/incoming", "/incoming/films", 1, true)]
    [InlineData("/incoming/", "/incoming/films/", 1, true)]
    [InlineData("/film", "/films", 2, false)]
    [InlineData("/films", "/films", 0, true)]
    [InlineData("/films", "/Films", 1, false)]
    [InlineData("", "/films", 2, false)]
    public void IndexCoverageUsesPathBoundariesAndActualCrawlerDepth(string root, string destination, int depth, bool covered) =>
        Assert.Equal(covered, HealthDoctor.Covers(root, destination, depth));

    [Fact]
    public void DestinationFixOnlyProposesKnownUncoveredPath()
    {
        var config = Config();
        config.Servers[0].Search.SearchPaths = ["/incoming"];
        config.Servers[0].SpreadSite.SectionMappings = [new() { Path = "/archive/films", RemoteSection = "FILMS" }];
        var problem = Assert.Single(Check(Context(config)));
        Assert.Equal("/servers/0/search/searchPaths", problem.Fix!.Path);
        Assert.Contains("immediately rebuilds the whole index", problem.Fix.Impact);
        Assert.Contains("all configured search roots", problem.Fix.Impact);
        Assert.Contains("main pool shared with downloads", problem.Fix.Impact);
        Assert.Single(config.Servers[0].Search.SearchPaths);
        config.Servers[0].SpreadSite.DownloadOnly = true;
        Assert.Empty(Check(Context(config)));
    }

    [Fact]
    public void EmptyMonitorRequiresObservationForCurrentPath()
    {
        var config = Config();
        var state = Runtime() with { MonitorEmpty = true, MonitorCheckedUtc = Now.UtcDateTime, MonitorPath = "/recent" };
        Assert.Single(Check(Context(config) with { Servers = [state] }));
        Assert.Empty(Check(Context(config) with { Servers = [state with { MonitorPath = "/old" }] }));
        Assert.Empty(Check(Context(config) with { Servers = [state with { MonitorCheckedUtc = null }] }));
        config.Servers[0].Notifications.Enabled = false;
        Assert.Empty(Check(Context(config) with { Servers = [state] }));
    }

    [Fact]
    public void ChannelFindingIsExactAndDoesNotInferMissingJoinWhileOffline()
    {
        var config = Config();
        config.Servers[0].Irc.Enabled = true;
        config.Servers[0].Irc.AnnounceRules = [new() { Channel = "#feed-a", Pattern = "x" }, new() { Channel = "#feed-b", Pattern = "y" }];
        config.Servers[0].Irc.Channels = [new() { Name = "#FEED-A", AutoJoin = true }, new() { Name = "#feed-b", AutoJoin = true }];
        Assert.Empty(Check(Context(config) with { Servers = [Runtime()] }));
        var rows = Check(Context(config) with { Servers = [Runtime() with { IrcConnected = true }] });
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(p => p.Id).Distinct().Count());
        Assert.All(rows, p => Assert.False(p.FixAvailable));
        config.Servers[0].Irc.Channels[1].AutoJoin = false;
        var row = Assert.Single(Check(Context(config) with { Servers = [Runtime()] }));
        Assert.True(row.FixAvailable);
        Assert.Contains("SITE INVITE", row.Fix!.Impact);
    }

    [Fact]
    public void SilenceRequiresLearnedBaselineAndExistingDownEpisode()
    {
        var config = Config();
        config.Servers[0].Irc.Enabled = true;
        var feed = new AnnounceFeedSnapshot("server-a", "#feed", "Down", Now.AddHours(-4).UtcDateTime,
            Now.AddHours(-1).UtcDateTime, 240, 12, true, "Channel joined", true, true, null);
        Assert.Single(Check(Context(config) with { Servers = [Runtime() with { Feeds = [feed] }] }));
        Assert.Empty(Check(Context(config) with { Servers = [Runtime() with { Feeds = [feed with { BaselineReady = false }] }] }));
        Assert.Empty(Check(Context(config) with { Servers = [Runtime() with { Feeds = [feed with { State = "Learning" }] }] }));
    }

    [Fact]
    public void PendingDecisionsAreReviewOnly()
    {
        var rows = Check(Context(Config()) with
        {
            FrozenDecisions = [new("proposal-1", "/servers/server-a/spread/sitePriority", Now.ToString("O"), "Operator preference")],
            Certificates = [new("cert-1", "ftp.example.invalid:21", Now.UtcDateTime, false), new("cert-2", "irc.example.invalid:6697", Now.UtcDateTime, true)]
        });
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => { Assert.False(row.FixAvailable); Assert.Null(row.Fix); });
        Assert.Contains(rows, row => row.Evidence.Contains("irc.example.invalid"));
        Assert.All(rows.Where(row => row.Id.StartsWith("certificate-decision:")),
            row => Assert.Contains("Certificate decisions window from Settings", row.Remedy));
    }

    [Fact]
    public void DuplicateLettersExcludeNonMountingAndDisabledServers()
    {
        var config = Config();
        config.Servers.Add(new() { Id = "server-b", Mount = new() { DriveLetter = "g:" } });
        Assert.Empty(Check(Context(config)));
        config.Servers[0].Mount.MountDrive = true;
        Assert.Single(Check(Context(config)));
        config.Servers[1].Enabled = false;
        Assert.Empty(Check(Context(config)));
    }

    [Fact]
    public void ResultsAreBoundedWithoutChangingOperationalConfiguration()
    {
        var config = Config();
        config.Servers[0].Search.SearchPaths = [];
        for (int i = 0; i < 400; i++) config.Servers[0].SpreadSite.Sections["section-" + i] = "/section-" + i;
        var snapshot = HealthDoctor.Evaluate(Context(config), Now);
        Assert.True(snapshot.Partial);
        Assert.InRange(snapshot.Problems.Count, 1, HealthDoctor.MaxProblems);
        Assert.Equal(400, config.Servers[0].SpreadSite.Sections.Count);
    }
}
