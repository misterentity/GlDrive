using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using FluentFTP;
using FluentFTP.Client.BaseClient;
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
    private DateTime _lastGhostKill;

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
    /// <summary>True when all connections have been discarded and the pool can't serve requests.</summary>
    public bool IsExhausted => IsConnected && _created <= 0 && _active <= 0;
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

    /// <summary>
    /// Re-initialize a pool that has been fully exhausted (all connections poisoned/discarded).
    /// Creates a fresh connection and puts it back in the pool.
    /// </summary>
    public async Task Reinitialize(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (_created > 0 || _active > 0) return; // Pool still has connections

        await _initLock.WaitAsync(ct);
        try
        {
            if (_created > 0 || _active > 0) return; // Double-check under lock

            var client = await _factory.CreateAndConnect(ct);
            ControlHost = _factory.Host;
            if (client.Capabilities.Contains(FtpCapability.CPSV))
                UseCpsv = true;

            await _pool.Writer.WriteAsync(client, ct);
            _created = 1;
            IsConnected = true;
            Log.Information("Connection pool reinitialized with 1 connection (max {Max})", _maxSize);
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
                Log.Warning(ex, "Pool: new connection failed (created={Created}, max={Max})", _created, _maxSize);

                // Kill ghost connections and retry once (throttle to once per 30s)
                if ((DateTime.UtcNow - _lastGhostKill).TotalSeconds > 30)
                {
                    _lastGhostKill = DateTime.UtcNow;
                    try
                    {
                        await _factory.KillGhosts(ct);
                        // Retry connection after ghost kill
                        if (Interlocked.Increment(ref _created) <= _maxSize)
                        {
                            try
                            {
                                client = await _factory.CreateAndConnect(ct);
                                Interlocked.Increment(ref _active);
                                return new PooledConnection(client, this);
                            }
                            catch
                            {
                                Interlocked.Decrement(ref _created);
                            }
                        }
                        else
                        {
                            Interlocked.Decrement(ref _created);
                        }
                    }
                    catch (Exception ghostEx)
                    {
                        Log.Debug(ghostEx, "Pool: ghost kill failed");
                    }
                }
            }
        }
        else
        {
            Interlocked.Decrement(ref _created);
        }

        // If no connections exist at all (all discarded), don't wait — nothing will be returned
        if (_created <= 0)
            throw new InvalidOperationException("Pool exhausted: all connections discarded and new connections failed");

        // At capacity — wait for one to be returned
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
    /// Neutralize GnuTLS before disposal to prevent native crashes.
    /// GnuTLS calls gnutls_bye() during stream Dispose, which can crash the process
    /// with a native access violation if the underlying socket is dead or the TLS
    /// session is corrupt. This method:
    /// 1. Sets IsSessionUsable=false on GnuTlsInternalStream to skip gnutls_bye()
    /// 2. Closes the raw socket so any remaining native I/O fails cleanly
    /// </summary>
    private static void NeutralizeGnuTls(AsyncFtpClient client)
    {
        try
        {
            // Get the FtpSocketStream via the public IInternalFtpClient interface
            var stream = ((IInternalFtpClient)client).GetBaseStream();
            if (stream == null) return;

            // Get m_customStream (the GnuTLS wrapper) via reflection
            var customStreamField = stream.GetType().GetField("m_customStream",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var customStream = customStreamField?.GetValue(stream);
            if (customStream != null)
            {
                // The custom stream wraps GnuTlsInternalStream as its BaseStream.
                // Set IsSessionUsable = false to prevent gnutls_bye() in Dispose.
                var baseStreamProp = customStream.GetType().GetProperty("BaseStream");
                var gnuTlsInternal = baseStreamProp?.GetValue(customStream);
                if (gnuTlsInternal != null)
                {
                    var usableProp = gnuTlsInternal.GetType().GetProperty("IsSessionUsable");
                    if (usableProp?.CanWrite == true)
                    {
                        usableProp.SetValue(gnuTlsInternal, false);
                    }
                    else
                    {
                        // Fallback: use backing field if setter is private
                        var backingField = gnuTlsInternal.GetType().GetField("<IsSessionUsable>k__BackingField",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        backingField?.SetValue(gnuTlsInternal, false);
                    }
                }
            }

            // Also close the raw socket to prevent any stray native I/O
            var socketField = stream.GetType().GetField("m_socket",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var socket = socketField?.GetValue(stream) as Socket;
            if (socket != null)
            {
                try { socket.Close(0); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "NeutralizeGnuTls: failed (non-fatal)");
        }
    }

    /// <summary>
    /// Disposes an FTP client safely by first neutralizing GnuTLS to prevent
    /// native crashes, then disposing in a fire-and-forget task with timeout.
    /// </summary>
    private static void DisconnectAndDispose(AsyncFtpClient client)
    {
        NeutralizeGnuTls(client);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.Disconnect(cts.Token).ConfigureAwait(false);
            }
            catch { }
            try { client.Dispose(); }
            catch { }
        });
    }

    /// <summary>
    /// Safe disposal for DisposeAsync — neutralizes GnuTLS then disconnects.
    /// </summary>
    private static async Task SafeDisconnectAndDispose(AsyncFtpClient client)
    {
        NeutralizeGnuTls(client);
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
