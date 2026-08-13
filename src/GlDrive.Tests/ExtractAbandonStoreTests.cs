using System;
using System.IO;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-12 finding: ExtractFailureClassifier correctly
/// ruled three archives Permanent, but the give-up set (<c>_watchAbandoned</c>) lived
/// only in an ExtractorWindow field. Every app restart — and GlDrive restarts often
/// (auto-update, watchdog, sleep/resume) — replayed the full extraction against a
/// volume set that provably cannot extract. Observed 21 abandon events across only 3
/// distinct archives over three days, each re-reading GBs and emitting WRN + stacks.
///
/// The verdict is a property of the VOLUME SET, so the memory is keyed to that set's
/// fingerprint: a new volume arriving (count or total size changes) must revive it.
/// </summary>
public sealed class ExtractAbandonStoreTests
{
    private static ExtractAbandonStore NewStore() => new(
        Path.Combine(Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-extract-abandoned.json"));

    private const string Rar = @"E:\movies\Hackers\hackers.rar";

    [Fact]
    public void Unknown_path_is_not_skipped()
    {
        var s = NewStore();
        Assert.False(s.ShouldSkip(Rar, volumeCount: 3, totalBytes: 100));
    }

    [Fact]
    public void Recorded_path_is_skipped_when_volume_set_is_unchanged()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "UnRAR.exe failed (exit 3)");
        Assert.True(s.ShouldSkip(Rar, 3, 100));
    }

    [Fact]
    public void Path_matching_is_case_insensitive()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "incomplete");
        Assert.True(s.ShouldSkip(Rar.ToUpperInvariant(), 3, 100));
    }

    // The whole point of the fingerprint: the missing volume finally downloads.
    [Fact]
    public void A_new_volume_revives_the_path()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "Entry expects a new volume");
        Assert.False(s.ShouldSkip(Rar, volumeCount: 4, totalBytes: 150));
    }

    [Fact]
    public void A_grown_volume_revives_the_path_even_at_the_same_count()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "unpacked file size does not match header");
        Assert.False(s.ShouldSkip(Rar, volumeCount: 3, totalBytes: 4_000_000));
    }

    // A revived path must not stay poisoned by the stale fingerprint.
    [Fact]
    public void Reviving_drops_the_stale_entry()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "incomplete");
        Assert.False(s.ShouldSkip(Rar, 4, 150));
        Assert.False(s.ShouldSkip(Rar, 3, 100)); // entry gone, not resurrected
    }

    [Fact]
    public void Record_reports_first_time_only()
    {
        var s = NewStore();
        Assert.True(s.Record(Rar, 3, 100, "incomplete"));
        Assert.False(s.Record(Rar, 3, 100, "incomplete"));
    }

    [Fact]
    public void Forget_clears_the_entry()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "incomplete");
        s.Forget(Rar);
        Assert.False(s.ShouldSkip(Rar, 3, 100));
    }

    // The behaviour the bug was about: the verdict must survive a process restart.
    [Fact]
    public void Verdict_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-extract-abandoned.json");

        var first = new ExtractAbandonStore(path);
        first.Record(Rar, 3, 100, "UnRAR.exe failed (exit 3)");

        var reloaded = new ExtractAbandonStore(path);
        reloaded.Load();
        Assert.True(reloaded.ShouldSkip(Rar, 3, 100));
        Assert.False(reloaded.ShouldSkip(Rar, 4, 150));
    }

    // Safety net: never freeze a path forever if fingerprinting ever misses a change.
    [Fact]
    public void Entries_expire_after_the_ttl()
    {
        var s = NewStore();
        s.Record(Rar, 3, 100, "incomplete");
        s.AgeEntryForTest(Rar, ExtractAbandonStore.EntryTtl + TimeSpan.FromHours(1));
        Assert.False(s.ShouldSkip(Rar, 3, 100));
    }

    [Fact]
    public void Load_on_a_missing_file_is_a_no_op()
    {
        var s = new ExtractAbandonStore(Path.Combine(Path.GetTempPath(), "gldrive-tests",
            Guid.NewGuid().ToString("N") + "-does-not-exist.json"));
        s.Load();
        Assert.False(s.ShouldSkip(Rar, 3, 100));
    }
}
