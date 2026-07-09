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
/// Dispose blocks until any in-flight recv returns (native recv is floored at GnuTLS CommTimeout
/// ~15s), then frees the session with nothing reading it. If a recv somehow never returns, we
/// LEAK the session rather than free it under a live read — a bounded native leak is always
/// preferable to a process crash.
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
    private readonly GnuTlsStream _inner = new();

    // The serializing view FluentFTP reads/writes through.
    private SerializingStream? _wrapper;

    /// <summary>
    /// The underlying <c>GnuTlsInternalStream</c>. Named/typed so the connection pool's crash-guard
    /// reflection (which reads <c>m_customStream.BaseStream</c> and inspects IsSessionUsable/sess)
    /// resolves it unchanged. Do NOT rename without updating GnuTlsReflectionGuard + the pool.
    /// </summary>
    internal Stream? BaseStream;

    /// <summary>
    /// Max time <see cref="Dispose"/> waits for an in-flight recv/send to finish before freeing the
    /// session. Must exceed the GnuTLS CommTimeout floor (~15s) so a normal recv always drains in
    /// time; on the rare timeout we skip the free (leak, never crash).
    /// </summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(20);

    private volatile bool _disposed;
    // Set true UNDER _ioLock after the session is freed. The read wrapper checks it after acquiring
    // the lock so a read that was queued behind Dispose fails cleanly instead of touching a freed sess.
    private volatile bool _sessionFreed;

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

    public bool Validate() => _inner.Validate();

    public Stream GetBaseStream() => _wrapper ?? _inner.GetBaseStream();

    public bool CanRead() => _inner.CanRead();

    public bool CanWrite() => _inner.CanWrite();

    public SslProtocols GetSslProtocol() => _inner.GetSslProtocol();

    public string GetCipherSuite() => _inner.GetCipherSuite();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Serialize the session free (sess.Dispose inside _inner.Dispose) against any in-flight recv.
        // Acquire the SAME lock the read wrapper holds during a native recv — this is the whole fix.
        var acquired = false;
        try { acquired = _ioLock.Wait(DisposeDrainTimeout); }
        catch (ObjectDisposedException) { /* already torn down */ }

        try
        {
            if (acquired)
            {
                _inner.Dispose(); // GnuTlsStream.Dispose → GnuTlsInternalStream.Dispose → sess.Dispose()
                _sessionFreed = true; // any read queued behind this lock must now bail, not touch freed sess
            }
            else
            {
                // A recv is still live after DisposeDrainTimeout (> CommTimeout). Freeing the session
                // now would fault the process. Leak it instead — bounded, and never a crash.
                Log.Warning("GnuTLS: recv did not drain within {Timeout}s — leaking session instead of freeing under a live read",
                    (int)DisposeDrainTimeout.TotalSeconds);
            }
        }
        finally
        {
            if (acquired) _ioLock.Release();
        }
    }

    /// <summary>
    /// A <see cref="Stream"/> that wraps the real <c>GnuTlsInternalStream</c> and holds
    /// <see cref="_ioLock"/> for the entire duration of every native read/write, so a concurrent
    /// <c>sess.Dispose()</c> (taken under the same lock in <see cref="SerializedGnuTlsStream.Dispose"/>)
    /// is forced to wait. Does NOT own/dispose the inner stream — that is done, serialized, by the
    /// owning <see cref="SerializedGnuTlsStream"/>.
    /// </summary>
    private sealed class SerializingStream : Stream
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

        private void ThrowIfFreed()
        {
            if (_owner._sessionFreed)
                throw new ObjectDisposedException(nameof(SerializedGnuTlsStream), "GnuTLS session was disposed");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _lock.Wait();
            try { ThrowIfFreed(); return _inner.Read(buffer, offset, count); }
            finally { _lock.Release(); }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Hold the lock across the native recv. We deliberately run the SYNC Read (the only real
            // implementation) rather than the base ReadAsync — the base would dispatch it to the pool
            // UNTRACKED, which is the exact bug. The native recv can't be cancelled; once started it
            // runs to CommTimeout, and we keep the lock until it returns so Dispose waits it out.
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { ThrowIfFreed(); return await Task.Run(() => _inner.Read(buffer, offset, count), CancellationToken.None).ConfigureAwait(false); }
            finally { _lock.Release(); }
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
            _lock.Wait();
            try { ThrowIfFreed(); _inner.Write(buffer, offset, count); }
            finally { _lock.Release(); }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { ThrowIfFreed(); await Task.Run(() => _inner.Write(buffer, offset, count), CancellationToken.None).ConfigureAwait(false); }
            finally { _lock.Release(); }
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
        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
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
