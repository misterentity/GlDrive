using System.Globalization;
using System.IO;
using System.IO.Hashing;

namespace GlDrive.Downloads;

internal enum SfvIssueKind { Missing, ChecksumMismatch, Error }
internal sealed record SfvIssue(string FileName, SfvIssueKind Kind, string Detail)
{
    internal string? LocalPath { get; init; }
    internal uint? ExpectedCrc { get; init; }
    internal uint? ActualCrc { get; init; }
    public override string ToString() => $"{FileName} ({Detail})";
}
internal sealed record SfvVerificationResult(IReadOnlyList<SfvIssue> Issues, IReadOnlySet<string> ReferencedFiles);

public static class SfvVerifier
{
    public static async Task<List<string>> VerifyAsync(string dirPath, CancellationToken ct) =>
        (await VerifyDetailedAsync(dirPath, Directory.GetFiles(dirPath, "*.sfv"), true, ct))
        .Issues.Select(issue => issue.ToString()).ToList();

    internal static async Task<SfvVerificationResult> VerifyDetailedAsync(string releaseRoot,
        IReadOnlyList<string> manifests, bool verifyChecksums, CancellationToken ct)
    {
        var issues = new List<SfvIssue>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(releaseRoot);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var buffer = new byte[65536];
        foreach (var sfvFile in manifests)
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(sfvFile, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new SfvIssue(sfvFile, SfvIssueKind.Error, $"cannot read manifest: {ex.Message}"));
                continue;
            }
            var entries = 0;
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;
                var lastSpace = trimmed.LastIndexOfAny([' ', '\t']);
                if (lastSpace <= 0 || !uint.TryParse(trimmed[(lastSpace + 1)..],
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expectedCrc))
                {
                    issues.Add(new SfvIssue(sfvFile, SfvIssueKind.Error, "invalid SFV entry"));
                    continue;
                }
                entries++;
                var fileName = trimmed[..lastSpace].Trim();
                string filePath;
                try
                {
                    var relative = fileName.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative)) throw new IOException("absolute filename in manifest");
                    var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sfvFile)!, relative));
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("filename escapes release directory");
                    // Match the tree planner's Windows filename sanitization.
                    filePath = Path.Combine(root, Path.Combine(Path.GetRelativePath(root, full)
                        .Split(Path.DirectorySeparatorChar).Select(PathSanitizer.Sanitize).ToArray()));
                    referenced.Add(filePath);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    issues.Add(new SfvIssue(fileName, SfvIssueKind.Error, ex.Message));
                    continue;
                }
                if (!File.Exists(filePath))
                {
                    issues.Add(new SfvIssue(fileName, SfvIssueKind.Missing, "missing"));
                    continue;
                }
                if (!verifyChecksums) continue;
                try
                {
                    var crc = new Crc32();
                    await using var fs = File.OpenRead(filePath);
                    int bytesRead;
                    while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(), ct)) > 0)
                        crc.Append(buffer.AsSpan(0, bytesRead));
                    var hash = crc.GetCurrentHashAsUInt32();
                    if (hash != expectedCrc)
                        issues.Add(new SfvIssue(fileName, SfvIssueKind.ChecksumMismatch,
                            $"CRC mismatch: expected {expectedCrc:X8}, got {hash:X8}")
                        { LocalPath = filePath, ExpectedCrc = expectedCrc, ActualCrc = hash });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new SfvIssue(fileName, SfvIssueKind.Error, $"read error: {ex.Message}"));
                }
            }
            if (entries == 0 && !issues.Any(i => i.FileName == sfvFile))
                issues.Add(new SfvIssue(sfvFile, SfvIssueKind.Missing, "manifest has no file entries yet"));
        }
        return new SfvVerificationResult(issues, referenced);
    }
}
