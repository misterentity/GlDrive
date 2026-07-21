using System.IO;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class ArchiveExtractorReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gldrive-extractor-tests-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractorReliabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DeleteArchiveSet_DoesNotDeletePrefixSibling()
    {
        var first = Touch("movie.rar");
        Touch("movie.r00");
        Touch("movie.sfv");
        var sibling = Touch("movie.extras.rar");

        Assert.True(ArchiveExtractor.DeleteArchiveSet(first));

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(Path.Combine(_root, "movie.r00")));
        Assert.False(File.Exists(Path.Combine(_root, "movie.sfv")));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void DeleteArchiveSet_LeavesUnextractedArchiveTypesAndNestedArchives()
    {
        var first = Touch("release.rar");
        Touch("release.r00");
        var zip = Touch("bonus.zip");
        var nestedDir = Path.Combine(_root, "extras");
        Directory.CreateDirectory(nestedDir);
        var nestedRar = Path.Combine(nestedDir, "nested.rar");
        File.WriteAllText(nestedRar, "nested");

        Assert.True(ArchiveExtractor.DeleteArchiveSet(first));

        Assert.True(File.Exists(zip));
        Assert.True(File.Exists(nestedRar));
        Assert.False(File.Exists(Path.Combine(_root, "release.rar")));
        Assert.False(File.Exists(Path.Combine(_root, "release.r00")));
    }

    [Fact]
    public void AtomicCopy_CancellationPreservesExistingDestination()
    {
        var destination = Path.Combine(_root, "output.bin");
        File.WriteAllText(destination, "known-good");
        using var cts = new CancellationTokenSource();
        using var source = new CancelAfterFirstReadStream(new byte[ArchiveFileOperations.BufferSize * 2], cts);

        Assert.Throws<OperationCanceledException>(() =>
            ArchiveFileOperations.CopyToFileAtomically(source, destination, cts.Token));

        Assert.Equal("known-good", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.gldrive-tmp"));
    }

    [Fact]
    public void AtomicCopy_SuccessfullyReplacesDestination()
    {
        var destination = Path.Combine(_root, "output.bin");
        File.WriteAllText(destination, "old");
        using var source = new MemoryStream("complete"u8.ToArray());

        ArchiveFileOperations.CopyToFileAtomically(source, destination, CancellationToken.None);

        Assert.Equal("complete", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.gldrive-tmp"));
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class CancelAfterFirstReadStream(byte[] data, CancellationTokenSource cts)
        : MemoryStream(data)
    {
        private bool _cancelled;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Math.Min(count, ArchiveFileOperations.BufferSize));
            if (!_cancelled)
            {
                _cancelled = true;
                cts.Cancel();
            }
            return read;
        }
    }
}
