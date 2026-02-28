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
    private bool _disposed;

    public FtpConnectionPool(FtpClientFactory factory, int maxSize = 3)
    {
        _factory = factory;
        _maxSize = maxSize;
        _pool = Channel.CreateBounded<AsyncFtpClient>(maxSize);
    }

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
                return new PooledConnection(client, this);

            // Stale connection, dispose and create new
            client.Dispose();
            Interlocked.Decrement(ref _created);
        }

        // Pool empty — create new if under limit
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                client = await _factory.CreateAndConnect(ct);
                return new PooledConnection(client, this);
            }
            catch
            {
                Interlocked.Decrement(ref _created);
                throw;
            }
        }

        Interlocked.Decrement(ref _created);

        // At capacity — wait for one to be returned
        client = await _pool.Reader.ReadAsync(ct);
        if (client.IsConnected)
            return new PooledConnection(client, this);

        // Stale, replace it
        client.Dispose();
        client = await _factory.CreateAndConnect(ct);
        return new PooledConnection(client, this);
    }

    internal void Return(AsyncFtpClient client)
    {
        if (_disposed || !client.IsConnected)
        {
            client.Dispose();
            Interlocked.Decrement(ref _created);
            return;
        }

        if (!_pool.Writer.TryWrite(client))
        {
            client.Dispose();
            Interlocked.Decrement(ref _created);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Writer.Complete();
        IsConnected = false;

        while (_pool.Reader.TryRead(out var client))
        {
            try { await client.Disconnect(); } catch { }
            client.Dispose();
        }

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

    public ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
            _pool.Return(client);
        return ValueTask.CompletedTask;
    }
}
