using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Services;

public class NewReleaseMonitor
{
    private readonly FtpConnectionPool _pool;
    private readonly NotificationConfig _config;
    private readonly Func<MountState> _getState;
    private readonly Dictionary<string, HashSet<string>> _snapshot = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _seeded;

    public event Action<string, string>? NewReleaseDetected;

    public NewReleaseMonitor(FtpConnectionPool pool, NotificationConfig config, Func<MountState> getState)
    {
        _pool = pool;
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

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);

                if (_getState() != MountState.Connected)
                    continue;

                await PollCycle(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "NewReleaseMonitor poll error");
            }
        }
    }

    private async Task PollCycle(CancellationToken ct)
    {
        await using var conn = await _pool.Borrow(ct);

        // Discover categories
        FtpListItem[] categories;
        if (_pool.UseCpsv)
            categories = await CpsvDataHelper.ListDirectory(conn.Client, _config.WatchPath, _pool.ControlHost, ct);
        else
            categories = await conn.Client.GetListing(_config.WatchPath, FtpListOption.AllFiles, ct);

        var excluded = _config.ExcludedCategories;
        var categoryDirs = categories
            .Where(i => i.Type == FtpObjectType.Directory)
            .Select(i => i.Name)
            .Where(name => !excluded.Any(ex => string.Equals(ex, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var category in categoryDirs)
        {
            ct.ThrowIfCancellationRequested();

            var categoryPath = _config.WatchPath.TrimEnd('/') + "/" + category;
            FtpListItem[] releases;
            try
            {
                if (_pool.UseCpsv)
                    releases = await CpsvDataHelper.ListDirectory(conn.Client, categoryPath, _pool.ControlHost, ct);
                else
                    releases = await conn.Client.GetListing(categoryPath, FtpListOption.AllFiles, ct);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to list {Category}, skipping", categoryPath);
                continue;
            }

            var currentNames = releases
                .Where(i => i.Type == FtpObjectType.Directory)
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
                            Log.Information("New release: [{Category}] {Release}", category, name);
                            NewReleaseDetected?.Invoke(category, name);
                        }
                    }
                }
            }

            _snapshot[category] = currentNames;
        }

        if (!_seeded)
        {
            _seeded = true;
            Log.Information("NewReleaseMonitor seeded with {Count} categories", categoryDirs.Count);
        }
    }
}
