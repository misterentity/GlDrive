using System.IO;
using System.Text;
using System.IO.Hashing;
using System.Security.Cryptography;
using FluentFTP;
using GlDrive.Ftp;

namespace GlDrive.Downloads.Segmented;

internal sealed record DownloadMirror(string Id, string Name, string Root, StreamingDownloader Downloader);

// SFV entries cover the common archive-volume case, including copies with different FTP timestamps.
// For files outside a manifest, both servers must return the same FTP checksum.
internal sealed class MirrorContentMismatchException(string path)
    : IOException($"Downloaded mirror content failed checksum verification: {path}");

internal sealed record MirrorChecksum(FtpHashAlgorithm Algorithm, string Value)
{
    internal async Task VerifyAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual;
        if (Algorithm == FtpHashAlgorithm.CRC)
        {
            var crc = new Crc32();
            await crc.AppendAsync(input, ct);
            actual = crc.GetCurrentHashAsUInt32().ToString("X8");
        }
        else
        {
            byte[] digest = Algorithm switch
            {
                FtpHashAlgorithm.MD5 => await MD5.HashDataAsync(input, ct),
                FtpHashAlgorithm.SHA1 => await SHA1.HashDataAsync(input, ct),
                FtpHashAlgorithm.SHA256 => await SHA256.HashDataAsync(input, ct),
                FtpHashAlgorithm.SHA512 => await SHA512.HashDataAsync(input, ct),
                _ => throw new IOException($"Unsupported FTP checksum: {Algorithm}")
            };
            actual = Convert.ToHexString(digest);
        }
        if (!actual.Equals(Value, StringComparison.OrdinalIgnoreCase)) throw new MirrorContentMismatchException(path);
    }
}

internal sealed class MirrorInventory
{
    internal Dictionary<string, MirrorChecksum> Files { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflicts = new(StringComparer.Ordinal);

    internal void AddManifest(string relativePath, string text)
    {
        var directory = relativePath[..(relativePath.LastIndexOf('/') + 1)];
        foreach (var line in text.Split('\n'))
        {
            var entry = line.Trim();
            var separator = entry.LastIndexOfAny([' ', '\t']);
            if (entry.StartsWith(';') || separator <= 0) continue;
            var crc = entry[(separator + 1)..];
            if (crc.Length != 8 || !crc.All(Uri.IsHexDigit)) continue;
            var name = entry[..separator].TrimEnd().Replace('\\', '/');
            if (name.Split('/').Any(part => part is "" or "." or ".." || part.Any(char.IsControl))) continue;
            var key = directory + name;
            var checksum = new MirrorChecksum(FtpHashAlgorithm.CRC, crc.ToUpperInvariant());
            if (_conflicts.Contains(key)) continue;
            if (Files.TryGetValue(key, out var old) && old != checksum)
            {
                Files.Remove(key);
                _conflicts.Add(key);
            }
            else Files[key] = checksum;
        }
    }

    internal static async Task<MirrorInventory> ReadAsync(FtpLane lane, string root,
        IEnumerable<string> manifests, CancellationToken ct)
    {
        var inventory = new MirrorInventory();
        foreach (var relative in manifests)
        {
            var path = root + "/" + relative;
            var size = await lane.GetSizeAsync(path, ct);
            if (size is <= 0 or > 1024 * 1024) continue;
            var bytes = new byte[(int)size];
            await using var reader = await lane.OpenAsync(new(path, "", size), 0, size, ct);
            var position = 0;
            while (position < bytes.Length)
            {
                var read = await reader.ReadAsync(bytes.AsMemory(position), ct);
                if (read == 0) throw new EndOfStreamException(path);
                position += read;
            }
            await reader.FinishAsync(true, ct);
            inventory.AddManifest(relative, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        }
        return inventory;
    }
}

internal sealed class MirrorLane(FtpLane ftp, IReadOnlyDictionary<string, string> paths,
    ITransferPacer pacer) : ISegmentLane, IMirrorLane
{
    public ITransferPacer Pacer => pacer;
    public void RecordBytes(long bytes) => DownloadNetworkServices.RecordBytes(NetworkId, bytes);
    public bool CanServe(TransferFile file) => paths.ContainsKey(file.Key);
    public string Label => ftp.Label;
    public string? NetworkId => ftp.NetworkId;
    public bool CanStopEarly => ftp.CanStopEarly;
    public bool ShouldYield => ftp.ShouldYield;
    public void Fault(Exception ex) => ftp.Fault(ex);
    public ValueTask DisposeAsync() => ftp.DisposeAsync();
    public async Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct)
    {
        var path = paths[file.Key];
        if (await ftp.GetSizeAsync(path, ct) != file.Size) throw new IOException($"Mirror file size changed: {path}");
        return await ftp.OpenAsync(file with { Key = path }, offset, end, ct);
    }
}
