using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public sealed class DashboardLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Poll_is_single_flight_and_stop_waits_for_actual_cleanup()
    {
        var poll = new DashboardDiskPoll();
        var release = Signal();
        var finished = false;
        CancellationToken token = default;
        Assert.True(poll.TryStart(async ct => { token = ct; await release.Task; finished = true; }));
        Assert.False(poll.TryStart(_ => throw new InvalidOperationException("overlapping poll")));
        var stopped = poll.StopAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.False(stopped.IsCompleted);
        Assert.False(finished);
        Assert.Same(stopped, poll.StopAsync());
        Assert.False(poll.TryStart(_ => throw new InvalidOperationException("poll after close")));
        release.SetResult();
        await stopped.WaitAsync(Limit);
        Assert.True(finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_or_failed_poll_releases_single_flight_slot(bool fail)
    {
        var poll = new DashboardDiskPoll();
        Assert.True(poll.TryStart(_ => fail ? Task.FromException(new IOException("fixture")) : Task.CompletedTask));
        var runs = 0;
        Assert.True(poll.TryStart(_ => { runs++; return Task.CompletedTask; }));
        Assert.Equal(1, runs);
        await poll.StopAsync().WaitAsync(Limit);
    }

    [Fact]
    public void Late_poll_callback_cannot_restart_disposed_dashboard_or_subscribe_again()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            DashboardViewModel? vm = null;
            var release = Signal();
            try
            {
                // Bypass only constructors that read settings/credentials. Exercise actual
                // dashboard disposal, timer, publication and subscription methods.
                vm = EmptyDashboard();
                var poll = (DashboardDiskPoll)Field(vm, "_diskPoll").GetValue(vm)!;
                var subscribers = (Dictionary<string, DownloadManager>)Field(vm, "_subscribedDownloads").GetValue(vm)!;
                var manager = (DownloadManager)RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
                var callbacks = 0;
                vm.PropertyChanged += (_, _) => callbacks++;
                vm.StartOverviewLive();
                var initialTimer = (DispatcherTimer)Field(vm, "_overviewTimer").GetValue(vm)!;
                var dashboard = vm;
                var lateCallbackFinished = false;
                CancellationToken token = default;
                Assert.True(poll.TryStart(async ct =>
                {
                    token = ct;
                    await release.Task;
                    dashboard.RefreshOverview();
                    dashboard.StartOverviewLive();
                    Invoke(dashboard, "SubscribeToDownloads", "fixture", manager);
                    Invoke(dashboard, "TickOverview");
                    lateCallbackFinished = true;
                }));
                vm.Dispose();
                vm.Dispose();
                Assert.True(token.IsCancellationRequested);
                Assert.False(vm.DiskPollShutdown.IsCompleted);
                Assert.False(initialTimer.IsEnabled);
                release.SetResult();
                vm.DiskPollShutdown.WaitAsync(Limit).GetAwaiter().GetResult();
                Assert.True(lateCallbackFinished);
                Assert.Null(Field(vm, "_overviewTimer").GetValue(vm));
                Assert.Empty(subscribers);
                Assert.Equal(0, callbacks);
                Assert.Null(Field(manager, "DownloadProgressChanged").GetValue(manager));
                Assert.Null(Field(manager, "DownloadStatusChanged").GetValue(manager));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                release.TrySetResult();
                vm?.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Dashboard lifetime fixture did not drain");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static DashboardViewModel EmptyDashboard()
    {
        var vm = Raw<DashboardViewModel>();
        Set(vm, "_diskPoll", new DashboardDiskPoll());
        Set(vm, "_serverManager", Raw<ServerManager>());
        Set(vm, "_searchRequests", new SearchRequestCoordinator());
        Set(vm, "_preDbClient", new PreDbClient());
        Set(vm, "_subscribedDownloads", new Dictionary<string, DownloadManager>());
        var irc = Raw<IrcViewModel>();
        Set(irc, "_disposed", true);
        Set(vm, "_ircViewModel", irc);
        var plex = Raw<PlexViewModel>();
        var service = Raw<GlDrive.Plex.PlexService>();
        var client = Raw<GlDrive.Plex.PlexClient>();
        Set(client, "_http", new HttpClient());
        Set(service, "_client", client);
        Set(plex, "_service", service);
        Set(vm, "_plexViewModel", plex);
        return vm;
    }

    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
