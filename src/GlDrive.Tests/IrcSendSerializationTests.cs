using System.IO;
using System.Text;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// SslStream permits exactly ONE in-flight write; a second overlapping WriteAsync throws
/// NotSupportedException("This method may not be called when another write operation is
/// pending."). IrcClient.SendRawAsync had no mutual exclusion, and every IRC command funnels
/// through it from genuinely concurrent callers: the read loop's PONG reply, the keepalive PING
/// timer, the invite-only JOIN retry timer, and user/wishlist PRIVMSGs. The collision was caught
/// and logged as a warning, so the line was SILENTLY DROPPED — observed live 2026-08-18 21:41:29.
///
/// A dropped PONG is a ping-timeout disconnect; a dropped JOIN extends the #ent retry loop; a
/// dropped DH1080 line breaks FiSH key exchange. These tests assert the property that matters:
/// under concurrency every line still reaches the wire, intact and uncorrupted.
/// </summary>
public class IrcSendSerializationTests
{
    /// <summary>
    /// Stands in for SslStream: faithful to the one-writer-at-a-time contract, and it records
    /// whether an overlap was ever attempted so a test can prove the serialization is real
    /// rather than merely getting lucky on timing.
    /// </summary>
    private sealed class SingleWriterStream : Stream
    {
        private int _writersInFlight;
        private readonly MemoryStream _sink = new();
        private readonly object _sinkLock = new();

        public volatile bool OverlapAttempted;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writersInFlight) > 1)
            {
                Interlocked.Decrement(ref _writersInFlight);
                OverlapAttempted = true;
                throw new NotSupportedException(
                    "This method may not be called when another write operation is pending.");
            }

            try
            {
                // A real await point, so overlapping callers actually interleave here instead of
                // completing synchronously and hiding the race.
                await Task.Yield();
                lock (_sinkLock) _sink.Write(buffer.Span);
            }
            finally
            {
                Interlocked.Decrement(ref _writersInFlight);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public string Text()
        {
            lock (_sinkLock) return Encoding.UTF8.GetString(_sink.ToArray());
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ConcurrentSends_DeliverEveryLine()
    {
        using var client = new IrcClient();
        var stream = new SingleWriterStream();
        client.AttachStreamForTests(stream);

        var lines = Enumerable.Range(0, 64).Select(i => $"PRIVMSG #chan :msg{i:D3}").ToArray();

        await Task.WhenAll(lines.Select(l => Task.Run(() => client.SendRawAsync(l))));

        var text = stream.Text();
        var dropped = lines.Where(l => !text.Contains(l, StringComparison.Ordinal)).ToArray();

        Assert.True(dropped.Length == 0,
            $"{dropped.Length}/{lines.Length} IRC lines were dropped under concurrency: " +
            string.Join(", ", dropped.Take(5)));
    }

    [Fact]
    public async Task ConcurrentSends_NeverOverlapOnTheStream()
    {
        using var client = new IrcClient();
        var stream = new SingleWriterStream();
        client.AttachStreamForTests(stream);

        await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(i => Task.Run(() => client.SendRawAsync($"PONG :token{i}"))));

        Assert.False(stream.OverlapAttempted,
            "Two writes were in flight on the stream at once — SendRawAsync is not serialized.");
    }

    /// <summary>
    /// StreamWriter is itself not thread-safe: concurrent WriteLineAsync calls share one internal
    /// char buffer, so an unserialized send path can interleave two commands into a single
    /// corrupt line even when the underlying stream tolerates it. Every line must arrive whole.
    /// </summary>
    [Fact]
    public async Task ConcurrentSends_DoNotInterleaveWithinALine()
    {
        using var client = new IrcClient();
        var stream = new SingleWriterStream();
        client.AttachStreamForTests(stream);

        var lines = Enumerable.Range(0, 64).Select(i => $"PRIVMSG #chan :{new string((char)('a' + i % 26), 40)}").ToArray();

        await Task.WhenAll(lines.Select(l => Task.Run(() => client.SendRawAsync(l))));

        var received = stream.Text()
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        Assert.All(received, line =>
            Assert.True(lines.Contains(line, StringComparer.Ordinal),
                $"Corrupt/interleaved line reached the wire: '{line}'"));
    }
}
