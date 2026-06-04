using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class SectionFolderDigesterTests
{
    private static MatchedAnnounceEvent Ann(string server, string section, string type, string quality) => new()
    {
        ServerId = server,
        Section = section,
        ParsedType = type,
        Quality = quality
    };

    private static RaceOutcomeEvent Race(string section, string resolved, string result) => new()
    {
        Section = section,
        ResolvedRemoteSection = resolved,
        Result = result
    };

    [Fact]
    public void Empty_inputs_yield_empty_digest()
    {
        var d = new SectionFolderDigester().Build([], []);
        Assert.Empty(d.Rows);
    }

    [Fact]
    public void Empty_announces_with_races_yields_empty_digest()
    {
        var d = new SectionFolderDigester().Build([], [Race("tv", "X264-HD", "complete")]);
        Assert.Empty(d.Rows);
    }

    [Fact]
    public void Groups_by_server_section_type_quality_and_counts()
    {
        var announces = new[]
        {
            Ann("alpha", "tv", "tv", "1080p"),
            Ann("alpha", "tv", "tv", "1080p"),
            Ann("alpha", "tv", "tv", "720p"),   // different quality → separate row
            Ann("beta", "tv", "tv", "1080p"),    // different server → separate row
        };

        var d = new SectionFolderDigester().Build(announces, []);

        Assert.Equal(3, d.Rows.Count);
        var alpha1080 = Assert.Single(d.Rows, r => r.ServerId == "alpha" && r.Quality == "1080p");
        Assert.Equal(2, alpha1080.AnnounceCount);
        Assert.Equal("tv", alpha1080.IrcSection);
        Assert.Equal("tv", alpha1080.ParsedType);
        Assert.Equal(0, alpha1080.RaceCount);
        Assert.Equal(0.0, alpha1080.RaceCompletionRate);
        Assert.Equal("", alpha1080.ObservedRemoteSection);
    }

    [Fact]
    public void ObservedRemoteSection_picks_dominant_resolved_section_case_insensitive()
    {
        var announces = new[] { Ann("alpha", "TV", "tv", "1080p") };
        var races = new[]
        {
            Race("tv", "X264-HD", "complete"),
            Race("TV", "x264-hd", "complete"), // same dest, case-insensitive section match
            Race("tv", "X265-HD", "aborted"),
        };

        var d = new SectionFolderDigester().Build(announces, races);

        var row = Assert.Single(d.Rows);
        Assert.Equal("X264-HD", row.ObservedRemoteSection); // 2x X264-HD beats 1x X265-HD
        Assert.Equal(3, row.RaceCount);                     // all 3 match "TV"/"tv" case-insensitively
    }

    [Fact]
    public void RaceCompletionRate_is_fraction_of_complete_results()
    {
        var announces = new[] { Ann("alpha", "mp3", "music", "") };
        var races = new[]
        {
            Race("mp3", "MP3", "complete"),
            Race("mp3", "MP3", "complete"),
            Race("mp3", "MP3", "aborted"),
            Race("mp3", "MP3", "blacklisted"),
        };

        var d = new SectionFolderDigester().Build(announces, races);

        var row = Assert.Single(d.Rows);
        Assert.Equal(4, row.RaceCount);
        Assert.Equal(0.5, row.RaceCompletionRate, 3);
        Assert.Equal("MP3", row.ObservedRemoteSection);
    }

    [Fact]
    public void Empty_resolved_sections_are_ignored_for_observed()
    {
        var announces = new[] { Ann("alpha", "tv", "tv", "1080p") };
        var races = new[]
        {
            Race("tv", "", "complete"),       // empty resolved → ignored for observed
            Race("tv", "  ", "complete"),     // whitespace → ignored
            Race("tv", "X264-HD", "complete"),
        };

        var d = new SectionFolderDigester().Build(announces, races);

        var row = Assert.Single(d.Rows);
        Assert.Equal("X264-HD", row.ObservedRemoteSection);
        Assert.Equal(3, row.RaceCount); // race count still counts all section matches
    }

    [Fact]
    public void Races_for_other_sections_do_not_correlate()
    {
        var announces = new[] { Ann("alpha", "tv", "tv", "1080p") };
        var races = new[]
        {
            Race("mp3", "MP3", "complete"),   // different section → not correlated
            Race("xvid", "XVID", "complete"),
        };

        var d = new SectionFolderDigester().Build(announces, races);

        var row = Assert.Single(d.Rows);
        Assert.Equal(0, row.RaceCount);
        Assert.Equal("", row.ObservedRemoteSection);
        Assert.Equal(0.0, row.RaceCompletionRate);
    }

    [Fact]
    public void Caps_rows_to_top_40_by_announce_count()
    {
        var announces = new List<MatchedAnnounceEvent>();
        // 50 distinct sections, section N announced (N+1) times so volume is strictly decreasing.
        for (int n = 0; n < 50; n++)
            for (int i = 0; i <= n; i++)
                announces.Add(Ann("alpha", $"sec{n:D2}", "tv", "1080p"));

        var d = new SectionFolderDigester().Build(announces, []);

        Assert.Equal(40, d.Rows.Count);
        // Top row must be the highest-volume section (sec49, 50 announces).
        Assert.Equal("sec49", d.Rows[0].IrcSection);
        Assert.Equal(50, d.Rows[0].AnnounceCount);
        // The 10 lowest-volume sections (sec00..sec09) must be dropped.
        Assert.DoesNotContain(d.Rows, r => r.IrcSection == "sec00");
        Assert.DoesNotContain(d.Rows, r => r.IrcSection == "sec09");
    }
}
