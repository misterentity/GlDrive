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

/// <summary>
/// v3.10.47 — a section blacklist must never be written from, or survive, the site
/// refusing a leading-dot metadata sidecar. GlDrive itself should never have offered
/// those files; v3.10.44 stopped racing them, but one entry the bug had already
/// written (superbnc/[tv-hd], ".imdbinfoname: path-filter denied", failureCount 46)
/// outlived the fix and removed the release's only source from 375 races in a day.
/// </summary>
public class LeadingDotMetadataBlacklistTests
{
    private static SectionBlacklistStore NewStore() => new(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-blacklist.json"));

    [Theory]
    // The exact reason string found in the live section-blacklist.json.
    [InlineData("STOR failed: 553 .imdbinfoname: path-filter denied permission. (Filename deny)")]
    [InlineData("STOR failed: 553 .imdb: path-filter denied permission. (Filename deny)")]
    [InlineData("STOR failed: 553 .message: path-filter denied permission. (Filename deny)")]
    // Keyed on the leading dot, so a sidecar never seen before is still caught.
    [InlineData("STOR failed: 553 .someNewSidecar: path-filter denied permission. (Filename deny)")]
    public void Leading_dot_metadata_denials_are_recognised(string reason)
        => Assert.True(MkdFailureClassifier.IsLeadingDotMetadataDenial(reason));

    [Theory]
    // A real content file rejected by a path filter IS a genuine section denial.
    [InlineData("STOR failed: 553 release.r00: path-filter denied permission. (Filename deny)")]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!")]
    [InlineData("550 Error: Not allowed to make directories here.")]
    [InlineData("Unable to read data from the transport connection")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_metadata_denials_are_left_alone(string? reason)
        => Assert.False(MkdFailureClassifier.IsLeadingDotMetadataDenial(reason));

    [Fact]
    public void Scene_names_are_dot_separated_but_never_dot_prefixed()
    {
        // The discriminator only works because a scene basename always precedes the
        // first dot. Guard it explicitly so nobody "simplifies" it to Contains('.').
        Assert.False(MkdFailureClassifier.IsLeadingDotMetadataDenial(
            "STOR failed: 553 the.proud.family.s04e09.720p.web.h264-afo.nfo: path-filter denied permission."));
    }

    [Fact]
    public void Metadata_denial_never_creates_a_section_blacklist_entry()
    {
        var s = NewStore();
        s.RecordPermanentFailure("bb90928a", "superbnc", "tv-hd",
            "/incoming/tv-hd/The.Proud.Family.Louder.and.Prouder.S04E09.720p.WEB.H264-AFO",
            "STOR failed: 553 .imdbinfoname: path-filter denied permission. (Filename deny)");

        Assert.False(s.IsBlacklisted("bb90928a", "tv-hd"));
        Assert.Equal(0, s.DistinctActiveSectionCount("bb90928a"));
    }

    [Fact]
    public void A_genuine_upload_denial_still_creates_an_entry()
    {
        // The guard must not swallow real denials — that would be the opposite bug.
        var s = NewStore();
        s.RecordPermanentFailure("bb90928a", "superbnc", "0day", "/incoming/0day/x",
            "STOR failed: 553 Error: you have no upload rights for this directory!");

        Assert.True(s.IsBlacklisted("bb90928a", "0day"));
    }
}

/// <summary>
/// v3.10.47 — end-to-end reproduction of the tv-hd outage. The fixture below is the
/// verbatim entry found in the live %AppData%\GlDrive\section-blacklist.json on
/// 2026-08-03; loading it must no longer blacklist superbnc for [tv-hd].
/// </summary>
public class TvHdFossilEntryScrubTests
{
    // Verbatim reasons from the live store. Timestamps are kept RECENT so these
    // assertions test the scrub, not the unrelated 14-day age-out.
    private static string LiveFossilJson()
    {
        var recent = DateTime.UtcNow.AddDays(-1).ToString("O");
        return $$"""
        [
          {
            "serverId": "bb90928a",
            "serverName": "superbnc.xxxxx.tw",
            "section": "tv-hd",
            "path": "/incoming/tv-hd/The.Proud.Family.Louder.and.Prouder.S04E09.720p.WEB.H264-AFO",
            "reason": "STOR failed: 553 .imdbinfoname: path-filter denied permission. (Filename deny)",
            "firstFailedAt": "{{recent}}",
            "lastFailedAt": "{{recent}}",
            "failureCount": 46
          },
          {
            "serverId": "bb90928a",
            "serverName": "superbnc.xxxxx.tw",
            "section": "0day",
            "path": "/incoming/0day/Destiny_Powers_Fairy_Place-RAZOR",
            "reason": "STOR failed: 553 Error: you have no upload rights for this directory!",
            "firstFailedAt": "{{recent}}",
            "lastFailedAt": "{{recent}}",
            "failureCount": 3
          }
        ]
        """;
    }

    private static SectionBlacklistStore LoadFrom(string json)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-blacklist.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, json);
        var store = new SectionBlacklistStore(path);
        store.Load();
        return store;
    }

    [Fact]
    public void The_live_tv_hd_fossil_entry_is_scrubbed_on_load()
    {
        var store = LoadFrom(LiveFossilJson());

        // This entry removed superbnc — the release's only source — from 375 of 377
        // [tv-hd] races on 2026-08-03.
        Assert.False(store.IsBlacklisted("bb90928a", "tv-hd"));
    }

    [Fact]
    public void A_real_upload_denial_in_the_same_file_survives_the_scrub()
    {
        var store = LoadFrom(LiveFossilJson());

        // superbnc genuinely has no upload rights for 0day — that entry must remain,
        // or the scrub would be indiscriminate.
        Assert.True(store.IsBlacklisted("bb90928a", "0day"));
    }
}
