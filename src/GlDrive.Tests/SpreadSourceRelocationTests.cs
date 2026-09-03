using System.IO;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for the v3.10.100 source-relocation defect. glftpd moves a
/// finished release between sections (/incoming/tv-hd -> /recent/tv-hd) without
/// deleting it. The race engine (a) declared such a source "migrated away" after
/// re-probing only its OLD path and then excluded that very site from the alternate
/// search, and (b) built every RETR path from the canonical FullPath — whichever
/// server observed the file first — so even a found alternate would have kept
/// pulling from the stale directory. Steven.Raichlens.Project.Fire.S01E12 was
/// abandoned 0/22 on 2026-09-02 with the release sitting at /recent/tv-hd on the
/// site declared gone.
/// </summary>
public sealed class SpreadSourceRelocationTests
{
    private static readonly string Source = ReadSpreadJob();

    private static ServerConfig Site()
    {
        var cfg = new ServerConfig();
        cfg.SpreadSite.Sections["TV"] = "/incoming/tv-hd";
        cfg.SpreadSite.Sections["X264"] = "/incoming/x264/";
        cfg.Notifications.WatchPath = "/recent/";
        return cfg;
    }

    // ---- pure helpers -------------------------------------------------------

    [Fact]
    public void Candidate_bases_cover_every_section_dir_and_the_watch_path_section()
    {
        var bases = SpreadJob.CandidateBasePaths(Site(), "TV_HD");

        Assert.Equal(new[] { "/incoming/tv-hd", "/incoming/x264", "/recent/tv-hd" }, bases);
    }

    [Fact]
    public void Candidate_bases_are_distinct_when_watch_section_equals_a_section_dir()
    {
        var cfg = Site();
        cfg.Notifications.WatchPath = "/incoming";

        var bases = SpreadJob.CandidateBasePaths(cfg, "tv-hd");

        Assert.Equal(new[] { "/incoming/tv-hd", "/incoming/x264" }, bases);
    }

    [Fact]
    public void Relocation_candidates_exclude_the_directory_already_known_to_be_gone()
    {
        var paths = SpreadJob.RelocationCandidatePaths(
            Site(), "tv-hd", "Some.Show.S01E12-GRP", "/incoming/tv-hd/Some.Show.S01E12-GRP");

        Assert.Equal(
            new[] { "/incoming/x264/Some.Show.S01E12-GRP", "/recent/tv-hd/Some.Show.S01E12-GRP" },
            paths);
    }

    [Fact]
    public void Source_path_is_rebuilt_from_the_sources_current_release_dir()
    {
        var file = new SpreadFileInfo
        {
            Name = "CD1/track01.mp3",
            FullPath = "/incoming/tv-hd/Rel/CD1/track01.mp3",
            Size = 1,
        };

        Assert.Equal("/recent/tv-hd/Rel/CD1/track01.mp3",
            SpreadJob.ResolveSourcePath("/recent/tv-hd/Rel/", file));
    }

    [Fact]
    public void Source_path_falls_back_to_the_observed_full_path_when_no_dir_is_known()
    {
        var file = new SpreadFileInfo { Name = "a.rar", FullPath = "/x/Rel/a.rar", Size = 1 };

        Assert.Equal("/x/Rel/a.rar", SpreadJob.ResolveSourcePath(null, file));
        Assert.Equal("/x/Rel/a.rar", SpreadJob.ResolveSourcePath("", file));
    }

    // ---- wiring (structural) ------------------------------------------------

    [Fact]
    public void Migration_handler_probes_for_a_relocation_before_writing_the_source_off()
    {
        var handler = Source.IndexOf("private async Task HandleSourceMigration(", StringComparison.Ordinal);
        Assert.True(handler >= 0, "HandleSourceMigration no longer exists.");

        var probe = Source.IndexOf("await FindRelocatedSourcePath(srcId", handler, StringComparison.Ordinal);
        var purge = Source.IndexOf("_sourceMigratedAway.Add(srcId)", handler, StringComparison.Ordinal);

        Assert.True(probe > handler, "The migration handler must probe the same site's other section dirs.");
        Assert.True(purge > probe, "The relocation probe must run BEFORE the source's ownership is purged.");
    }

    [Fact]
    public void Relocation_is_followed_through_the_extra_source_path_map()
    {
        var handler = Source.IndexOf("private async Task HandleSourceMigration(", StringComparison.Ordinal);
        var follow = Source.IndexOf("_extraSourcePaths[srcId] = relocated", handler, StringComparison.Ordinal);
        var rescan = Source.IndexOf("_lastSourceScanTime = DateTime.MinValue", handler, StringComparison.Ordinal);

        Assert.True(follow > handler, "A relocated source must be re-pointed via _extraSourcePaths.");
        Assert.True(rescan > follow, "A relocated source must be re-LISTed immediately, not after the rescan interval.");
    }

    [Fact]
    public void Transfer_never_takes_its_retr_path_from_the_canonical_full_path()
    {
        Assert.DoesNotContain("var srcPath = file.FullPath;", Source);
        Assert.Contains("var srcPath = ResolveSourcePath(SourceBasePath(srcId), file);", Source);
    }

    [Fact]
    public void Initial_probe_and_relocation_probe_share_one_candidate_list()
    {
        // Two gates answering "where might this site hold the release?" with different
        // predicates is the v3.10.45 shape; both must consult CandidateBasePaths.
        Assert.Contains("var pathsToProbe = CandidateBasePaths(config, Section);", Source);
        Assert.Contains("RelocationCandidatePaths(config, Section, ReleaseName, currentPath)", Source);
    }

    private static string ReadSpreadJob()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("SpreadJob.cs not found from " + Directory.GetCurrentDirectory());
    }
}
