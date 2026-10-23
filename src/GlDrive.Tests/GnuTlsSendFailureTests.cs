using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP.GnuTLS;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.28 — FluentFTP.GnuTLS 1.0.38 <c>GnuTlsInternalStream.Write</c> leaves its retry loop with
/// a fatal <c>gnutls_record_send</c> result (-53 GNUTLS_E_PUSH_ERROR on a dead socket, -10
/// GNUTLS_E_INVALID_SESSION) and then uses that negative number as an <c>Array.Copy</c> source
/// index. A lost connection therefore surfaced as
/// <c>ArgumentOutOfRangeException: sourceIndex ('-53')</c> (2026-10-22 13:17:26, SITE INVITE,
/// NewReleaseMonitor LIST, ConnectionMonitor) — invisible to every <c>IOException</c> transport
/// classifier — and the throw skipped the library's own <c>IsSessionUsable = false</c>.
/// </summary>
public class GnuTlsSendFailureTests
{
    private sealed class ThrowingStream(Exception toThrow) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw toThrow;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Exactly what Array.Copy throws inside GnuTlsInternalStream.Write.
    private static ArgumentOutOfRangeException LibrarySendFailure(int code) =>
        new("sourceIndex", code, $"sourceIndex ('{code}') must be greater than or equal to '0'.");

    private static Stream Wrap(Exception toThrow) =>
        new SerializedGnuTlsStream.SerializingStream(new SerializedGnuTlsStream(), new ThrowingStream(toThrow), new SemaphoreSlim(1, 1));

    [Fact]
    public async Task Failed_async_send_surfaces_as_IOException_naming_the_GnuTLS_error()
    {
        var original = LibrarySendFailure(-53);
        var stream = Wrap(original);

        var ex = await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[8], 0, 8, CancellationToken.None));

        Assert.Contains("GNUTLS_E_PUSH_ERROR", ex.Message);
        Assert.Contains("-53", ex.Message);
        Assert.Same(original, ex.InnerException);
    }

    [Fact]
    public void Failed_sync_send_surfaces_as_IOException()
    {
        var stream = Wrap(LibrarySendFailure(-10));

        var ex = Assert.Throws<IOException>(() => stream.Write(new byte[8], 0, 8));

        Assert.Contains("GNUTLS_E_INVALID_SESSION", ex.Message);
    }

    [Fact]
    public async Task Unrelated_argument_errors_are_not_reinterpreted_as_connection_loss()
    {
        var unrelated = new ArgumentOutOfRangeException("count", 5, "count");
        var stream = Wrap(unrelated);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.WriteAsync(new byte[8], 0, 8, CancellationToken.None));
        Assert.Same(unrelated, ex);
    }

    [Fact]
    public void Marking_a_failed_session_unusable_clears_the_library_flag()
    {
        var type = typeof(GnuTlsStream).Assembly.GetType("FluentFTP.GnuTLS.GnuTlsInternalStream", throwOnError: true)!;
        var session = RuntimeHelpers.GetUninitializedObject(type);
        var prop = type.GetProperty("IsSessionUsable")!;
        prop.SetValue(session, true);

        FtpConnectionPool.MarkGnuTlsSessionUnusable(session);

        Assert.False((bool)prop.GetValue(session)!);
    }
}
