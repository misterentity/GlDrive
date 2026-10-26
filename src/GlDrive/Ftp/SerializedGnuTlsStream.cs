using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.GnuTLS;
using FluentFTP.Streams;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>
/// The definitive fix for the recurring native GnuTLS crash (Event 1026, stack
/// <c>FluentFTP.GnuTLS.GnuTlsInternalStream.Read</c> on a thread-pool thread).
///
/// ROOT CAUSE (decompiled FluentFTP 53 + FluentFTP.GnuTLS 1.0.x): the real TLS stream
/// <c>GnuTlsInternalStream</c> overrides ONLY the synchronous <c>Read</c> (a blocking native
/// <c>gnutls_record_recv(sess, …)</c>), NOT <c>ReadAsync</c>. So <c>Stream</c>'s default
/// <c>ReadAsync</c> dispatches that blocking recv to the thread pool as an untracked work item.
/// When teardown then runs <c>Dispose</c> — which does <c>sess.Dispose()</c>, freeing the GnuTLS
/// session — while that recv is still in native code, the recv touches freed session state →
/// AccessViolationException, a Corrupted State Exception .NET 10 does NOT route to managed
/// handlers (uncatchable, no crashdump). FluentFTP's OWN 30s ReadTimeout disposes the stream on
/// this path, so the connection pool's reflective neutralize can't reach it.
///
/// FIX: wrap the custom stream so every read AND the dispose take the SAME per-connection
/// <see cref="SemaphoreSlim"/>. A recv and <c>sess.Dispose()</c> are now mutually exclusive:
/// Dispose waits up to 20 seconds for in-flight I/O, then retains one asynchronous cleanup waiter.
/// New I/O is rejected as soon as disposal starts. Cleanup frees the session only after obtaining
/// the same lock; if native I/O never returns, its session remains retained rather than being freed
/// under a live read. A slow read that eventually drains no longer leaks its session permanently.
///
/// This type is a drop-in <see cref="IFtpStream"/> for <c>Config.CustomStream</c> that composes
/// the package's <see cref="GnuTlsStream"/> (so TLS handshake / session-resumption behaviour is
/// unchanged) and only interposes the serializing wrapper on the I/O + dispose path. It also
/// exposes a <c>BaseStream</c> field so the connection pool's existing crash-guard reflection
/// (<c>m_customStream.BaseStream</c> → IsSessionUsable/sess) keeps working unchanged.
/// </summary>
public sealed class SerializedGnuTlsStream : IFtpStream
{
    // Serializes native recv/send against gnutls_deinit. Shared with the read wrapper.
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    // The real package stream — does the TLS handshake and owns the GnuTlsInternalStream.
    private readonly IFtpStream _inner;

    // The serializing view FluentFTP reads/writes through.
    private SerializingStream? _wrapper;

    /// <summary>
    /// The underlying <c>GnuTlsInternalStream</c>. Named/typed so the connection pool's crash-guard
    /// reflection (which reads <c>m_customStream.BaseStream</c> and inspects IsSessionUsable/sess)
    /// resolves it unchanged. Do NOT rename without updating GnuTlsReflectionGuard + the pool.
    /// </summary>
    internal Stream? BaseStream;

    /// <summary>
    /// Maximum synchronous wait for in-flight I/O. A timeout transfers cleanup to an asynchronous
    /// waiter; it never authorizes freeing a session while native I/O still holds the lock.
    /// </summary>
    private readonly TimeSpan _disposeDrainTimeout;

    public SerializedGnuTlsStream() : this(new GnuTlsStream(), TimeSpan.FromSeconds(20)) { }

    // Instance-only seam for deterministic native-lifetime regression tests.
    internal SerializedGnuTlsStream(IFtpStream inner, TimeSpan disposeDrainTimeout)
    {
        _inner = inner;
        _disposeDrainTimeout = disposeDrainTimeout;
    }

    // Atomic ownership prevents duplicate cleanup, including the native library's global refcount.
    // Requesting disposal also closes I/O admission before the native session can actually be freed.
    private int _disposeRequested;
    private bool DisposeRequested => Volatile.Read(ref _disposeRequested) != 0;

