using System.IO;

namespace GlDrive.Util;

internal static class StreamTransfer
{
    // A known length is a promise, not a hint: EOF before it must never publish a cache entry.
    internal static async Task CopyAsync(Stream source, Stream destination, long? length,
        CancellationToken ct, Stream? mirror = null)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[256 * 1024];
        var remaining = length ?? long.MaxValue;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                if (length.HasValue) throw new EndOfStreamException($"Transfer ended with {remaining} bytes missing.");
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            if (mirror != null) await mirror.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        await destination.FlushAsync(ct);
    }
}
