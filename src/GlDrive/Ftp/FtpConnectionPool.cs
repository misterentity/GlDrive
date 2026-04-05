using System.Threading.Channels;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

public class FtpConnectionPool : IAsyncDisposable
{
    private readonly FtpClientFactory _factory;
    private readonly Channel<AsyncFtpClient> _pool;
    private readonly int _maxSize;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private int _created;
    private int _active;
    private bool _disposed;

    public FtpConnectionPool(FtpClientFactory factory, int maxSize = 3)
    {
        _factory = factory;
        _maxSize = maxSize;
        _pool = Channel.CreateBounded<AsyncFtpClient>(maxSize);
    }

    public int ActiveCount => _active;
    public int TotalCreated => _created;
    public int MaxSize => _maxSize;
    public bool IsConnected { get; private set; }
    public bool UseCpsv { get; private set; }
    public string ControlHost { get; private set; } = "";

    public async Task Initialize(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_created > 0) return;

            // Create initial connection to verify connectivity
            var client = await _factory.CreateAndConnect(ct);

            // Detect CPSV support (glftpd BNC)
            ControlHost = _factory.Host;
            if (client.Capabilities.Contains(FtpCapability.CPSV))
            {
                UseCpsv = true;
                Log.Information("Server supports CPSV — using BNC-compatible data connections");
            }

            await _pool.Writer.WriteAsync(client, ct);
            _created = 1;
            IsConnected = true;
            Log.Information("Connection pool initialized with 1 connection (max {Max})", _maxSize);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<PooledConnection> Borrow(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FtpConnectionPool));

        // Try to get from pool first
        if (_pool.Reader.TryRead(out var client))
        {
            if (client.IsConnected)
            {
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }

            // Stale connection, properly disconnect and create new
            DisconnectAndDispose(client);
            Interlocked.Decrement(ref _created);
        }

        // Pool empty — create new if under limit
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                client = await _factory.CreateAndConnect(ct);
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _created);
                // Server refused new connection — fall through to wait for an existing one
                Log.Debug(ex, "Pool: new connection failed, waiting for existing one to be returned");
            }
        }
        else
        {
            Interlocked.Decrement(ref _created);
        }

        // At capacity or new connection failed — wait for one to be returned
        client = await _pool.Reader.ReadAsync(ct);
        if (client.IsConnected)
        {
            Interlocked.Increment(ref _active);
            return new PooledConnection(client, this);
        }

        // Stale, replace it
        DisconnectAndDispose(client);
        client = await _factory.CreateAndConnect(ct);
        Interlocked.Increment(ref _active);
        return new PooledConnection(client, this);
    }

    internal void Return(AsyncFtpClient client)
    {
        Interlocked.Decrement(ref _active);
        if (_disposed || !client.IsConnected)
        {
            DisconnectAndDispose(client);
            Interlocked.Decrement(ref _created);
            return;
        }

        if (!_pool.Writer.TryWrite(client))
        {
            DisconnectAndDispose(client);
            Interlocked.Decrement(ref _created);
        }
    }

    /// <summary>
    /// Discard a poisoned connection — dispose it without returning to the pool.
    /// Used when a cancellation or error may have left the GnuTLS stream corrupt.
    /// </summary>
    internal void Discard(AsyncFtpClient client)
    {
        Interlocked.Decrement(ref _active);
        Interlocked.Decrement(ref _created);
        DisconnectAndDispose(client);
        Log.Debug("Pool: discarded poisoned connection (created={Created})", _created);
    }

    /// <summary>
    /// Disposes an FTP client safely. GnuTLS can crash during disposal of poisoned
    /// streams — its Dispose throws a native exception inside FluentFTP's async
    /// pipeline that kills the process. To prevent this, wrap disposal in try-catch
    /// and close the underlying socket first to prevent GnuTLS from attempting
    /// read/close operations on dead connections.
    /// </summary>
    private static void DisconnectAndDispose(AsyncFtpClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.Disconnect(cts.Token).ConfigureAwait(false);
            }
            catch { }
            // Dispose can trigger GnuTLS native crash — wrap in try-catch
            try { client.Dispose(); }
            catch { }
        });
    }

    /// <summary>
    /// Safe disposal for DisposeAsync — wraps each client in try-catch to prevent
    /// GnuTLS native crash from killing the process.
    /// </summary>
    private static async Task SafeDisconnectAndDispose(AsyncFtpClient client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.Disconnect(cts.Token).ConfigureAwait(false);
        }
        catch { }
        try { client.Dispose(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Writer.Complete();
        IsConnected = false;

        while (_pool.Reader.TryRead(out var client))
            await SafeDisconnectAndDispose(client);

        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class PooledConnection : IAsyncDisposable
{
    private readonly FtpConnectionPool _pool;
    private AsyncFtpClient? _client;

    internal PooledConnection(AsyncFtpClient client, FtpConnectionPool pool)
    {
        _client = client;
        _pool = pool;
    }

    public AsyncFtpClient Client => _client ?? throw new ObjectDisposedException(nameof(PooledConnection));

    /// <summary>
    /// Mark this connection as poisoned so it will be discarded instead of returned
    /// to the pool. Call this after a cancellation or error that may have left the
    /// GnuTLS stream in a corrupt state.
    /// </summary>
    public bool Poisoned { get; set; }

    public ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            if (Poisoned)
                _pool.Discard(client);
            else
                _pool.Return(client);
        }
        return ValueTask.CompletedTask;
    }
}
