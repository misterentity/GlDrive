using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.33 — a race shipped ANOTHER release's rar volumes.
///
/// 2026-10-27 03:00 Line.of.Fire.2026.S01E06.1080p.WEB.H264-CAKES: for ~70 s superbnc listed
/// below.deck.s13e03.1080p.web.h264-hotdogwater.r03..r09 inside the Line.of.Fire dir (its RETR
/// even answered 150 with a size), while that race had already parsed its own SFV. The race
/// counted 30 files for a 22-file release, FXP'd 100 MB of below.deck .r09 into zephyr's
/// /TV/Line.of.Fire dir (zipscript deleted it), and held its one race slot ~60 s after zephyr
/// was COMPLETE while Below.Deck itself sat queued. Same shape 10-26 (Minimum.Wage listed
/// marshals.*-sauerkraut volumes) and 10-27 (90.Day.Hunt listed west.indies.*-dev0 volumes).
///
/// The SFV inventory was only ever used to COUNT. It is also the authority on which payload
/// files belong to the directory it sits in. A file-name heuristic is wrong (ViCEROYS ships
/// "viceroys-sihfnl261025_gsevzsc_en720p.r00" in an "SIHF.National.League..." dir), so the
/// defining property is: same payload kind as the SFV declares, in the SFV's directory, and
/// not declared by it.
/// </summary>
public sealed class SpreadSfvForeignFileTests
{
    private static readonly Dictionary<string, HashSet<string>> LineOfFire = new(StringComparer.OrdinalIgnoreCase)
    {
        ["line.of.fire.sfv"] = new(StringComparer.OrdinalIgnoreCase)
            { "line.of.fire.sfv", "line.of.fire.rar", "line.of.fire.r00", "line.of.fire.r01" }
    };

    [Theory]
    [InlineData("below.deck.s13e03.1080p.web.h264-hotdogwater.r09")]
    [InlineData("below.deck.s13e03.1080p.web.h264-hotdogwater.rar")]
    [InlineData("marshals.s02e04.german.dl.1080p.web.h264-sauerkraut.s01")]
    [InlineData("west.indies.1979.1080p.bluray.x264-dev0.045")]
    [InlineData("line.of.fire.r02")] // same group, but the SFV says the set ends at .r01
    [InlineData("other.release.sfv")]
    public void An_undeclared_volume_in_the_sfv_directory_is_foreign(string name) =>
        Assert.True(SpreadJob.IsUndeclaredBySfv(name, LineOfFire));

    [Theory]
    [InlineData("line.of.fire.r00")]
    [InlineData("LINE.OF.FIRE.R01")]
    [InlineData("line.of.fire.sfv")]
    [InlineData("line.of.fire.nfo")]          // releases ship an nfo the SFV never lists
    [InlineData("Sample/line.of.fire-sample.mkv")] // no SFV covers Sample/
    [InlineData("Proof/line.of.fire-proof.jpg")]
    public void Declared_files_and_non_payload_extras_still_race(string name) =>
        Assert.False(SpreadJob.IsUndeclaredBySfv(name, LineOfFire));

    [Fact]
    public void Without_a_parsed_sfv_nothing_is_foreign() =>
        Assert.False(SpreadJob.IsUndeclaredBySfv("below.deck.r09", new Dictionary<string, HashSet<string>>()));

