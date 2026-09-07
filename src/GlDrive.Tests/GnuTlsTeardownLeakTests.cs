using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Streams;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.106 — every pool teardown leaked its native GnuTLS session AND certificate credentials.
///
/// <c>NeutralizeGnuTls</c> nulled <c>FtpSocketStream.m_customStream</c> so FluentFTP's own
/// dispose chain could never reach the GnuTLS wrapper (that was the v3.5.1 crash guard, before
/// <c>SerializedGnuTlsStream</c> made freeing safe). Nothing else ever disposed the detached
/// stream, so <c>gnutls_deinit</c> and <c>gnutls_certificate_free_credentials</c> never ran.
/// Each credentials object holds a parsed copy of the whole Windows trust store (~150 certs),
/// so the leak was ~2.3 MB per connection: production sat at 911 MB private after 51 h and 293
/// connections, with a 100 MB managed heap. The detached stream must now be handed to the
/// deferred teardown and disposed there.
/// </summary>
public sealed class GnuTlsTeardownLeakTests
{
    private sealed class FakeTlsStream : IFtpStream
    {
        public int DisposeCount;
        public void Init(BaseFtpClient client, string targetHost, Socket socket,
            CustomRemoteCertificateValidationCallback customRemoteCertificateValidation,
            bool isControl, IFtpStream controlConnStream, IFtpStreamConfig config) { }
        public Stream GetBaseStream() => Stream.Null;
        public bool CanRead() => true;
        public bool CanWrite() => true;
        public SslProtocols GetSslProtocol() => SslProtocols.None;
        public string GetCipherSuite() => "fake";
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static (AsyncFtpClient client, FakeTlsStream fake, FieldInfo customField, FtpSocketStream stream) Arrange()
    {
        var client = new AsyncFtpClient();
        var stream = ((IInternalFtpClient)client).GetBaseStream();
        if (stream == null)
        {
            stream = new FtpSocketStream(client);
            var streamField = typeof(BaseFtpClient).GetField("m_stream", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("FluentFTP BaseFtpClient.m_stream not found");
            streamField.SetValue(client, stream);
        }
        var customField = typeof(FtpSocketStream).GetField("m_customStream", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("FluentFTP FtpSocketStream.m_customStream not found");
        var fake = new FakeTlsStream();
        customField.SetValue(stream, fake);
        return (client, fake, customField, stream);
    }

    [Fact]
    public void Neutralize_detaches_the_tls_stream_without_disposing_it()
    {
        var (client, fake, customField, stream) = Arrange();

        var detached = FtpConnectionPool.NeutralizeGnuTls(client);

        Assert.Same(fake, detached);
        Assert.Null(customField.GetValue(stream));   // FluentFTP must not double-dispose it
        Assert.Equal(0, fake.DisposeCount);          // the free is deferred, never immediate
    }

    [Fact]
    public void FreeDetached_disposes_exactly_once_and_tolerates_null()
    {
        var fake = new FakeTlsStream();

        FtpConnectionPool.FreeDetachedGnuTls(fake, "test");
        FtpConnectionPool.FreeDetachedGnuTls(null, "test");

        Assert.Equal(1, fake.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quarantine_frees_the_detached_session_after_the_reclaim_delay(bool recvQuiescent)
    {
        var (client, fake, customField, stream) = Arrange();
        var pool = new FtpConnectionPool(factory: null!, maxSize: 2);
        var saved = FtpConnectionPool.AbandonedReclaimDelay;
        FtpConnectionPool.AbandonedReclaimDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            pool.Quarantine(client, "test", recvQuiescent);

            if (recvQuiescent)
                Assert.Null(customField.GetValue(stream)); // detached synchronously
            Assert.Equal(0, fake.DisposeCount);            // but never freed synchronously

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Volatile.Read(ref fake.DisposeCount) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(1, fake.DisposeCount);
            Assert.Null(customField.GetValue(stream));
        }
        finally
        {
            FtpConnectionPool.AbandonedReclaimDelay = saved;
        }
    }

    [Fact]
    public void Every_neutralize_call_site_keeps_and_frees_the_detached_stream()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Ftp", "FtpConnectionPool.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = Directory.GetParent(dir)?.FullName;
        }
        var source = File.ReadAllText(path ?? throw new InvalidOperationException("FtpConnectionPool.cs not found"));

        var calls = Regex.Matches(source, @"NeutralizeGnuTls\(client\)").Count;
        var kept = Regex.Matches(source, @"= NeutralizeGnuTls\(client\)").Count;
        var freed = Regex.Matches(source, @"FreeDetachedGnuTls\(detached").Count;

        Assert.True(calls >= 3, "expected the quarantine funnel and both disconnect paths to neutralize");
        Assert.Equal(calls, kept);     // a bare `NeutralizeGnuTls(client);` statement drops the session on the floor
        Assert.True(freed >= 3, "each teardown path must free what it detached");
    }
}
