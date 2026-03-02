using System.IO;
using System.IO.Hashing;
using Serilog;

namespace GlDrive.Downloads;

public static class SfvVerifier
{
    public static async Task<List<string>> VerifyAsync(string dirPath, CancellationToken ct)
    {
        var failed = new List<string>();
        var sfvFiles = Directory.GetFiles(dirPath, "*.sfv");
        if (sfvFiles.Length == 0) return failed;

        foreach (var sfvFile in sfvFiles)
        {
            var lines = await File.ReadAllLinesAsync(sfvFile, ct);
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                    continue;

                var lastSpace = trimmed.LastIndexOf(' ');
                if (lastSpace <= 0) continue;

                var fileName = trimmed[..lastSpace].Trim();
                var expectedHex = trimmed[(lastSpace + 1)..].Trim();

                if (!uint.TryParse(expectedHex, System.Globalization.NumberStyles.HexNumber, null, out var expectedCrc))
                    continue;

                var filePath = Path.Combine(dirPath, fileName);
                if (!File.Exists(filePath))
                {
                    failed.Add($"{fileName} (missing)");
                    continue;
                }

                try
                {
                    var crc = new Crc32();
                    await using var fs = File.OpenRead(filePath);
                    var buffer = new byte[65536];
                    int bytesRead;
                    while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(), ct)) > 0)
                        crc.Append(buffer.AsSpan(0, bytesRead));

                    var hash = crc.GetCurrentHashAsUInt32();
                    if (hash != expectedCrc)
                    {
                        failed.Add($"{fileName} (CRC mismatch: expected {expectedCrc:X8}, got {hash:X8})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SFV check failed for {File}", fileName);
                    failed.Add($"{fileName} (error: {ex.Message})");
                }
            }
        }

        return failed;
    }
}