    public void Init(BaseFtpClient client, string targetHost, Socket socket,
        CustomRemoteCertificateValidationCallback customRemoteCertificateValidation,
        bool isControl, IFtpStream controlConnStream, IFtpStreamConfig config)
    {
        // For a DATA connection FluentFTP passes the CONTROL connection's custom stream so GnuTLS can
        // resume the control session (REQUIRED by glftpd). GnuTlsStream.Init casts it to GnuTlsStream,
        // so unwrap our composed inner stream, else session resumption silently breaks all data I/O.
        var innerControl = (controlConnStream as SerializedGnuTlsStream)?._inner ?? controlConnStream;
        _inner.Init(client, targetHost, socket, customRemoteCertificateValidation, isControl, innerControl, config);

        BaseStream = _inner.GetBaseStream();
        _wrapper = new SerializingStream(this, BaseStream, _ioLock);
    }

    public bool Validate() => _inner is not GnuTlsStream native || native.Validate();

    public Stream GetBaseStream() => _wrapper ?? _inner.GetBaseStream();

    public bool CanRead() => !DisposeRequested && _inner.CanRead();

    public bool CanWrite() => !DisposeRequested && _inner.CanWrite();

    public SslProtocols GetSslProtocol() => _inner.GetSslProtocol();

    public string GetCipherSuite() => _inner.GetCipherSuite();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;

        // Serialize the session free (sess.Dispose inside _inner.Dispose) against any in-flight recv.
        // Acquire the SAME lock the read wrapper holds during a native recv — this is the whole fix.
        if (_ioLock.Wait(_disposeDrainTimeout))
        {
            try { _inner.Dispose(); }
            finally { _ioLock.Release(); }
            return;
        }

