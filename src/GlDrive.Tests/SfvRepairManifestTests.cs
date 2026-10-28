using System.IO;
using System.IO.Hashing;
using System.Text;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class SfvRepairManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfv-paths-" + Guid.NewGuid().ToString("N"));
    public SfvRepairManifestTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Same_basename_in_nested_manifests_identifies_only_the_bad_file()
    {
        var good = Encoding.UTF8.GetBytes("correct");
        var bad = Encoding.UTF8.GetBytes("corrupt");
        var expected = Crc32.HashToUInt32(good);
        var manifests = new List<string>();
        foreach (var disc in new[] { "CD1", "CD2" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, disc)).FullName;
            await File.WriteAllBytesAsync(Path.Combine(directory, "disc.rar"), disc == "CD1" ? good : bad);
            var manifest = Path.Combine(directory, "disc.sfv");
            await File.WriteAllTextAsync(manifest, $"disc.rar {expected:X8}\n");
            manifests.Add(manifest);
        }
        var result = await SfvVerifier.VerifyDetailedAsync(_root, manifests, true, CancellationToken.None);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SfvIssueKind.ChecksumMismatch, issue.Kind);
        Assert.Equal(Path.Combine(_root, "CD2", "disc.rar"), issue.LocalPath);
        Assert.Equal(expected, issue.ExpectedCrc);
        Assert.Equal(Crc32.HashToUInt32(bad), issue.ActualCrc);
        Assert.Equal(2, result.ReferencedFiles.Count);
    }

    [Fact]
    public async Task Repair_path_matches_the_tree_planners_filename_sanitization()
    {
        var manifest = Path.Combine(_root, "disc.sfv");
        await File.WriteAllTextAsync(manifest, "video?.rar 00000000\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "video.rar"), "bad");
        var result = await SfvVerifier.VerifyDetailedAsync(_root, [manifest], true, CancellationToken.None);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SfvIssueKind.ChecksumMismatch, issue.Kind);
        Assert.Equal(Path.Combine(_root, "video.rar"), issue.LocalPath);
    }

    [Theory]
    [InlineData("../outside.rar 00000000")]
    [InlineData("not a valid checksum")]
    [InlineData("missing.rar 00000000")]
    public async Task Non_crc_failures_never_supply_repair_authority(string entry)
    {
        var manifest = Path.Combine(_root, "disc.sfv");
        await File.WriteAllTextAsync(manifest, entry + "\n");
        var result = await SfvVerifier.VerifyDetailedAsync(_root, [manifest], true, CancellationToken.None);
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue =>
        {
            Assert.NotEqual(SfvIssueKind.ChecksumMismatch, issue.Kind);
            Assert.Null(issue.LocalPath);
            Assert.Null(issue.ExpectedCrc);
            Assert.Null(issue.ActualCrc);
        });
    }
}
