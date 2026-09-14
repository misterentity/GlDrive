using System.IO;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class NewReleaseMonitorRecoveryTests
{
    private static FtpListItem[] Dirs(params string[] names) => names.Select(name =>
        new FtpListItem { Name = name, Type = FtpObjectType.Directory }).ToArray();

    private static NewReleaseMonitor Monitor(
        Func<string, CancellationToken, Task<FtpListItem[]>> list,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(new NotificationConfig { Enabled = true, WatchPath = "/recent", PollIntervalSeconds = 10 },
            () => MountState.Connected, list, () => 0, "fixture",
            delay ?? ((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));

    [Fact]
    public async Task Internal_cancellation_retries_backs_off_and_resets_after_recovery()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var delays = new List<double>();
        var monitor = Monitor((_, _) =>
        {
            calls++;
            return calls <= 3
                ? Task.FromException<FtpListItem[]>(new OperationCanceledException("data TLS deadline"))
                : Task.FromResult(Dirs());
        }, (delay, ct) =>
        {
            delays.Add(delay.TotalSeconds);
            if (delays.Count == 5) stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await monitor.PollLoop(stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, calls);
        Assert.Equal(new double[] { 10, 10, 10, 20, 10 }, delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_category_keeps_snapshot_and_other_categories_continue(bool deadline)
    {
        var round = 0;
        var monitor = Monitor((path, _) =>
        {
            if (path == "/recent") return Task.FromResult(Dirs("TV", "Movies"));
            if (path == "/recent/TV" && round == 1)
                return Task.FromException<FtpListItem[]>(deadline
                    ? new OperationCanceledException("data deadline") : new IOException("connection reset"));
            return Task.FromResult(round == 0 ? Dirs("old") : Dirs("old", "new"));
        });
        var detected = new List<string>();
        monitor.NewReleaseDetected += (_, _, path) => detected.Add(path);
        await monitor.PollCycle(CancellationToken.None);
        round = 1;
        var error = await Assert.ThrowsAsync<IOException>(() => monitor.PollCycle(CancellationToken.None));
        Assert.Contains("1 category listing(s) failed; first /recent/TV", error.Message);
        Assert.NotNull(error.InnerException);
        Assert.Equal(new[] { "/recent/Movies/new" }, detected);
        round = 2;
        await monitor.PollCycle(CancellationToken.None);
        await monitor.PollCycle(CancellationToken.None);
        Assert.Equal(new[] { "/recent/Movies/new", "/recent/TV/new" }, detected);
    }

    [Fact]
    public async Task Shutdown_inside_category_does_not_continue_to_other_categories()
    {
        using var stop = new CancellationTokenSource();
        var paths = new List<string>();
        var monitor = Monitor((path, ct) =>
        {
            paths.Add(path);
            if (path == "/recent") return Task.FromResult(Dirs("TV", "Movies"));
            stop.Cancel();
            return Task.FromCanceled<FtpListItem[]>(ct);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.PollCycle(stop.Token));
        Assert.Equal(new[] { "/recent", "/recent/TV" }, paths);
    }

    [Fact]
    public async Task Partial_cycles_activate_poll_backoff()
    {
        using var stop = new CancellationTokenSource();
        var polls = new List<double>();
        var monitor = Monitor((path, _) => path == "/recent"
            ? Task.FromResult(Dirs("TV"))
            : Task.FromException<FtpListItem[]>(new IOException("LIST failed")), (delay, ct) =>
        {
            if (delay.TotalSeconds >= 10)
            {
                polls.Add(delay.TotalSeconds);
                if (polls.Count == 4) stop.Cancel();
            }
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await monitor.PollLoop(stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new double[] { 10, 10, 10, 20 }, polls);
    }

    [Fact]
    public async Task Failed_initial_listing_seeds_on_recovery_without_old_release_notifications()
    {
        var fail = true;
        var monitor = Monitor((path, _) => path == "/recent"
            ? Task.FromResult(Dirs("TV"))
            : fail ? Task.FromException<FtpListItem[]>(new IOException("LIST failed"))
            : Task.FromResult(Dirs("existing")));
        var notifications = 0;
        monitor.NewReleaseDetected += (_, _, _) => notifications++;
        await Assert.ThrowsAsync<IOException>(() => monitor.PollCycle(CancellationToken.None));
        fail = false;
        await monitor.PollCycle(CancellationToken.None);
        Assert.Equal(0, notifications);
    }
}
