using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadQueueAutomationTests
{
    [Fact]
    public async Task Legacy_rows_get_routable_owner_identity_without_mutating_persisted_fields()
    {
        using var h = new Harness();
        h.FtpItem.ServerId = h.FtpItem.ServerName = "";
        h.TorrentItem.ServerId = h.TorrentItem.ServerName = "";
        foreach (var queueId in new[] { "ftp-fixture", ServerManager.TorrentDownloadQueueId })
        {
            var listed = await h.Call("downloads.list", new { serverId = queueId });
            var row = Assert.Single(listed.GetProperty("items").EnumerateArray());
            Assert.Equal(queueId, row.GetProperty("serverId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("serverName").GetString()));
            var paused = await h.Call("downloads.pause", new { serverId = row.GetProperty("serverId").GetString(), id = row.GetProperty("id").GetString() });
            Assert.Equal(queueId, paused.GetProperty("serverId").GetString());
            Assert.Equal("Paused", paused.GetProperty("status").GetString());
        }
        Assert.Equal("", h.FtpItem.ServerId);
        Assert.Equal("", h.TorrentItem.ServerId);
    }

    [Fact]
    public async Task List_includes_both_queue_kinds_and_reserved_filter_without_exposing_persisted_magnet()
    {
        using var h = new Harness();
        var all = await h.Call("downloads.list", new { });
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var torrents = await h.Call("downloads.list", new { serverId = ServerManager.TorrentDownloadQueueId });
        var item = Assert.Single(torrents.GetProperty("items").EnumerateArray());
        Assert.Equal("Torrent", item.GetProperty("sourceKind").GetString());
        Assert.Equal(h.TorrentItem.RemotePath, item.GetProperty("remotePath").GetString());
        Assert.Equal("Fetching metadata", item.GetProperty("statusDetail").GetString());
        Assert.False(item.TryGetProperty("torrentMagnet", out _));
        Assert.DoesNotContain("private-token", torrents.GetRawText());
        Assert.DoesNotContain("magnet:", all.GetRawText());
        foreach (var field in new[] { "id", "remotePath", "releaseName", "localPath", "totalBytes", "downloadedBytes", "status", "queuedAt", "startedAt", "completedAt", "errorMessage", "wishlistItemId", "category", "serverId", "serverName", "retryCount", "sourceCompletionPending" })
            Assert.True(item.TryGetProperty(field, out _), "Missing legacy field: " + field);
        h.Torrents.Store.Save();
        Assert.Contains("private-token", File.ReadAllText(h.TorrentStorePath));
        Assert.Equal(h.Magnet, h.TorrentItem.TorrentMagnet);
        h.TorrentItem.ReleaseName = "changed after snapshot";
        Assert.Equal("torrent fixture", item.GetProperty("releaseName").GetString());
    }

    [Fact]
    public async Task Pause_resume_cancel_and_retry_route_to_torrent_owner_with_same_item_id_in_ftp_queue()
    {
        using var h = new Harness();
        var paused = await h.ItemCall("downloads.pause");
        Assert.Equal("Paused", paused.GetProperty("status").GetString());
        Assert.Equal(DownloadStatus.Queued, h.FtpItem.Status);
        var filtered = await h.Call("downloads.list", new { serverId = ServerManager.TorrentDownloadQueueId, status = "Paused" });
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        var resumed = await h.ItemCall("downloads.resume");
        Assert.Equal("Queued", resumed.GetProperty("status").GetString());
        var cancelled = await h.ItemCall("downloads.cancel");
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        var retried = await h.ItemCall("downloads.retry");
        Assert.Equal("Queued", retried.GetProperty("status").GetString());
        Assert.Equal(DownloadStatus.Queued, h.FtpItem.Status);
        foreach (var result in new[] { paused, resumed, cancelled, retried })
        {
            Assert.False(result.TryGetProperty("torrentMagnet", out _));
            Assert.DoesNotContain("private-token", result.GetRawText());
        }
    }

    [Fact]
    public async Task Blocked_retry_needs_explicit_consent_and_does_not_allow_path_escape_override()
    {
        using var h = new Harness();
        h.TorrentItem.Status = DownloadStatus.Failed;
        h.TorrentItem.RequiresContentApproval = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ItemCall("downloads.retry"));
        Assert.False(h.TorrentItem.SkipBlockedTorrentFiles);
        var retried = await h.Call("downloads.retry", new { serverId = ServerManager.TorrentDownloadQueueId, id = h.TorrentItem.Id, skipBlockedFiles = true });
        Assert.True(retried.GetProperty("skipBlockedTorrentFiles").GetBoolean());
        Assert.False(h.TorrentItem.RequiresContentApproval);
        Assert.Equal(DownloadStatus.Queued, h.TorrentItem.Status);
        Assert.DoesNotContain("private-token", retried.GetRawText());
        h.TorrentItem.Status = DownloadStatus.Failed;
        h.TorrentItem.RequiresContentApproval = false; // A non-overridable path refusal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Call("downloads.retry",
            new { serverId = ServerManager.TorrentDownloadQueueId, id = h.TorrentItem.Id, skipBlockedFiles = true }));
    }

    [Fact]
    public async Task Finished_removal_and_clear_are_scoped_to_queue_and_enqueue_remains_ftp_only()
    {
        using var h = new Harness();
        h.TorrentItem.Status = DownloadStatus.Completed;
        h.FtpItem.Status = DownloadStatus.Completed;
        await h.ItemCall("downloads.remove");
        Assert.Empty(h.Torrents.Store.Items);
        Assert.Single(h.Ftp.Store.Items);
        h.Torrents.Store.Add(h.TorrentItem);
        await h.Call("downloads.clear_finished", new { serverId = ServerManager.TorrentDownloadQueueId });
        Assert.Empty(h.Torrents.Store.Items);
        Assert.Single(h.Ftp.Store.Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Call("downloads.enqueue",
            new { serverId = ServerManager.TorrentDownloadQueueId, path = "/release" }));
    }

    [Fact]
    public async Task Invalid_pause_resume_states_are_rejected_and_ftp_cancel_still_routes_normally()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ItemCall("downloads.resume"));
        h.TorrentItem.Status = DownloadStatus.Extracting;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ItemCall("downloads.pause"));
        await h.Call("downloads.cancel", new { serverId = "ftp-fixture", id = h.FtpItem.Id });
        Assert.Equal(DownloadStatus.Cancelled, h.FtpItem.Status);
        Assert.Equal(DownloadStatus.Extracting, h.TorrentItem.Status);
    }

    [Fact]
    public void Catalog_exposes_pause_resume_and_explicit_skip_with_paused_filter()
    {
        using var h = new Harness();
        foreach (var name in new[] { "downloads.pause", "downloads.resume" })
            Assert.False(h.Service.Tools.Single(t => t.Name == name).ReadOnly);
        Assert.Contains("Paused", h.Service.Tools.Single(t => t.Name == "downloads.list").InputSchema.GetRawText());
        Assert.Contains("skipBlockedFiles", h.Service.Tools.Single(t => t.Name == "downloads.retry").InputSchema.GetRawText());
    }

    [Fact]
    public void Rate_refresh_callback_runs_only_after_successful_settings_persistence_and_reconciliation()
    {
        var config = new AppConfig();
        var calls = 0;
        var persisted = false;
        var module = new ConfigurationAutomation(config, _ => persisted = true, () => config,
            () => { Assert.True(persisted); Assert.Equal(25, config.Downloads.SpeedLimitKbps); calls++; });
        module.Apply(JsonNode.Parse("[{\"op\":\"replace\",\"path\":\"/downloads/speedLimitKbps\",\"value\":25}]")!.AsArray());
        Assert.Equal(1, calls);
        module.Apply(JsonNode.Parse("[{\"op\":\"replace\",\"path\":\"/downloads/speedLimitKbps\",\"value\":25}]")!.AsArray());
        Assert.Equal(1, calls);
        var failed = new ConfigurationAutomation(config, _ => throw new IOException("fixture save failure"), () => config, () => calls++);
        Assert.Throws<IOException>(() => failed.Apply(JsonNode.Parse("[{\"op\":\"replace\",\"path\":\"/downloads/speedLimitKbps\",\"value\":50}]")!.AsArray()));
        Assert.Equal(1, calls);
        Assert.Equal(25, config.Downloads.SpeedLimitKbps);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-queue-api-" + Guid.NewGuid().ToString("N"));
        public string TorrentStorePath => Path.Combine(_root, "torrents.json");
        public string Magnet => "magnet:?xt=urn:btih:" + new string('a', 40) + "&tr=https%3A%2F%2Ftracker.invalid%2Fprivate-token";
        public DownloadManager Ftp { get; }
        public DownloadManager Torrents { get; }
        public DownloadItem FtpItem { get; }
        public DownloadItem TorrentItem { get; }
        public AutomationService Service { get; }
        public Harness()
        {
            Directory.CreateDirectory(_root);
            var ftpConfig = new ServerConfig { Id = "ftp-fixture", Name = "FTP fixture" };
            var config = new AppConfig { Servers = [ftpConfig] };
            Ftp = Manager(Path.Combine(_root, "ftp.json"), config.Downloads);
            Torrents = Manager(TorrentStorePath, config.Downloads);
            FtpItem = new DownloadItem { Id = "shared-id", ServerId = ftpConfig.Id, ServerName = ftpConfig.Name,
                ReleaseName = "FTP fixture", RemotePath = "/fixture", LocalPath = Path.Combine(_root, "ftp-payload") };
            TorrentItem = new DownloadItem { Id = "shared-id", ServerId = ServerManager.TorrentDownloadQueueId, ServerName = "BitTorrent",
                SourceKind = DownloadSourceKind.Torrent, TorrentMagnet = Magnet, RemotePath = new string('a', 40),
                ReleaseName = "torrent fixture", StatusDetail = "Fetching metadata", LocalPath = Path.Combine(_root, "torrent-payload") };
            Ftp.Store.Add(FtpItem);
            Torrents.Store.Add(TorrentItem);
            // Initialize only the queue-owner fields: no ServerManager constructor/startup,
            // production stores, FTP connection, timer or torrent engine is touched.
            var mount = (MountService)RuntimeHelpers.GetUninitializedObject(typeof(MountService));
            Set(mount, "_serverConfig", ftpConfig);
            Set(mount, "_downloadManager", Ftp);
            var servers = (ServerManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerManager));
            Set(servers, "_servers", new ConcurrentDictionary<string, MountService>([new(ftpConfig.Id, mount)]));
            Set(servers, "_torrentDownloadLock", new object());
            Set(servers, "_torrentDownloads", Torrents);
            Service = new AutomationService(config, servers, new NotificationStore(persistToDisk: false));
        }
        private static DownloadManager Manager(string path, DownloadConfig config)
        {
            var store = new DownloadStore("isolated-queue-api");
            Set(store, "_filePath", path);
            return new DownloadManager(store, new UnusedSource(), config);
        }
        public async Task<JsonElement> Call(string operation, object args) => Assert.IsType<JsonElement>(
            await Service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args), CancellationToken.None));
        public Task<JsonElement> ItemCall(string operation) => Call(operation, new { serverId = ServerManager.TorrentDownloadQueueId, id = TorrentItem.Id });
        public void Dispose()
        {
            Ftp.Dispose(); Torrents.Dispose();
            Ftp.Store.Flush(); Torrents.Store.Flush();
            Directory.Delete(_root, true);
        }
        private static void Set(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        private sealed class UnusedSource : IWholeDownloadSource
        {
            public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
                throw new InvalidOperationException("Queue API fixture must never start a transfer.");
        }
    }
}
