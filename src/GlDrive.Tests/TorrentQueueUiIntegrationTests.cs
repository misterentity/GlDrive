using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentQueueUiIntegrationTests
{
    [Fact]
    public async Task Player_download_is_persisted_in_main_queue_and_owns_a_child_directory()
    {
        await using var h = new Harness();
        using var player = new PlayerViewModel(h.Servers, h.Config);
        var item = player.QueueTorrentDownload(h.Magnet, "../CON:unsafe/title", h.Root);
        Assert.Same(item, Assert.Single(h.Torrents.Store.Items));
        Assert.Equal(ServerManager.TorrentDownloadQueueId, item.ServerId);
        Assert.Equal(DownloadSourceKind.Torrent, item.SourceKind);
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(h.Root, Path.GetDirectoryName(item.LocalPath));
        Assert.DoesNotContain("..", Path.GetFileName(item.LocalPath));
        Assert.DoesNotContain("private-token", item.RemotePath);
        Assert.Equal(h.Magnet, item.TorrentMagnet);
        Assert.False(Directory.Exists(item.LocalPath));
        using var persisted = JsonDocument.Parse(File.ReadAllText(h.TorrentStorePath));
        Assert.Equal(item.Id, persisted.RootElement[0].GetProperty("id").GetString());
        Assert.Equal((int)DownloadSourceKind.Torrent, persisted.RootElement[0].GetProperty("sourceKind").GetInt32());
        Assert.Equal(item.LocalPath, persisted.RootElement[0].GetProperty("localPath").GetString());
        // A repeated click finds the existing durable row, including after Player disposal.
        player.Dispose();
        using var replacementPlayer = new PlayerViewModel(h.Servers, h.Config);
        Assert.Same(item, replacementPlayer.QueueTorrentDownload(h.Magnet, "renamed", h.Root));
    }

    [Fact]
    public async Task Closing_player_leaves_active_queue_running_until_its_backend_finishes()
    {
        await using var h = new Harness();
        var player = new PlayerViewModel(h.Servers, h.Config);
        var item = player.QueueTorrentDownload(h.Magnet, "fixture", h.Root);
        var completed = Signal();
        h.Torrents.DownloadStatusChanged += changed =>
        {
            if (changed.Status == DownloadStatus.Completed) completed.TrySetResult();
        };
        h.Torrents.Start();
        await h.Source.Entered.Task.WaitAsync(Limit);
        player.Dispose();
        Assert.False(h.Source.Token.IsCancellationRequested);
        Assert.Equal(DownloadStatus.Downloading, item.Status);
        Assert.False(h.Source.Disposed);
        h.Source.Finish.TrySetResult();
        await completed.Task.WaitAsync(Limit);
        await h.Torrents.StopAsync();
        Assert.True(h.Source.Disposed);
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public async Task Player_automation_queues_without_initializing_a_playback_engine()
    {
        await using var h = new Harness();
        using var player = new PlayerViewModel(h.Servers, h.Config);
        var response = JsonSerializer.SerializeToElement(await player.AutomationDownloadTorrent(h.Magnet, h.Root, false));
        var item = Assert.Single(h.Torrents.Store.Items);
        Assert.Equal(item.Id, response.GetProperty("id").GetString());
        Assert.Equal(ServerManager.TorrentDownloadQueueId, response.GetProperty("queueId").GetString());
        Assert.True(response.GetProperty("queued").GetBoolean());
        Assert.False(response.GetProperty("started").GetBoolean());
        Assert.Equal("Queued", response.GetProperty("status").GetString());
        Assert.Equal(item.LocalPath, response.GetProperty("path").GetString());
        Assert.Null(typeof(PlayerViewModel).GetField("_torrentStream", Hidden)!.GetValue(player));
        Assert.Null(typeof(PlayerViewModel).GetField("_mediaPlayer", Hidden)!.GetValue(player));
        Assert.Equal(0, h.Source.Preparations);
    }

    [Theory]
    [InlineData("PauseDownload", DownloadStatus.Queued, DownloadStatus.Paused)]
    [InlineData("ResumeDownload", DownloadStatus.Paused, DownloadStatus.Queued)]
    [InlineData("CancelDownload", DownloadStatus.Paused, DownloadStatus.Cancelled)]
    [InlineData("RetryDownload", DownloadStatus.Failed, DownloadStatus.Queued)]
    [InlineData("RetryDownload", DownloadStatus.Cancelled, DownloadStatus.Queued)]
    public async Task Dashboard_action_targets_selected_queue_even_when_ids_match(string action, DownloadStatus before, DownloadStatus after)
    {
        await using var h = new Harness();
        var ftp = h.AddFtp();
        var torrent = h.AddTorrent(before);
        var vm = h.Dashboard();
        vm.RefreshDownloadQueue();
        Assert.Equal(2, vm.DownloadItems.Count);
        vm.SelectedDownloadItem = vm.DownloadItems.Single(i => i.ServerId == ServerManager.TorrentDownloadQueueId);
        Invoke(vm, action);
        vm.RefreshDownloadQueue();
        Assert.Equal(after, torrent.Status);
        Assert.Equal(DownloadStatus.Queued, ftp.Status);
        Assert.Equal(ServerManager.TorrentDownloadQueueId, vm.SelectedDownloadItem?.ServerId);
        Assert.Equal(after.ToString(), vm.SelectedDownloadItem?.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_blocked_retry_requires_an_explicit_confirmation(bool approve)
    {
        await using var h = new Harness();
        var item = h.AddTorrent(DownloadStatus.Failed);
        item.RequiresContentApproval = true;
        item.ErrorMessage = "blocked fixture";
        var vm = h.Dashboard();
        vm.RefreshDownloadQueue();
        vm.SelectedDownloadItem = Assert.Single(vm.DownloadItems);
        Assert.Equal("Review…", vm.SelectedDownloadItem.RetryLabel);
        var prompts = 0;
        vm.ConfirmBlockedTorrentFiles = candidate =>
        {
            Assert.Same(item, candidate);
            prompts++;
            return approve;
        };
        Invoke(vm, "RetryDownload");
        Assert.Equal(1, prompts);
        Assert.Equal(approve, item.SkipBlockedTorrentFiles);
        Assert.Equal(!approve, item.RequiresContentApproval);
        Assert.Equal(approve ? DownloadStatus.Queued : DownloadStatus.Cancelled, item.Status);
    }

    [Fact]
    public async Task Containment_refusal_cannot_be_overridden_by_the_content_approval_action()
    {
        await using var h = new Harness();
        var item = h.AddTorrent(DownloadStatus.Failed);
        item.RequiresContentApproval = false;
        item.ErrorMessage = "path escapes destination";
        Assert.False(h.Servers.SkipBlockedTorrentFilesAndRetry(item.Id));
        Assert.False(item.SkipBlockedTorrentFiles);
        Assert.Equal(DownloadStatus.Failed, item.Status);
    }

    [Fact]
    public async Task Dashboard_projects_metadata_then_payload_and_paused_progress()
    {
        await using var h = new Harness();
        var item = h.AddTorrent(DownloadStatus.Downloading);
        item.StatusDetail = "Fetching torrent metadata";
        var vm = h.Dashboard();
        vm.RefreshDownloadQueue();
        var row = Assert.Single(vm.DownloadItems);
        Assert.True(row.IsIndeterminate);
        Assert.Equal(item.StatusDetail, row.ProgressText);
        Assert.True(row.CanPause);
        Assert.False(row.CanResume);
        item.TotalBytes = 100;
        item.DownloadedBytes = 25;
        row.ApplyProgress(item, new DownloadProgress(25, 100, 1024, StatusDetail: "Downloading · 2 seeds / 3 peers"));
        Assert.False(row.IsIndeterminate);
        Assert.Equal(25, row.ProgressPercent);
        Assert.Contains("2 seeds / 3 peers", row.ProgressText);
        item.Status = DownloadStatus.Paused;
        vm.RefreshDownloadQueue();
        row = Assert.Single(vm.DownloadItems);
        Assert.False(row.IsIndeterminate);
        Assert.True(row.CanResume);
        Assert.True(row.CanCancel);
        Assert.Equal("Paused at 25%", row.ProgressText);
        Assert.Equal(25, row.ProgressPercent);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, null);

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gldrive-queue-ui-" + Guid.NewGuid().ToString("N"));
        public string TorrentStorePath => Path.Combine(Root, "torrent-queue.json");
        public string Magnet => "magnet:?xt=urn:btih:" + new string('a', 40) + "&tr=https%3A%2F%2Ftracker.invalid%2Fprivate-token";
        public AppConfig Config { get; } = new();
        public DownloadManager Torrents { get; }
        public DownloadManager Ftp { get; }
        public ServerManager Servers { get; }
        public FakeSource Source { get; } = new();
        private readonly List<DashboardViewModel> _dashboards = [];

        public Harness()
        {
            Directory.CreateDirectory(Root);
            Config.Downloads.AutoExtract = false;
            Config.Downloads.MaxConcurrentDownloads = 1;
            Config.Downloads.MaxRetries = 0;
            Torrents = CreateManager(TorrentStorePath);
            Ftp = CreateManager(Path.Combine(Root, "ftp-queue.json"));
            var server = new ServerConfig { Id = "ftp-fixture", Name = "FTP fixture" };
            Config.Servers = [server];
            var mount = (MountService)RuntimeHelpers.GetUninitializedObject(typeof(MountService));
            Set(mount, "_serverConfig", server);
            Set(mount, "_downloadManager", Ftp);
            Servers = (ServerManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerManager));
            Set(Servers, "_config", Config);
            Set(Servers, "_servers", new ConcurrentDictionary<string, MountService>([new(server.Id, mount)]));
            Set(Servers, "_torrentDownloadLock", new object());
            Set(Servers, "_torrentDownloads", Torrents);
        }

        private DownloadManager CreateManager(string path)
        {
            var store = DownloadStore.ForFile(path);
            return new DownloadManager(store, Source, Config.Downloads);
        }

        public DownloadItem AddFtp()
        {
            var item = new DownloadItem { Id = "same-id", ServerId = "ftp-fixture", SourceKind = DownloadSourceKind.Ftp,
                RemotePath = "/fixture", LocalPath = Path.Combine(Root, "ftp-payload") };
            Ftp.Store.Add(item);
            return item;
        }

        public DownloadItem AddTorrent(DownloadStatus status)
        {
            var item = new DownloadItem { Id = "same-id", ServerId = ServerManager.TorrentDownloadQueueId,
                SourceKind = DownloadSourceKind.Torrent, Status = status, RemotePath = new string('a', 40),
                TorrentMagnet = Magnet, LocalPath = Path.Combine(Root, "torrent-payload") };
            Torrents.Store.Add(item);
            return item;
        }

        public DashboardViewModel Dashboard()
        {
            // Exercise real queue projection/action methods without the constructor's
            // production wishlist reads, UI timers, IRC or Plex initialization.
            var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
            Set(vm, "_serverManager", Servers);
            Set(vm, "_config", Config);
            Set(vm, "<DownloadItems>k__BackingField", new ObservableCollection<DownloadItemVm>());
            foreach (var name in new[] { "_subscribedDownloads", "_downloadItemById", "_streamMatrices", "_liveItemSpeeds", "_activeDownloads", "_serverSpeeds" })
            {
                var field = typeof(DashboardViewModel).GetField(name, Hidden)!;
                field.SetValue(vm, Activator.CreateInstance(field.FieldType));
            }
            _dashboards.Add(vm);
            return vm;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var vm in _dashboards) Invoke(vm, "UnsubscribeAllServers");
            Source.Finish.TrySetResult();
            await Torrents.StopAsync();
            await Ftp.StopAsync();
            Torrents.Dispose();
            Ftp.Dispose();
            Torrents.Store.Flush();
            Ftp.Store.Flush();
            Directory.Delete(Root, true);
        }
    }

    private sealed class FakeSource : IWholeDownloadSource, IPreparedDownload
    {
        public int Preparations;
        public bool Disposed;
        public CancellationToken Token;
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Finish { get; } = Signal();
        public long TotalBytes => 100;
        public IReadOnlyList<string> Directories => [];
        public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct)
        { Preparations++; return Task.FromResult<IPreparedDownload>(this); }
        public async Task ExecuteAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
        { Token = ct; Entered.TrySetResult(); await Finish.Task.WaitAsync(ct); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
