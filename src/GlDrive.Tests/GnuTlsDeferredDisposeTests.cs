using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Streams;
using GlDrive.Ftp;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("GnuTLS disposal logger", DisableParallelization = true)]
public sealed class GnuTlsDisposalLoggerCollection { }

[Collection("GnuTLS disposal logger")]
public sealed class GnuTlsDeferredDisposeTests
{
    private sealed class BlockingStream : Stream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public bool IsSessionUsable { get; private set; } = true;
        private int _calls;
        private int _active;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);

        private void PerformIo()
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _active);
            try
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test I/O was not released");
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public override int Read(byte[] buffer, int offset, int count) { PerformIo(); return 1; }
        public override void Write(byte[] buffer, int offset, int count) => PerformIo();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeNativeStream(BlockingStream stream) : IFtpStream
    {
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount;
        public bool DisposedDuringIo;
        public bool? UsableAtDisposal;
        public Exception? DisposeFailure;

        public void Init(BaseFtpClient client, string targetHost, Socket socket,
            CustomRemoteCertificateValidationCallback validation, bool isControl,
            IFtpStream controlConnStream, IFtpStreamConfig config) { }
        public Stream GetBaseStream() => stream;
        public bool CanRead() => stream.CanRead;
        public bool CanWrite() => stream.CanWrite;
        public SslProtocols GetSslProtocol() => SslProtocols.None;
        public string GetCipherSuite() => "synthetic";
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposedDuringIo |= stream.Active != 0;
            UsableAtDisposal = stream.IsSessionUsable;
            Disposed.TrySetResult();
            if (DisposeFailure != null) throw DisposeFailure;
        }
    }

    private static (SerializedGnuTlsStream owner, Stream wrapper, BlockingStream io, FakeNativeStream native)
        Create(TimeSpan? drain = null)
    {
        var io = new BlockingStream();
        var native = new FakeNativeStream(io);
        var owner = new SerializedGnuTlsStream(native, drain ?? TimeSpan.FromMilliseconds(30));
        owner.Init(null!, "synthetic", null!, null!, true, null!, null!);
        return (owner, owner.GetBaseStream(), io, native);
    }

    private static Task BeginIo(Stream wrapper, string operation) => operation switch
    {
        "sync-read" => Task.Run(() => wrapper.Read(new byte[1], 0, 1)),
        "sync-write" => Task.Run(() => wrapper.Write(new byte[1], 0, 1)),
        "async-read" => wrapper.ReadAsync(new byte[1], 0, 1),
        "async-write" => wrapper.WriteAsync(new byte[1], 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    [Theory]
    [InlineData("sync-read")]
    [InlineData("async-read")]
    [InlineData("sync-write")]
    [InlineData("async-write")]
    public async Task Timed_out_dispose_retains_live_session_then_frees_once_after_io_drains(string operation)
    {
        var (owner, wrapper, io, native) = Create();
        var active = BeginIo(wrapper, operation);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(owner.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref native.DisposeCount));
            Assert.False(active.IsCompleted);
        }
        finally { io.Release.Set(); }

        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await native.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        Assert.Equal(1, Volatile.Read(ref native.DisposeCount));
        Assert.False(native.DisposedDuringIo);
        Assert.False(native.UsableAtDisposal); // delayed close must skip native gnutls_bye
    }

    [Fact]
    public async Task Queued_io_is_rejected_when_disposal_has_started()
    {
        var (owner, wrapper, io, native) = Create();
        var active = wrapper.ReadAsync(new byte[1], 0, 1);
        Task? queuedRead = null;
        Task? queuedWrite = null;
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queuedRead = wrapper.ReadAsync(new byte[1], 0, 1);
            queuedWrite = wrapper.WriteAsync(new byte[1], 0, 1);
            owner.Dispose();
            Assert.Equal(1, io.Calls);
        }
        finally { io.Release.Set(); }

        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedRead!);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedWrite!);
        await native.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, io.Calls);
        Assert.False(native.DisposedDuringIo);
    }

    [Fact]
    public async Task New_io_and_capabilities_report_disposal_before_live_io_drains()
    {
        var (owner, wrapper, io, native) = Create();
        var active = wrapper.ReadAsync(new byte[1], 0, 1);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Dispose();
            Assert.False(owner.CanRead());
            Assert.False(owner.CanWrite());
            Assert.False(wrapper.CanRead);
            Assert.False(wrapper.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => wrapper.Read(new byte[1], 0, 1));
            Assert.Throws<ObjectDisposedException>(() => wrapper.Write(new byte[1], 0, 1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wrapper.ReadAsync(new byte[1], 0, 1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wrapper.WriteAsync(new byte[1], 0, 1));
            Assert.Equal(1, io.Calls);
        }
        finally { io.Release.Set(); }

        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await native.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Concurrent_dispose_requests_share_one_eventual_cleanup()
    {
        var (owner, wrapper, io, native) = Create();
        var active = wrapper.ReadAsync(new byte[1], 0, 1);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(owner.Dispose)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref native.DisposeCount));
        }
        finally { io.Release.Set(); }

        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await native.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(owner.Dispose)));
        Assert.Equal(1, Volatile.Read(ref native.DisposeCount));
        Assert.False(native.DisposedDuringIo);
    }

    [Fact]
    public void Quiescent_dispose_frees_immediately_and_only_once()
    {
        var (owner, wrapper, _, native) = Create();
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(1, native.DisposeCount);
        Assert.False(native.DisposedDuringIo);
        Assert.True(native.UsableAtDisposal); // preserve normal graceful-close behavior
        Assert.Throws<ObjectDisposedException>(() => wrapper.Read(new byte[1], 0, 1));
    }

    [Fact]
    public async Task Io_draining_within_budget_is_freed_by_the_synchronous_disposer()
    {
        var (owner, wrapper, io, native) = Create(TimeSpan.FromSeconds(5));
        var active = wrapper.ReadAsync(new byte[1], 0, 1);
        Task? disposing = null;
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = Task.Run(owner.Dispose);
            Assert.True(SpinWait.SpinUntil(() => !owner.CanRead(), TimeSpan.FromSeconds(2)));
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref native.DisposeCount));
        }
        finally { io.Release.Set(); }

        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref native.DisposeCount));
        Assert.False(native.DisposedDuringIo);
        Assert.True(native.UsableAtDisposal);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public readonly TaskCompletionSource<LogEvent> Failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.MessageTemplate.Text.Contains("deferred session cleanup failed", StringComparison.Ordinal))
                Failure.TrySetResult(logEvent);
        }
    }

    [Fact]
    public async Task Deferred_cleanup_failure_is_observed_and_logged_without_retrying_native_disposal()
    {
        var (owner, wrapper, io, native) = Create();
        var failure = new IOException("synthetic disposal failure");
        native.DisposeFailure = failure;
        var sink = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        var active = wrapper.ReadAsync(new byte[1], 0, 1);
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Dispose();
            io.Release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            var logged = await sink.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(failure, logged.Exception);
            Assert.Equal(LogEventLevel.Warning, logged.Level);
            owner.Dispose();
            Assert.Equal(1, Volatile.Read(ref native.DisposeCount));
            Assert.Throws<ObjectDisposedException>(() => wrapper.Write(new byte[1], 0, 1));
        }
        finally
        {
            io.Release.Set();
            Log.Logger = previous;
        }
    }
}
