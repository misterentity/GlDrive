using System.IO;
using GlDrive.Config;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukeWatchStoreRegressionTests : IDisposable
{
    private const string Original = "Example.Movie.2026.1080p.WEB.H264-GROUP";
    private const string Proper = "Example.Movie.2026.PROPER.1080p.WEB.H264-OTHER";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-nuke-watch-" + Guid.NewGuid().ToString("N"));
    private readonly NukeWatchConfig _config = new();
    private DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private string StatePath => Path.Combine(_root, "state.json");
    public NukeWatchStoreRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private NukeWatchStore Store(string? path = null) => new(path ?? StatePath, () => _config, () => _now);
    private OwnedReleaseObservation Owned(string release = Original, DateTime? at = null) =>
        new(release, Path.Combine(_root, "owned", release), at ?? _now);
    private NukeWatchEvidence Nuke(string source = "predb", string release = Original, bool active = true) =>
        new(release, source, "bad.ivtc", _now, active);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nuke_and_ownership_can_arrive_in_either_order(bool nukeFirst)
    {
        var store = Store();
        if (nukeFirst) store.ObserveNuke(Nuke());
        store.ObserveOwned(Owned());
        if (!nukeFirst) store.ObserveNuke(Nuke());
        var entry = Assert.Single(store.Snapshot());
        Assert.True(entry.Flagged);
        Assert.True(entry.QualityNuke);
        Assert.Contains("predb", entry.Sources);
        Assert.True(_config.Enabled);
        Assert.False(_config.AutoQueueFixes);
    }

    [Fact]
    public void Site_unnuke_clears_only_that_source_and_predb_non_nuke_is_not_clearance()
    {
        var store = Store(); store.ObserveOwned(Owned());
        store.ObserveNuke(Nuke("site:a")); store.ObserveNuke(Nuke("site:b")); store.ObserveNuke(Nuke());
        _now = _now.AddSeconds(1);
        store.ObserveNuke(Nuke("site:a", active: false));
        Assert.DoesNotContain("site:a", Assert.Single(store.Snapshot()).Sources);
        Assert.Contains("site:b", Assert.Single(store.Snapshot()).Sources);
        store.ObserveNuke(Nuke("site:b", active: false));
        store.ObserveNuke(Nuke(active: false));
        Assert.Equal(["predb"], Assert.Single(store.Snapshot()).Sources);
        Assert.True(store.IsCandidateNuked("any-site", Original));
    }

    [Fact]
    public void Approximate_or_truncated_release_cannot_flag_a_different_exact_name()
    {
        var store = Store(); store.ObserveOwned(Owned());
        store.ObserveNuke(Nuke(release: Original[..^3]));
        store.ObserveNuke(Nuke(release: Original[..^5] + "..."));
        Assert.False(Assert.Single(store.Snapshot()).Flagged);
        store.ObserveNuke(Nuke(release: Original.ToLowerInvariant()));
        Assert.True(Assert.Single(store.Snapshot()).Flagged);
    }

    [Fact]
    public void Positive_candidate_nukes_apply_globally_for_predb_and_to_the_exact_site_for_sitebot()
    {
        var store = Store();
        store.ObserveNuke(Nuke("site:a", Proper));
        Assert.True(store.IsCandidateNuked("a", Proper));
        Assert.False(store.IsCandidateNuked("b", Proper));
        Assert.False(store.IsCandidateNuked("a", Proper[..^2]));
        store.ObserveNuke(Nuke("predb", Proper));
        Assert.True(store.IsCandidateNuked("b", Proper));
    }

    [Fact]
    public void Restart_and_reconciliation_keep_original_ownership_time()
    {
        var originalTime = _now;
        var store = Store(); store.ObserveOwned(Owned()); store.ObserveNuke(Nuke());
        _now = _now.AddDays(6);
        var restored = Store();
        restored.ObserveOwned(Owned(at: originalTime));
        Assert.Equal(originalTime, Assert.Single(restored.Snapshot()).Owned.OwnedAtUtc);
        _now = _now.AddDays(2);
        restored.ObserveOwned(Owned(at: originalTime));
        Assert.Empty(restored.Snapshot());
    }

    [Fact]
    public void Repeated_same_path_notification_does_not_renew_watch_age()
    {
        var originalTime = _now;
        var store = Store(); store.ObserveOwned(Owned());
        _now = _now.AddDays(6);
        store.ObserveOwned(Owned());
        Assert.Equal(originalTime, Assert.Single(store.Snapshot()).Owned.OwnedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_import_of_a_download_preserves_its_link_to_explicit_wishlist_choices(bool importFirst)
    {
        var store = Store();
        var download = Owned() with { WishlistItemId = "chosen-profile-item" };
        _now = _now.AddMinutes(1);
        var destination = Path.Combine(_root, "library", "movie.mkv");
        var imported = new OwnedReleaseObservation(Original, destination, _now);
        store.ObserveOwned(importFirst ? imported : download);
        store.ObserveOwned(importFirst ? download : imported);
        var owned = Assert.Single(store.Snapshot()).Owned;
        Assert.Equal(destination, owned.LocalPath);
        Assert.Equal("chosen-profile-item", owned.WishlistItemId);
    }

    [Fact]
    public void Correction_attempt_survives_restart_and_is_exact_per_candidate()
    {
        var store = Store(); store.ObserveOwned(Owned());
        store.RecordAttempt(new(Original, Proper, "download-id", _now));
        var restored = Store();
        Assert.True(restored.WasAttempted(Original.ToLowerInvariant(), Proper.ToLowerInvariant()));
        Assert.False(restored.WasAttempted(Original, "Example.Movie.2026.REPACK2.1080p.WEB.H264-GROUP"));
        Assert.Equal("download-id", Assert.Single(Assert.Single(restored.Snapshot()).Attempts).DownloadId);
    }

    [Fact]
    public void Failed_bookkeeping_keeps_owned_flags_and_attempts_in_memory()
    {
        var pathAsDirectory = Path.Combine(_root, "unwritable-file"); Directory.CreateDirectory(pathAsDirectory);
        var store = Store(pathAsDirectory);
        var payload = Path.Combine(_root, "user-payload"); File.WriteAllText(payload, "unchanged");
        store.ObserveOwned(new(Original, payload, _now));
        store.ObserveNuke(Nuke());
        store.RecordAttempt(new(Original, Proper, "download-id", _now));
        Assert.False(store.PersistenceHealthy);
        Assert.True(Assert.Single(store.Snapshot()).Flagged);
        Assert.True(store.WasAttempted(Original, Proper));
        Assert.Equal("unchanged", File.ReadAllText(payload));
    }

    [Fact]
    public void Corrupt_file_is_copied_aside_then_new_observations_continue()
    {
        File.WriteAllText(StatePath, "{corrupt");
        var store = Store();
        Assert.Empty(store.Snapshot());
        Assert.Equal("{corrupt", File.ReadAllText(StatePath + ".corrupt"));
        store.ObserveOwned(Owned()); store.ObserveNuke(Nuke());
        Assert.True(Assert.Single(store.Snapshot()).Flagged);
        Assert.True(store.PersistenceHealthy);
    }

    [Fact]
    public void At_capacity_oldest_entries_are_evicted_and_new_observations_still_work()
    {
        _config.HistoryCapacity = 2;
        var store = Store();
        for (var i = 0; i < 3; i++)
        {
            _now = _now.AddSeconds(1);
            var release = $"Example.Movie.{2024 + i}.1080p.WEB.H264-GROUP";
            store.ObserveOwned(Owned(release)); store.ObserveNuke(Nuke(release: release));
        }
        var entries = store.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.Owned.ReleaseName.Contains("2024", StringComparison.Ordinal));
        Assert.All(entries, e => Assert.True(e.Flagged));
        Assert.True(store.IsCandidateNuked("site", Original));
    }

    [Fact]
    public void Null_optional_reason_keeps_the_flag_but_does_not_claim_a_quality_defect()
    {
        var store = Store(); store.ObserveOwned(Owned());
        store.ObserveNuke(Nuke() with { Reason = null! });
        var entry = Assert.Single(store.Snapshot());
        Assert.True(entry.Flagged);
        Assert.False(entry.QualityNuke);
        store.ObserveNuke(Nuke() with { Source = null! });
        Assert.Single(store.Snapshot());
    }
}
