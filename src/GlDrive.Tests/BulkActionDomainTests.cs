using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Plex;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class BulkActionDomainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-bulk-domain-" + Guid.NewGuid().ToString("N"));
    private readonly bool _readOnly = ConfigManager.ReadOnly;
    public BulkActionDomainTests() { Directory.CreateDirectory(_root); ConfigManager.ReadOnly = false; }
    public void Dispose() { ConfigManager.ReadOnly = _readOnly; Directory.Delete(_root, true); }

    [Fact]
    public void Wishlist_bulk_removal_targets_ids_once_and_retains_imported_identity_as_dead()
    {
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        var manual = new WishlistItem { Id = "manual", Title = "Shared Title" };
        var imported = new WishlistItem { Id = "imported", Title = "Imported", Source = PlexWatchlistSync.Source, PlexWatchlistId = "watchlist-fixture" };
        var retained = new WishlistItem { Id = "retained", Title = manual.Title };
        store.Add(manual); store.Add(imported); store.Add(retained);
        writes = 0;
        var notices = 0; store.Changed += () => notices++;
        Assert.Equal(2, store.RemoveMany([manual.Id, manual.Id, imported.Id, "missing"]));
        Assert.Null(store.GetById(manual.Id));
        Assert.Same(retained, store.GetById(retained.Id));
        Assert.Same(imported, store.GetById(imported.Id));
        Assert.True(imported.Dead);
        Assert.Equal("watchlist-fixture", imported.PlexWatchlistId);
        Assert.Equal(1, writes); Assert.Equal(1, notices);
    }

    [Fact]
    public void Wishlist_status_patch_preserves_custom_profile_floor_and_new_acquisition_evidence()
    {
        var store = new WishlistStore(() => null, _ => { });
        var item = new WishlistItem { Id = "selected", Title = "Fixture", Quality = QualityProfile.Q1080p,
            ProfileId = "custom-profile", AllowLowQualitySources = false };
        store.Add(item);
        var selectedIds = new[] { item.Id };
        var owned = new WishlistCopy { DownloadId = "completed", ReleaseName = "Fixture.2026.1080p.WEB-GROUP" };
        var pending = new WishlistCopy { DownloadId = "pending", ReleaseName = "Fixture.2026.2160p.WEB-GROUP" };
        item.BestOwned.Add(owned); item.PendingGrabs.Add(pending); item.GrabbedReleases.Add(pending.ReleaseName);
        Assert.Equal(1, store.UpdateMany(selectedIds, status: WishlistStatus.Paused));
        Assert.Same(item, store.GetById(item.Id));
        Assert.Equal(WishlistStatus.Paused, item.Status);
        Assert.Equal(QualityProfile.Q1080p, item.Quality);
        Assert.Equal("custom-profile", item.ProfileId);
        Assert.False(item.AllowLowQualitySources);
        Assert.Same(owned, Assert.Single(item.BestOwned));
        Assert.Same(pending, Assert.Single(item.PendingGrabs));
        Assert.Equal(pending.ReleaseName, Assert.Single(item.GrabbedReleases));
    }

    [Fact]
    public void Wishlist_quality_patch_updates_only_legacy_profile_and_saves_once()
    {
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        var legacy = new WishlistItem { Id = "legacy", Quality = QualityProfile.Q1080p };
        var custom = new WishlistItem { Id = "custom", Quality = QualityProfile.Q1080p, ProfileId = "custom-profile" };
        store.Add(legacy); store.Add(custom); writes = 0;
        Assert.Equal(2, store.UpdateMany([legacy.Id, legacy.Id, custom.Id], QualityProfile.Q2160p));
        Assert.Equal(ScoredQualityProfile.Legacy(QualityProfile.Q2160p).Id, legacy.ProfileId);
        Assert.Equal("custom-profile", custom.ProfileId);
        Assert.Equal(QualityProfile.Q2160p, custom.Quality);
        Assert.Equal(1, writes);
        Assert.Equal(0, store.UpdateMany([legacy.Id, custom.Id], QualityProfile.Q2160p));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void Wishlist_bulk_operations_keep_memory_changes_when_save_fails()
    {
        var store = new WishlistStore(() => null, _ => throw new IOException("fixture write failure"));
        var item = new WishlistItem { Id = "selected" }; store.Add(item);
        Assert.Equal(1, store.UpdateMany([item.Id], status: WishlistStatus.Paused));
        Assert.Equal(WishlistStatus.Paused, store.GetById(item.Id)!.Status);
        Assert.Equal(1, store.RemoveMany([item.Id]));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Notification_bulk_removal_preserves_unselected_and_later_arrivals()
    {
        var store = new NotificationStore(persistToDisk: false);
        var selected = new NotificationItem { Id = "selected", ReleaseName = "Shared.Release" };
        store.Add(selected);
        var selectedIds = new[] { selected.Id, selected.Id, "missing" };
        var later = new NotificationItem { Id = "later", ReleaseName = selected.ReleaseName, Timestamp = selected.Timestamp };
        store.Add(later);
        var notices = 0; store.Changed += _ => notices++;
        Assert.Equal(1, store.RemoveMany(selectedIds));
        Assert.Same(later, Assert.Single(store.Items));
        Assert.Equal(1, notices);
        Assert.Equal(0, store.RemoveMany(selectedIds));
        Assert.Equal(1, notices);
    }

    [Fact]
    public void Download_bulk_history_removal_rechecks_terminal_state_and_never_removes_payloads()
    {
        var store = DownloadStore.ForFile(Path.Combine(_root, "downloads.json"));
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        var payload = Path.Combine(_root, "payload.bin"); File.WriteAllText(payload, "keep payload");
        var terminal = new DownloadItem { Id = "completed", Status = DownloadStatus.Completed, LocalPath = payload };
        var retried = new DownloadItem { Id = "retried", Status = DownloadStatus.Failed };
        store.Add(terminal); store.Add(retried);
        var selectedIds = new[] { terminal.Id, retried.Id };
        retried.Status = DownloadStatus.Queued;
        Assert.Equal(1, manager.RemoveTerminalHistory(selectedIds));
        Assert.Null(store.GetById(terminal.Id));
        Assert.Same(retried, Assert.Single(store.Items));
        Assert.Equal("keep payload", File.ReadAllText(payload));
    }

    [Theory]
    [InlineData("cts")]
    [InlineData("active")]
    [InlineData("restart")]
    public void Download_terminal_row_with_outstanding_worker_or_restart_is_retained(string owner)
    {
        var store = DownloadStore.ForFile(Path.Combine(_root, "downloads.json"));
        using var manager = new DownloadManager(store, new UnusedSource(), new DownloadConfig());
        using var token = new CancellationTokenSource();
        var item = new DownloadItem { Id = "worker", Status = DownloadStatus.Cancelled }; store.Add(item);
        if (owner == "cts") Field<Dictionary<string, CancellationTokenSource>>(manager, "_activeCts")[item.Id] = token;
        if (owner == "active") Field<Dictionary<string, DownloadItem>>(manager, "_activeItems")[item.Id] = item;
        if (owner == "restart") Field<HashSet<string>>(manager, "_restartAfterExit").Add(item.Id);
        Assert.Equal(0, manager.RemoveTerminalHistory([item.Id]));
        Assert.Same(item, Assert.Single(store.Items));
        Field<Dictionary<string, CancellationTokenSource>>(manager, "_activeCts").Clear();
        Field<Dictionary<string, DownloadItem>>(manager, "_activeItems").Clear();
        Field<HashSet<string>>(manager, "_restartAfterExit").Clear();
        Assert.Equal(1, manager.RemoveTerminalHistory([item.Id]));
    }

    [Fact]
    public void Bulk_race_removal_uses_request_identity_and_keeps_replacement_and_other_requests()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        var original = RaceControlPolicyTests.Request();
        var replacement = original with { RequestId = "replacement" };
        var selected = original with { RequestId = "selected", ReleaseName = "Other.Release" };
        var queue = Field<List<PersistedRace>>(manager, "_raceQueue"); queue.Add(replacement); queue.Add(selected);
        var result = manager.RemoveQueuedRequests([original.RequestId, selected.RequestId, selected.RequestId]);
        Assert.Equal(new BulkRaceRemovalResult(1, 1), result);
        Assert.Equal(replacement.RequestId, Assert.Single(manager.QueuedRaces).RequestId);
        Assert.Equal(replacement.RequestId, Assert.Single(new RaceQueueStore(Path.Combine(_root, "race-queue.json")).Load()).RequestId);
    }

    [Theory]
    [InlineData("owned-partial")]
    [InlineData("cleanup")]
    [InlineData("control-busy")]
    [InlineData("starting")]
    [InlineData("active")]
    public void Bulk_race_removal_never_cancels_inflight_or_discards_cleanup_authority(string condition)
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        using var job = new SpreadJob("video", "Example.Release", SpreadMode.Race, new(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        var request = RaceControlPolicyTests.Request(condition == "owned-partial") with { CleanupOnly = condition == "cleanup" };
        var queue = Field<List<PersistedRace>>(manager, "_raceQueue"); queue.Add(request);
        if (condition == "control-busy") Field<HashSet<string>>(manager, "_controlRequestsInFlight").Add(request.RequestId);
        if (condition == "starting") Field<List<PersistedRace>>(manager, "_startingRequests").Add(request);
        if (condition == "active") Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests")[job] = request;
        var state = job.State;
        Assert.Equal(new BulkRaceRemovalResult(0, 1), manager.RemoveQueuedRequests([request.RequestId]));
        Assert.Same(request, Assert.Single(queue));
        Assert.Equal(state, job.State);
        Assert.Equal(condition == "cleanup", request.CleanupOnly);
        if (condition == "owned-partial") Assert.Single(request.Partial!.Directories);
        Field<HashSet<string>>(manager, "_controlRequestsInFlight").Clear();
        Field<List<PersistedRace>>(manager, "_startingRequests").Clear();
        Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests").Clear();
    }

    [Fact]
    public void Deferred_membership_edit_cannot_target_a_replacement_request()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        var original = RaceControlPolicyTests.Request();
        var replacement = original with { RequestId = "replacement" };
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(replacement);
        var rejected = manager.EditQueuedRaceSite(original.Section, original.ReleaseName, "third", false, original.RequestId);
        Assert.False(rejected.Accepted); Assert.Equal("request-changed", rejected.Code);
        Assert.Contains("third", Assert.Single(manager.QueuedRaces).ServerIds);
        Assert.True(manager.EditQueuedRaceSite(replacement.Section, replacement.ReleaseName, "third", false, replacement.RequestId).Accepted);
        Assert.DoesNotContain("third", Assert.Single(manager.QueuedRaces).ServerIds);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private sealed class UnusedSource : IWholeDownloadSource
    {
        public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Bulk history actions must never start a transfer.");
    }
}
