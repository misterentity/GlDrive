using System.Buffers;
using System.IO;
using FluentFTP;
using FluentFTP.GnuTLS;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class GnuTlsReadFailureTests
{
    // Use the pinned package's exception, which derives from Exception, not IOException.
    private static GnuTlsException ReadFailure() => new("Error in the pull function.")
    {
        ExMethod = "*GnuTlsRecordRecv(...)",
        ExResult = -54,
        ExMeaning = "GNUTLS_E_PULL_ERROR"
    };

    private sealed class FailingStream(Exception failure) : Stream
    {
        public bool IsSessionUsable { get; private set; } = true;
        public int ReadCalls { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw failure;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class NonArrayMemory : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[8];
        public override Span<byte> GetSpan() => _bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private static Stream Wrap(Stream inner, SemaphoreSlim gate) =>
        new SerializedGnuTlsStream.SerializingStream(new SerializedGnuTlsStream(), inner, gate);

    [Fact]
    public void Sync_read_failure_becomes_transport_error_and_invalidates_session()
    {
        var failure = ReadFailure();
        using var inner = new FailingStream(failure);
        using var gate = new SemaphoreSlim(1, 1);
        using var stream = Wrap(inner, gate);

        var error = Assert.Throws<IOException>(() => stream.Read(new byte[8], 0, 8));

        Assert.Same(failure, error.InnerException);
        Assert.Contains("GNUTLS_E_PULL_ERROR (-54)", error.Message);
        Assert.False(inner.IsSessionUsable);
        Assert.Equal(1, gate.CurrentCount);
        Assert.True(SerializedGnuTlsStream.IsGnuTlsIoFailure(error));
    }

    [Fact]
    public void Background_failure_classifier_accepts_package_errors_but_not_unrelated_IO_errors()
    {
        var native = ReadFailure();
        Assert.True(SerializedGnuTlsStream.IsGnuTlsIoFailure(native));
        Assert.True(SerializedGnuTlsStream.IsGnuTlsIoFailure(new IOException("read failed", native)));
        Assert.False(SerializedGnuTlsStream.IsGnuTlsIoFailure(new IOException("disk failed")));
        Assert.False(SerializedGnuTlsStream.IsGnuTlsIoFailure(new IOException("disk failed", new ArgumentException())));
        Assert.False(SerializedGnuTlsStream.IsGnuTlsIoFailure(new InvalidOperationException("unexpected", native)));
    }

    [Theory]
    [InlineData("array")]
    [InlineData("memory")]
    [InlineData("non-array-memory")]
    public async Task Async_read_failure_becomes_transport_error_and_releases_the_native_lock(string overload)
    {
        var failure = ReadFailure();
        using var inner = new FailingStream(failure);
        using var gate = new SemaphoreSlim(1, 1);
        using var stream = Wrap(inner, gate);
        using var memory = new NonArrayMemory();

        var error = await Assert.ThrowsAsync<IOException>(() => overload switch
        {
            "array" => stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None),
            "memory" => stream.ReadAsync(new byte[8].AsMemory()).AsTask(),
            _ => stream.ReadAsync(memory.Memory).AsTask()
        });

        Assert.Same(failure, error.InnerException);
        Assert.Contains("GNUTLS_E_PULL_ERROR (-54)", error.Message);
        Assert.False(inner.IsSessionUsable);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Search_retries_read_transport_failure_and_returns_the_second_listing()
    {
        using var inner = new FailingStream(ReadFailure());
        using var gate = new SemaphoreSlim(1, 1);
        using var stream = Wrap(inner, gate);
        var attempts = 0;
        FtpListItem[] expected = [new() { Name = "release", Type = FtpObjectType.Directory }];

        var result = await FtpSearchService.ListWithTransientRetry(async (_, ct) =>
        {
            if (++attempts == 1) Assert.Equal(8, await stream.ReadAsync(new byte[8], 0, 8, ct));
            return expected;
        }, "/TV", CancellationToken.None, TimeSpan.Zero);

        Assert.Same(expected, result);
        Assert.Equal(2, attempts);
        Assert.False(inner.IsSessionUsable);
    }

    [Fact]
    public async Task Search_stops_after_one_retry_if_read_transport_failure_persists()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => FtpSearchService.ListWithTransientRetry(async (_, ct) =>
        {
            attempts++;
            using var inner = new FailingStream(ReadFailure());
            using var gate = new SemaphoreSlim(1, 1);
            using var stream = Wrap(inner, gate);
            Assert.Equal(8, await stream.ReadAsync(new byte[8], 0, 8, ct));
            return Array.Empty<FtpListItem>();
        }, "/TV", CancellationToken.None, TimeSpan.Zero));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Cancellation_waiting_for_native_lock_never_reads_or_invalidates_session()
    {
        using var inner = new FailingStream(ReadFailure());
        using var gate = new SemaphoreSlim(0, 1);
        using var stream = Wrap(inner, gate);
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[8], 0, 8, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.Equal(0, inner.ReadCalls);
        Assert.True(inner.IsSessionUsable);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_argument_error_is_preserved_without_invalidating_session(bool asynchronous)
    {
        var original = new ArgumentOutOfRangeException("count");
        using var inner = new FailingStream(original);
        using var gate = new SemaphoreSlim(1, 1);
        using var stream = Wrap(inner, gate);

        var error = asynchronous
            ? await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAsync(new byte[8], 0, 8))
            : Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[8], 0, 8));

        Assert.Same(original, error);
        Assert.True(inner.IsSessionUsable);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Cancellation_from_inner_read_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        using var inner = new FailingStream(original);
        using var gate = new SemaphoreSlim(1, 1);
        using var stream = Wrap(inner, gate);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[8], 0, 8));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(inner.IsSessionUsable);
        Assert.Equal(1, gate.CurrentCount);
    }
}