        Log.Warning("GnuTLS: native I/O did not drain within {Timeout}s — deferring session cleanup until I/O completes",
            _disposeDrainTimeout.TotalSeconds);
        // Keep the owner/session reachable without blocking a worker thread. Task.Run also keeps
        // cleanup off the Dispose caller if the lock becomes available just after the timeout.
        _ = Task.Run(DisposeWhenDrainedAsync);
    }

    private async Task DisposeWhenDrainedAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // FluentFTP may already have closed the socket after our synchronous Dispose returned.
            // Skip gnutls_bye and pending-record reads on that stale transport. Mutation and native
            // deallocation both remain under the I/O lock, after the active operation has returned.
            if (BaseStream != null) FtpConnectionPool.MarkGnuTlsSessionUnusable(BaseStream);
            _inner.Dispose();
            Log.Information("GnuTLS: deferred session cleanup completed after native I/O drained");
        }
        catch (Exception ex) { Log.Warning(ex, "GnuTLS: deferred session cleanup failed"); }
        finally { _ioLock.Release(); }
    }

    private static readonly Type? GnuTlsErrorEnum =
        typeof(GnuTlsStream).Assembly.GetType("FluentFTP.GnuTLS.Core.EC+errNo");

    // The library's own error-code names (diagnostic only; falls back if a bump renames them).
    internal static string GnuTlsErrorName(int code)
    {
        if (GnuTlsErrorEnum == null) return "GnuTLS error";
        return Enum.GetName(GnuTlsErrorEnum, Enum.ToObject(GnuTlsErrorEnum, code)) ?? "GnuTLS error";
    }

    // Timed-out native reads may finish after their caller has gone away. Keep their
    // existing background-fault classification when the stream adds an IOException wrapper.
    internal static bool IsGnuTlsIoFailure(Exception exception) =>
        exception is GnuTlsException or IOException { InnerException: GnuTlsException };

    /// <summary>
    /// A <see cref="Stream"/> that wraps the real <c>GnuTlsInternalStream</c> and holds
    /// <see cref="_ioLock"/> for the entire duration of every native read/write, so a concurrent
    /// <c>sess.Dispose()</c> (taken under the same lock in <see cref="SerializedGnuTlsStream.Dispose"/>)
    /// is forced to wait. Does NOT own/dispose the inner stream — that is done, serialized, by the
    /// owning <see cref="SerializedGnuTlsStream"/>.
    /// </summary>
    internal sealed class SerializingStream : Stream
    {
        private readonly SerializedGnuTlsStream _owner;
        private readonly Stream _inner;
        private readonly SemaphoreSlim _lock;

        public SerializingStream(SerializedGnuTlsStream owner, Stream inner, SemaphoreSlim ioLock)
        {
            _owner = owner;
            _inner = inner;
            _lock = ioLock;
        }

        private void ThrowIfDisposed()
        {
            if (_owner.DisposeRequested)
                throw new ObjectDisposedException(nameof(SerializedGnuTlsStream), "GnuTLS session disposal has started");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            _lock.Wait();
            try { ThrowIfDisposed(); return _inner.Read(buffer, offset, count); }
            catch (GnuTlsException ex) { throw ReadFailed(ex); }
            finally { _lock.Release(); }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Hold the lock across the native recv. We deliberately run the SYNC Read (the only real
            // implementation) rather than the base ReadAsync — the base would dispatch it to the pool
            // UNTRACKED, which is the exact bug. The native recv can't be cancelled; once started it
            // runs to CommTimeout, and we keep the lock until it returns so Dispose waits it out.
            ThrowIfDisposed();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { ThrowIfDisposed(); return await Task.Run(() => _inner.Read(buffer, offset, count), CancellationToken.None).ConfigureAwait(false); }
            catch (GnuTlsException ex) { throw ReadFailed(ex); }
            finally { _lock.Release(); }
        }

        // The package reports failed native recv calls as GnuTlsException (not IOException).
        // Normalize only at the I/O boundary so search's transport retry and spread's scan
        // poisoning recognize them, while handshake/authentication failures remain distinct.
        // Invalidate under the native I/O lock before any queued teardown can send gnutls_bye.
        private IOException ReadFailed(GnuTlsException ex)
        {
            FtpConnectionPool.MarkGnuTlsSessionUnusable(_inner);
            return new IOException($"GnuTLS read failed: {GnuTlsErrorName(ex.ExResult)} ({ex.ExResult}) — connection lost", ex);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // FluentFTP's async data path calls this with an ARRAY-backed Memory (buffer.AsMemory()).
            // Recv straight into the caller's array — no rent, no extra copy on the multi-GB hot path.
            if (MemoryMarshal.TryGetArray<byte>(buffer, out var seg) && seg.Array != null)
                return new ValueTask<int>(ReadAsync(seg.Array, seg.Offset, seg.Count, cancellationToken));
            return RentingReadAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> RentingReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var n = await ReadAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                new ReadOnlySpan<byte>(rented, 0, n).CopyTo(buffer.Span);
                return n;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            _lock.Wait();
            try { ThrowIfDisposed(); _inner.Write(buffer, offset, count); }
            catch (ArgumentOutOfRangeException ex) when (IsFailedSend(ex)) { throw SendFailed(ex); }
            finally { _lock.Release(); }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { ThrowIfDisposed(); await Task.Run(() => _inner.Write(buffer, offset, count), CancellationToken.None).ConfigureAwait(false); }
            catch (ArgumentOutOfRangeException ex) when (IsFailedSend(ex)) { throw SendFailed(ex); }
            finally { _lock.Release(); }
        }

        // GnuTlsInternalStream.Write (FluentFTP.GnuTLS 1.0.38) leaves its retry loop holding a
        // FATAL gnutls_record_send result and then uses it as an Array.Copy source index, so a
        // dead socket (-53 PUSH_ERROR) or session (-10 INVALID_SESSION) throws
        // ArgumentOutOfRangeException("sourceIndex", <error code>) — and skips the library's own
        // IsSessionUsable = false. Report it as the transport failure it is, and do what the
        // library skipped so the pool's health check and teardown see a dead session.
        private static bool IsFailedSend(ArgumentOutOfRangeException ex) =>
            ex.ParamName == "sourceIndex" && ex.ActualValue is int code && code < 0;

        private IOException SendFailed(ArgumentOutOfRangeException ex)
        {
            var code = (int)ex.ActualValue!;
            FtpConnectionPool.MarkGnuTlsSessionUnusable(_inner);
            return new IOException($"GnuTLS send failed: {GnuTlsErrorName(code)} ({code}) — connection lost", ex);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Send straight from the caller's array when possible — no rent/copy on the hot path.
            if (MemoryMarshal.TryGetArray(buffer, out var seg) && seg.Array != null)
                return new ValueTask(WriteAsync(seg.Array, seg.Offset, seg.Count, cancellationToken));
            return RentingWriteAsync(buffer, cancellationToken);
        }

        private async ValueTask RentingWriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(rented);
                await WriteAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
        }

        public override void Flush() => _inner.Flush();
        public override bool CanRead => !_owner.DisposeRequested && _inner.CanRead;
        public override bool CanWrite => !_owner.DisposeRequested && _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        // Do NOT dispose the inner stream here — the owning SerializedGnuTlsStream frees it under
        // the lock. This wrapper is a serializing view only (leaveOpen semantics).
        protected override void Dispose(bool disposing) { }
    }
}
