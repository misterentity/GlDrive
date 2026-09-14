using FluentFTP;
using System.IO;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Services;

public class NewReleaseMonitor
{
    private readonly Func<string, CancellationToken, Task<FtpListItem[]>> _listDirectory;
    private readonly Func<int> _activeCount;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _server;
    private readonly NotificationConfig _config;
    private readonly Func<MountState> _getState;
    private readonly Dictionary<string, HashSet<string>> _snapshot = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _seeded;
    private int _consecutiveErrors;

    public event Action<string, string, string>? NewReleaseDetected; // category, release, remotePath

    public NewReleaseMonitor(FtpConnectionPool pool, NotificationConfig config, Func<MountState> getState)
        : this(config, getState, new FtpOperations(pool).ListDirectory,
            () => pool.ActiveCount, pool.ControlHost)
    {
    }

    internal NewReleaseMonitor(NotificationConfig config, Func<MountState> getState,
        Func<string, CancellationToken, Task<FtpListItem[]>> listDirectory,
        Func<int> activeCount, string server,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _listDirectory = listDirectory;
        _activeCount = activeCount;
        _server = server;
        _delay = delay ?? Task.Delay;
        _config = config;
        _getState = getState;
    }

    public void Start()
    {
        if (!_config.Enabled) return;
        _cts = new CancellationTokenSource();
        _pollTask = PollLoop(_cts.Token);
        Log.Information("NewReleaseMonitor started — watching {Path} every {Interval}s",
            _config.WatchPath, _config.PollIntervalSeconds);
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _cts?.Cancel();
        if (_pollTask != null)
        {
            try
            {
                await _pollTask.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warning("NewReleaseMonitor stop timed out — abandoning background task");
            }
            catch { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    internal async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delay = _consecutiveErrors >= 3
                    ? Math.Min(_config.PollIntervalSeconds * 2, 300)
                    : _config.PollIntervalSeconds;
                await _delay(TimeSpan.FromSeconds(delay), ct);

                if (_getState() != MountState.Connected)
                    continue;

                await PollCycle(ct);
                _consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                if (_consecutiveErrors <= 3)
                    Log.Warning(ex, "NewReleaseMonitor[{Server}] poll error ({Count} consecutive)", _server, _consecutiveErrors);
                else
                    Log.Debug(ex, "NewReleaseMonitor[{Server}] poll error ({Count} consecutive, backing off)", _server, _consecutiveErrors);
            }
        }
    }

    internal async Task PollCycle(CancellationToken ct)
    {
        // FtpOperations owns borrow/disposal and quarantines unclean LIST failures,
        // while preserving connections after clean final rejections (425 / 550).
        var categories = await _listDirectory(_config.WatchPath, ct);

        var excluded = _config.ExcludedCategories;
        var categoryDirs = categories
            .Where(i => i.Type is FtpObjectType.Directory or FtpObjectType.Link)
            .Select(i => i.Name)
            .Where(name => !excluded.Any(ex => string.Equals(ex, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Exception? firstFailure = null;
        string? firstFailedPath = null;
        var failedCategories = 0;
        foreach (var category in categoryDirs)
        {
            ct.ThrowIfCancellationRequested();

            // Throttle without holding a pool slot — sleep, then borrow per category.
            if (_activeCount() > 0)
                await _delay(TimeSpan.FromSeconds(2), ct);
            else
                await _delay(TimeSpan.FromMilliseconds(200), ct);

            var categoryPath = _config.WatchPath.TrimEnd('/') + "/" + category;
            FtpListItem[] releases;
            try
            {
                releases = await _listDirectory(categoryPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                firstFailedPath ??= categoryPath;
                failedCategories++;
                continue;
            }

            var currentNames = releases
                .Where(i => i.Type is FtpObjectType.Directory or FtpObjectType.Link)
                .Select(i => i.Name)
                .ToHashSet();

            if (_snapshot.TryGetValue(category, out var previous))
            {
                if (_seeded)
                {
                    foreach (var name in currentNames)
                    {
                        if (!previous.Contains(name))
                        {
                            var remotePath = categoryPath + "/" + name;
                            Log.Information("New release: [{Category}] {Release}", category, name);
                            NewReleaseDetected?.Invoke(category, name, remotePath);
                        }
                    }
                }
            }

            _snapshot[category] = currentNames;
        }

        // Prune categories that no longer exist on the server
        var staleCategories = _snapshot.Keys.Except(categoryDirs).ToList();
        foreach (var stale in staleCategories)
            _snapshot.Remove(stale);

        if (!_seeded)
        {
            _seeded = true;
            Log.Information("NewReleaseMonitor seeded with {Count} categories", categoryDirs.Count);
        }

        // Keep successful categories current and retain failed categories' snapshots,
        // but let the loop report and back off an incomplete poll instead of resetting
        // its error counter. One summary per cycle avoids a warning per category.
        if (firstFailure != null)
            throw new IOException($"{failedCategories} category listing(s) failed; first {firstFailedPath}", firstFailure);
    }
}