    [Fact]
    public void An_sfv_only_rules_its_own_directory()
    {
        var cd1 = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CD1/a.sfv"] = new(StringComparer.OrdinalIgnoreCase) { "CD1/a.sfv", "CD1/01-track.flac" }
        };
        Assert.True(SpreadJob.IsUndeclaredBySfv("CD1/99-foreign.flac", cd1));
        Assert.False(SpreadJob.IsUndeclaredBySfv("CD2/01-track.flac", cd1));
        Assert.False(SpreadJob.IsUndeclaredBySfv("01-track.flac", cd1));
        Assert.False(SpreadJob.IsUndeclaredBySfv("CD1/a.m3u", cd1));
    }

    [Fact]
    public async Task A_foreign_volume_listed_after_the_sfv_never_enters_the_race()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "line.of.fire.sfv", "line.of.fire.rar", "line.of.fire.r00");
        Assert.True(fixture.Job.ApplySfvInventory("line.of.fire.sfv",
            "line.of.fire.rar 00000000\nline.of.fire.r00 00000000\n"));

        fixture.Observe("source", "line.of.fire.sfv", "line.of.fire.rar", "line.of.fire.r00",
            "line.of.fire.nfo", "below.deck.r09");

        var infos = fixture.FileInfos;
        Assert.DoesNotContain("below.deck.r09", infos.Keys);
        Assert.Contains("line.of.fire.nfo", infos.Keys);
        Assert.Equal(4, infos.Count);
        Assert.Equal(4, fixture.ServerFileCount("source"));
    }

    [Fact]
    public async Task Applying_the_sfv_purges_a_foreign_volume_seen_before_it()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "line.of.fire.sfv", "line.of.fire.rar", "below.deck.r09");
        fixture.Observe("destination", "below.deck.r09");
        Assert.Contains("below.deck.r09", fixture.FileInfos.Keys);

        Assert.True(fixture.Job.ApplySfvInventory("line.of.fire.sfv", "line.of.fire.rar 00000000\n"));

        Assert.DoesNotContain("below.deck.r09", fixture.FileInfos.Keys);
        Assert.Equal(2, fixture.ServerFileCount("source"));
        Assert.Equal(0, fixture.ServerFileCount("destination"));
        Assert.Equal(2, fixture.Total());
    }

    [Fact]
    public async Task A_volume_the_refreshed_sfv_now_declares_rejoins_on_the_next_listing()
    {
        await using var fixture = new Fixture();
        // An SFV caught mid-upload can end on a line boundary and look complete.
        Assert.True(fixture.Job.ApplySfvInventory("line.of.fire.sfv", "line.of.fire.rar 00000000\n"));
        fixture.Observe("source", "line.of.fire.sfv", "line.of.fire.rar", "line.of.fire.r00");
        Assert.DoesNotContain("line.of.fire.r00", fixture.FileInfos.Keys);

        Assert.True(fixture.Job.ApplySfvInventory("line.of.fire.sfv",
            "line.of.fire.rar 00000000\nline.of.fire.r00 00000000\n"));
        fixture.Observe("source", "line.of.fire.sfv", "line.of.fire.rar", "line.of.fire.r00");

        Assert.Contains("line.of.fire.r00", fixture.FileInfos.Keys);
    }

    [Fact]
    public async Task A_resized_sfv_gates_nothing_until_it_is_reread()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "line.of.fire.sfv");
        Assert.True(fixture.Job.ApplySfvInventory("line.of.fire.sfv", "line.of.fire.rar 00000000\n"));

        // The listing that reveals the resize was gated by the old inventory; the next is not.
        fixture.ObserveSized("source", ("line.of.fire.sfv", 140), ("line.of.fire.rar", 100), ("line.of.fire.r00", 100));
        fixture.ObserveSized("source", ("line.of.fire.sfv", 140), ("line.of.fire.rar", 100), ("line.of.fire.r00", 100));

        Assert.Contains("line.of.fire.r00", fixture.FileInfos.Keys);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Dictionary<string, FtpConnectionPool> Pools { get; } = new()
        {
            ["source"] = new(null!, 1), ["destination"] = new(null!, 1)
        };
        public Dictionary<string, string> Paths { get; } = new()
        {
            ["source"] = "/source/Release", ["destination"] = "/dest/Release"
        };
        public SpreadJob Job { get; }

        public Fixture()
        {
            Job = new("TV", "Release", SpreadMode.Race, new SpreadConfig(), Pools, Pools,
                new() { ["source"] = new() { Id = "source" }, ["destination"] = new() { Id = "destination" } },
                new SpeedTracker(), new SkiplistEvaluator());
            Field<HashSet<string>>(Job, "_sourceServersField").Add("source");
            typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Job, Paths);
        }

        public void Observe(string id, params string[] names) =>
            ObserveSized(id, names.Select(n => (n, 100L)).ToArray());

        public void ObserveSized(string id, params (string name, long size)[] files)
        {
            var gate = Field<System.Threading.Lock>(Job, "_ownershipLock");
            lock (gate)
                typeof(SpreadJob).GetMethod("ProcessFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Job,
                    [id, files.Select(f => new SpreadFileInfo { Name = f.name, FullPath = Paths[id] + "/" + f.name, Size = f.size }).ToList(), 0L]);
        }

        public Dictionary<string, SpreadFileInfo> FileInfos => Field<Dictionary<string, SpreadFileInfo>>(Job, "_fileInfos");
        public int ServerFileCount(string id) => Field<Dictionary<string, int>>(Job, "_serverFileCount").GetValueOrDefault(id);
        public int Total() => (int)typeof(SpreadJob).GetMethod("ExpectedTotalNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Job, null)!;

        public async ValueTask DisposeAsync()
        {
            Job.Dispose();
            foreach (var pool in Pools.Values) await pool.DisposeAsync();
        }
    }

    private static T Field<T>(SpreadJob job, string name) =>
        (T)typeof(SpreadJob).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!;
}
