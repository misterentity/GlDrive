using System;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class BlacklistStoreTests
{
    // RecordPermanentFailure persists; tests use a fresh store and rely on the
    // in-memory state. The Load() path is exercised on startup; here we focus on
    // logic (Distinct count + IsBlacklisted) that drives PRD R2 self-healing.

    [Fact]
    public void DistinctActiveSectionCount_zero_for_unknown_server()
    {
        var s = new SectionBlacklistStore();
        Assert.Equal(0, s.DistinctActiveSectionCount("any-server"));
    }

    [Fact]
    public void DistinctActiveSectionCount_grows_with_distinct_sections()
    {
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/x", "denied");
        s.RecordPermanentFailure("srv1", "Server One", "flac", "/flac/y", "denied");
        s.RecordPermanentFailure("srv1", "Server One", "x265", "/x265/z", "denied");
        Assert.Equal(3, s.DistinctActiveSectionCount("srv1"));
        // Other servers unaffected
        Assert.Equal(0, s.DistinctActiveSectionCount("srv2"));
    }

    [Fact]
    public void DistinctActiveSectionCount_does_not_double_count_same_section()
    {
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/a", "denied");
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/b", "denied again");
        Assert.Equal(1, s.DistinctActiveSectionCount("srv1"));
    }

    [Fact]
    public void IsBlacklisted_true_after_record_and_within_ttl()
    {
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/x", "denied");
        Assert.True(s.IsBlacklisted("srv1", "mp3"));
        Assert.True(s.IsBlacklisted("srv1", "MP3"));   // case-insensitive
    }

    [Fact]
    public void IsBlacklisted_false_when_section_not_recorded()
    {
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/x", "denied");
        Assert.False(s.IsBlacklisted("srv1", "x265"));
        Assert.False(s.IsBlacklisted("srv2", "mp3"));
    }

    [Fact]
    public void DistinctActiveSectionCount_threshold_3_drives_auto_download_only()
    {
        // PRD R2 acceptance: ">=3 distinct-section permanent denials"
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3", "x");
        s.RecordPermanentFailure("srv1", "Server One", "flac", "/flac", "x");
        Assert.True(s.DistinctActiveSectionCount("srv1") < 3); // not yet
        s.RecordPermanentFailure("srv1", "Server One", "x265", "/x265", "x");
        Assert.True(s.DistinctActiveSectionCount("srv1") >= 3); // now triggers auto-DL
    }

    [Fact]
    public void Disk_full_entries_are_transient_and_excluded_from_distinct_count()
    {
        // v3.5.2 regression guard. Three disk-full denials should NOT trip the
        // auto-download-only blanket — the dest will accept uploads again once
        // the siteop frees space.
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "tv-hd",     "/tv-hd",     "out of disk space, contact the siteop!");
        s.RecordPermanentFailure("srv1", "Server One", "tv-sports", "/tv-sports", "out of disk space, contact the siteop!");
        s.RecordPermanentFailure("srv1", "Server One", "games",     "/games",     "disk full");
        Assert.Equal(0, s.DistinctActiveSectionCount("srv1"));    // transient — doesn't count
        // The per-section blacklist still applies short-term so we don't pile on
        // a full disk in the same moment.
        Assert.True(s.IsBlacklisted("srv1", "tv-hd"));
    }

    [Fact]
    public void Persistent_and_transient_reasons_count_separately()
    {
        var s = new SectionBlacklistStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3",   "/mp3",   "Not allowed to make directories here.");
        s.RecordPermanentFailure("srv1", "Server One", "flac",  "/flac",  "Permission denied");
        s.RecordPermanentFailure("srv1", "Server One", "tv-hd", "/tv-hd", "out of disk space, contact the siteop!");
        Assert.Equal(2, s.DistinctActiveSectionCount("srv1"));   // tv-hd excluded
    }

    [Theory]
    [InlineData("out of disk space, contact the siteop!", true)]
    [InlineData("disk full", true)]
    [InlineData("no space on device", true)]
    [InlineData("Not allowed to make directories here.", false)]
    [InlineData("Denied by dirscript", false)]
    [InlineData("", false)]
    public void IsTransientReason_classifies_correctly(string reason, bool expected)
        => Assert.Equal(expected, SectionBlacklistStore.IsTransientReason(reason));
}

public class RaceSummarizeTests
{
    private static RaceHistoryStore Store(params (SpreadJobState state, bool clean, string failCat)[] entries)
    {
        var s = new RaceHistoryStore();
        foreach (var (state, clean, cat) in entries)
        {
            s.Add(new RaceHistoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Result = state,
                CleanComplete = clean,
                FailureCategory = cat,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
        }
        return s;
    }

    [Fact]
    public void Empty_store_summary()
    {
        var sum = new RaceHistoryStore().Summarize();
        Assert.Equal(0, sum.Finished);
        Assert.Equal(0.0, sum.CleanRate);
    }

    [Fact]
    public void Mixed_outcomes_summarize_correctly()
    {
        var s = Store(
            (SpreadJobState.Completed, true,  ""),
            (SpreadJobState.Completed, true,  ""),
            (SpreadJobState.Completed, false, ""),               // partial
            (SpreadJobState.Failed,    false, "upload-denied"),
            (SpreadJobState.Failed,    false, "upload-denied"),
            (SpreadJobState.Failed,    false, "bnc-pressure"),
            (SpreadJobState.Stopped,   false, ""));
        var sum = s.Summarize();
        Assert.Equal(7, sum.Finished);
        Assert.Equal(2, sum.Clean);
        Assert.Equal(3, sum.Failed);
        Assert.Equal(2.0 / 7, sum.CleanRate, 3);
        Assert.Equal(2, sum.FailureCounts["upload-denied"]);
        Assert.Equal(1, sum.FailureCounts["bnc-pressure"]);
    }

    [Fact]
    public void Running_races_excluded_from_summary()
    {
        var s = Store(
            (SpreadJobState.Running,   false, ""),
            (SpreadJobState.Completed, true,  ""));
        Assert.Equal(1, s.Summarize().Finished);
    }
}
