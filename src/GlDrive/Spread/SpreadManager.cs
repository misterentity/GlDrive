using System.IO;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public class SpreadManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, FtpConnectionPool> _spreadPools = new();
    private readonly Dictionary<string, FtpClientFactory> _factories = new();
    private readonly List<SpreadJob> _activeJobs = new();
    private readonly SpeedTracker _speedTracker = new();
    private readonly SkiplistEvaluator _skiplist = new();
    private readonly Lock _lock = new();
    private bool _disposed;

    public IReadOnlyList<SpreadJob> ActiveJobs
    {
        get { lock (_lock) return _activeJobs.ToList(); }
    }

    public event Action<SpreadJob>? JobStarted;
    public event Action<SpreadJob>? JobCompleted;
    public event Action<SpreadJob>? JobProgressChanged;

    public SpreadManager(AppConfig config)
    {
        _config = config;
    }

    public async Task InitializePool(string serverId, FtpClientFactory factory, CancellationToken ct)
    {
        if (_disposed) return;

        var poolSize = _config.Spread.SpreadPoolSize;
        if (poolSize <= 0) return;

        var pool = new FtpConnectionPool(factory, poolSize);
        try
        {
            await pool.Initialize(ct);
            lock (_lock)
            {
                _spreadPools[serverId] = pool;
                _factories[serverId] = factory;
            }
            Log.Information("Spread pool initialized for {ServerId} (size={Size})", serverId, poolSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize spread pool for {ServerId}", serverId);
            await pool.DisposeAsync();
        }
    }

    public async Task DisposePool(string serverId)
    {
        FtpConnectionPool? pool;
        lock (_lock)
        {
            _spreadPools.Remove(serverId, out pool);
            _factories.Remove(serverId);
        }

        if (pool != null)
            await pool.DisposeAsync();
    }

    public SpreadJob StartRace(string section, string releaseName,
        IReadOnlyList<string> serverIds, SpreadMode mode)
    {
        Dictionary<string, FtpConnectionPool> pools;
        Dictionary<string, ServerConfig> configs;

        lock (_lock)
        {
            pools = serverIds
                .Where(id => _spreadPools.ContainsKey(id))
                .ToDictionary(id => id, id => _spreadPools[id]);

            configs = serverIds
                .Where(id => _config.Servers.Any(s => s.Id == id))
                .ToDictionary(id => id, id => _config.Servers.First(s => s.Id == id));
        }

        if (pools.Count < 2)
            throw new InvalidOperationException("Need at least 2 connected servers to start a race");

        var job = new SpreadJob(section, releaseName, mode, _config.Spread,
            pools, configs, _speedTracker, _skiplist);

        job.ProgressChanged += j => JobProgressChanged?.Invoke(j);
        job.Completed += j =>
        {
            lock (_lock) _activeJobs.Remove(j);
            JobCompleted?.Invoke(j);
        };
        job.Error += (j, msg) =>
        {
            lock (_lock) _activeJobs.Remove(j);
            Log.Warning("Spread job error: {Release} — {Error}", j.ReleaseName, msg);
        };

        lock (_lock) _activeJobs.Add(job);
        JobStarted?.Invoke(job);

        _ = Task.Run(job.RunAsync);
        return job;
    }

    public void StopJob(string jobId)
    {
        SpreadJob? job;
        lock (_lock)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.Stop();
    }

    public async Task StartFxp(string srcServerId, string srcPath,
        string dstServerId, string dstPath, CancellationToken ct)
    {
        FtpConnectionPool srcPool, dstPool;
        lock (_lock)
        {
            if (!_spreadPools.TryGetValue(srcServerId, out srcPool!))
                throw new InvalidOperationException($"No spread pool for source server {srcServerId}");
            if (!_spreadPools.TryGetValue(dstServerId, out dstPool!))
                throw new InvalidOperationException($"No spread pool for dest server {dstServerId}");
        }

        var mode = FxpModeDetector.Detect(srcPool, dstPool);
        await using var srcConn = await srcPool.Borrow(ct);
        await using var dstConn = await dstPool.Borrow(ct);

        var transfer = new FxpTransfer();
        var ok = await transfer.ExecuteAsync(srcConn, dstConn, srcPath, dstPath, mode,
            _config.Spread.TransferTimeoutSeconds, ct);

        if (!ok)
            throw new IOException($"FXP transfer failed: {transfer.ErrorMessage}");
    }

    public IReadOnlyList<string> GetConnectedServerIds()
    {
        lock (_lock) return _spreadPools.Keys.ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var job in _activeJobs)
                job.Stop();
            _activeJobs.Clear();
        }

        // Dispose pools fire-and-forget
        var pools = _spreadPools.Values.ToList();
        _spreadPools.Clear();
        _ = Task.Run(async () =>
        {
            foreach (var pool in pools)
            {
                try { await pool.DisposeAsync(); } catch { }
            }
        });

        GC.SuppressFinalize(this);
    }
}
