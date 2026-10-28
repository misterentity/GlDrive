using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class SiteTcpProbeTests
{
    [Fact]
    public void DomainConnectUsesRemoteDnsAndExactPort()
    {
        Assert.Equal(new byte[] { 5, 1, 0, 3, 4, (byte)'s', (byte)'i', (byte)'t', (byte)'e', 8, 73 }, SiteTcpProbe.ConnectRequest("site", 2121));
        Assert.Equal(TimeSpan.FromSeconds(5), SiteTcpProbe.Deadline);
    }

    [Theory]
    [InlineData("127.0.0.1", 1, 10)]
    [InlineData("::1", 4, 22)]
    public void IpConnectUsesCorrectAddressType(string address, byte type, int length)
    {
        var request = SiteTcpProbe.ConnectRequest(address, 21);
        Assert.Equal(type, request[3]);
        Assert.Equal(length, request.Length);
        Assert.Equal(21, request[^1]);
    }

    [Theory]
    [InlineData("", 21)]
    [InlineData("site\nother", 21)]
    [InlineData("site name", 21)]
    [InlineData("site", 0)]
    [InlineData("site", 65536)]
    public void InvalidEndpointIsRejectedBeforeNetworkWork(string host, int port) =>
        Assert.False(SiteTcpProbe.ValidEndpoint(host, port));

    [Fact]
    public async Task SocksSuccessWritesOnlyGreetingAndConnect()
    {
        using var stream = new ScriptedStream([5, 0, 5, 0, 0, 1, 127, 0, 0, 1, 0, 21]);
        Assert.True(await SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "", "", default));
        Assert.Equal(new byte[] { 5, 1, 0 }.Concat(SiteTcpProbe.ConnectRequest("site", 21)).ToArray(), stream.Written.ToArray());
        Assert.Equal(stream.ResponseLength, stream.Consumed);
    }

    [Fact]
    public async Task SocksAuthUsesOnlyProxyCredentialsAndNoTargetFtpCommands()
    {
        using var stream = new ScriptedStream([5, 2, 1, 0, 5, 0, 0, 3, 1, (byte)'x', 0, 21]);
        Assert.True(await SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "u", "p", default));
        Assert.Equal(new byte[] { 5, 1, 2, 1, 1, (byte)'u', 1, (byte)'p' }
            .Concat(SiteTcpProbe.ConnectRequest("site", 21)).ToArray(), stream.Written.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task ConfiguredAuthNeverAcceptsDowngradeOrFallback(byte method)
    {
        using var stream = new ScriptedStream([5, method]);
        Assert.False(await SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "u", "p", default));
        Assert.Equal(new byte[] { 5, 1, 2 }, stream.Written.ToArray());
    }

    [Fact]
    public async Task RejectedProxyAuthenticationDoesNotSendTargetConnect()
    {
        using var stream = new ScriptedStream([5, 2, 1, 1]);
        Assert.False(await SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "u", "p", default));
        Assert.Equal(8, stream.Written.Length);
    }

    [Fact]
    public async Task ProxyConnectRefusalFailsWithoutTargetProtocolBytes()
    {
        using var stream = new ScriptedStream([5, 0, 5, 5, 0, 1]);
        Assert.False(await SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "", "", default));
        Assert.Equal(3 + SiteTcpProbe.ConnectRequest("site", 21).Length, stream.Written.Length);
    }

    [Fact]
    public async Task TruncatedReplyDoesNotCountAsConnected()
    {
        using var stream = new ScriptedStream([5]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => SiteTcpProbe.NegotiateSocksAsync(stream, "site", 21, "", "", default));
    }

    private sealed class ScriptedStream(byte[] response) : Stream
    {
        private readonly MemoryStream _response = new(response);
        public MemoryStream Written { get; } = new();
        public long Consumed => _response.Position;
        public long ResponseLength => response.Length;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _response.Read(buffer, offset, Math.Min(1, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _response.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _response.Dispose(); Written.Dispose(); } base.Dispose(disposing); }
    }
}
