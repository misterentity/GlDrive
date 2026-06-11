using System;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class BlacklistStoreTests
{
    // RecordPermanentFailure persists; tests use a fresh store and rely on the
    // in-memory state. The Load() path is exercised on startup; here we focus on
    // logic (Distinct count + IsBlacklisted) that drives PRD R2 self-healing.
    // MUST use the path-override ctor: the default ctor points at the user's
    // LIVE %AppData% store, and these tests were overwriting it on every run.
    private static SectionBlacklistStore NewStore() => new(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-blacklist.json"));

    [Fact]
    public void DistinctActiveSectionCount_zero_for_unknown_server()
    {
        var s = NewStore();
        Assert.Equal(0, s.DistinctActiveSectionCount("any-server"));
    }

    [Fact]
    public void DistinctActiveSectionCount_grows_with_distinct_sections()
    {
        var s = NewStore();
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
        var s = NewStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/a", "denied");
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/b", "denied again");
        Assert.Equal(1, s.DistinctActiveSectionCount("srv1"));
    }

    [Fact]
    public void IsBlacklisted_true_after_record_and_within_ttl()
    {
        var s = NewStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/x", "denied");
        Assert.True(s.IsBlacklisted("srv1", "mp3"));
        Assert.True(s.IsBlacklisted("srv1", "MP3"));   // case-insensitive
    }

    [Fact]
    public void IsBlacklisted_false_when_section_not_recorded()
    {
        var s = NewStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3", "/mp3/x", "denied");
        Assert.False(s.IsBlacklisted("srv1", "x265"));
        Assert.False(s.IsBlacklisted("srv2", "mp3"));
    }

    [Fact]
    public void DistinctActiveSectionCount_threshold_3_drives_auto_download_only()
    {
        // PRD R2 acceptance: ">=3 distinct-section permanent denials"
        var s = NewStore();
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
        var s = NewStore();
        s.RecordPermanentFailure("srv1", "Server One", "tv-hd",     "/tv-hd",     "out of disk space, contact the siteop!");
        s.RecordPermanentFailure("srv1", "Server One", "tv-sports", "/tv-sports", "out of disk space, contact the siteop!");
        s.RecordPermanentFailure("srv1", "Server One", "games",     "/games",     "disk full");
        Assert.Equal(0, s.DistinctActiveSectionCount("srv1"));    // transient — doesn't count
        // The per-section blacklist still applies short-term so we don't pile on
        // a full disk in the same moment.
        Assert.True(s.IsBlacklisted("srv1", "tv-hd"));
    }

    [Fact]
    public void Mkd_path_and_disk_full_denials_excluded_from_distinct_count()
    {
        // v3.8.4: MKD path rejections ("Not allowed to make directories here.")
        // mean the section path is wrong for THIS server — not that it's leech-only
        // — so they must NOT count toward the auto-download-only blanket (which
        // deadlocked SYN out of every race on 2026-06-08). Disk-full stays
        // transient-excluded. Only the persistent, non-MKD-path "Permission denied"
        // remains in the count.
        var s = NewStore();
        s.RecordPermanentFailure("srv1", "Server One", "mp3",   "/mp3",   "Not allowed to make directories here.");
        s.RecordPermanentFailure("srv1", "Server One", "flac",  "/flac",  "Permission denied");
        s.RecordPermanentFailure("srv1", "Server One", "tv-hd", "/tv-hd", "out of disk space, contact the siteop!");
        Assert.Equal(1, s.DistinctActiveSectionCount("srv1"));   // mp3 (mkd-path) + tv-hd (disk) excluded
        // The per-section blacklist still applies to the MKD-denied section.
        Assert.True(s.IsBlacklisted("srv1", "mp3"));
    }

    [Fact]
    public void Three_mkd_path_denials_do_not_trigger_auto_download_only()
    {
        // Regression for the 2026-06-08 SYN deadlock: flac/mp3/nsw all returned
        // 550 "Not allowed to make directories here." and the >=3 distinct-section
        // count wrongly flagged the whole site download-only, blocking EVERY race.
        var s = NewStore();
        s.RecordPermanentFailure("770fa16a", "SYN", "flac", "/flac/x", "550 Error: Not allowed to make directories here.");
        s.RecordPermanentFailure("770fa16a", "SYN", "mp3",  "/mp3/y",  "550 Error: Not allowed to make directories here.");
        s.RecordPermanentFailure("770fa16a", "SYN", "nsw",  "/nsw/z",  "550 Error: Not allowed to make directories here.");
        Assert.Equal(0, s.DistinctActiveSectionCount("770fa16a"));   // NOT auto-download-only
        Assert.True(s.IsBlacklisted("770fa16a", "flac"));            // each section still individually skipped
    }

    [Fact]
    public void Three_upload_rights_denials_still_trigger_auto_download_only()
    {
        // R2 preserved for the genuine signal: a leech site that reaches STOR and is
        // rejected with "no upload rights" SHOULD still be auto-flagged download-only.
        var s = NewStore();
        s.RecordPermanentFailure("srv9", "Leech", "mp3",  "/mp3",  "STOR failed: 553 Error: you have no upload rights for this directory!");
        s.RecordPermanentFailure("srv9", "Leech", "flac", "/flac", "STOR failed: 553 Error: you have no upload rights for this directory!");
        s.RecordPermanentFailure("srv9", "Leech", "x265", "/x265", "STOR failed: 553 Error: you have no upload rights for this directory!");
        Assert.True(s.DistinctActiveSectionCount("srv9") >= 3);
    }

    [Theory]
    [InlineData("550 Error: Not allowed to make directories here.", true)]
    [InlineData("You cannot create that directory", true)]
    [InlineData("MKD failed: 550 MKD Denied by dirscript.", true)]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!", false)]
    [InlineData("Permission denied", false)]
    [InlineData("", false)]
    public void IsPermanentMkdPathDenial_classifies_correctly(string reason, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsPermanentMkdPathDenial(reason));

    [Theory]
    [InlineData("550 MKD Denied by dirscript.", true)]
    [InlineData("MKD failed: 550 Denied by dirscript", true)]
    [InlineData("550 Error: Not allowed to make directories here.", false)]   // section-scoped, stays blacklisted
    [InlineData("out of disk space", false)]
    [InlineData("", false)]
    public void IsReleaseScopedDirscriptDenial_classifies_correctly(string reason, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsReleaseScopedDirscriptDenial(reason));

    [Fact]
    public void Load_scrubs_release_scoped_dirscript_entries()
    {
        // v3.8.8 migration: dirscript denials recorded per-section by earlier
        // versions soft-locked zephyr out of entire sections (x72 on 2026-06-10).
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-blacklist.json");
        var w = new SectionBlacklistStore(path);
        w.RecordPermanentFailure("zephyr", "zephyr", "tv-sports", "/TV/Some.Release", "550 MKD Denied by dirscript.");
        w.RecordPermanentFailure("syn", "SYN", "mp3", "/mp3/x", "550 Error: Not allowed to make directories here.");

        var r = new SectionBlacklistStore(path);
        r.Load();
        Assert.False(r.IsBlacklisted("zephyr", "tv-sports"));   // scrubbed
        Assert.True(r.IsBlacklisted("syn", "mp3"));             // kept
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
