using System.IO;
using System.Text.Json;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public sealed class SegmentedStagedFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-staged-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, "sub", name);

    private static byte[] Bytes(int n, int seed = 1)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task Fresh_CreatesDirectoryStagingAndSidecar_ThenCommitRenames()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000, "1000:20261014");
        var data = Bytes(1000);
        await using (var s = StagedFile.Open(f))
        {
            Assert.False(s.IsComplete);
            Assert.Equal(0, s.Done.TotalLength);
            Assert.True(File.Exists(f.LocalPath + ".gdpart"));
            Assert.Equal(1000, new FileInfo(f.LocalPath + ".gdpart").Length); // preallocated
            Assert.True(File.Exists(f.LocalPath + ".gdpart.json"));

            // Out of order, concurrent positional writes.
            await Task.WhenAll(
                s.WriteAsync(500, data.AsMemory(500, 500), default).AsTask(),
                s.WriteAsync(0, data.AsMemory(0, 500), default).AsTask());
            s.MarkDone(new ByteRange(500, 1000));
            s.MarkDone(new ByteRange(0, 500));
            await s.CommitAsync();
        }

        Assert.Equal(data, File.ReadAllBytes(f.LocalPath));
        Assert.False(File.Exists(f.LocalPath + ".gdpart"));
        Assert.False(File.Exists(f.LocalPath + ".gdpart.json"));
    }

    [Fact]
    public async Task Resume_LoadsSavedRanges()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000, "v1");
        var data = Bytes(1000);
        await using (var s = StagedFile.Open(f))
        {
            await s.WriteAsync(0, data.AsMemory(0, 300), default);
            s.MarkDone(new ByteRange(0, 300));
            await s.WriteAsync(600, data.AsMemory(600, 100), default);
            s.MarkDone(new ByteRange(600, 700));
        } // dispose flushes without committing

        var json = JsonDocument.Parse(File.ReadAllText(f.LocalPath + ".gdpart.json")).RootElement;
        Assert.Equal(1000, json.GetProperty("size").GetInt64());
        Assert.Equal("v1", json.GetProperty("validator").GetString());
        Assert.Equal(2, json.GetProperty("ranges").GetArrayLength());

        await using (var s = StagedFile.Open(f))
        {
            Assert.False(s.IsComplete);
            Assert.Equal([new ByteRange(0, 300), new ByteRange(600, 700)], s.Done.Ranges);
            await s.WriteAsync(300, data.AsMemory(300, 300), default);
            await s.WriteAsync(700, data.AsMemory(700, 300), default);
            await s.CommitAsync();
        }
        Assert.Equal(data, File.ReadAllBytes(f.LocalPath));
    }

    [Theory]
    [InlineData(1000, "v2")]   // validator changed
    [InlineData(1200, "v1")]   // size changed
    public async Task Mismatch_RestartsFresh(long newSize, string newValidator)
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000, "v1");
        await using (var s = StagedFile.Open(f))
        {
            await s.WriteAsync(0, Bytes(500), default);
            s.MarkDone(new ByteRange(0, 500));
        }

        await using var again = StagedFile.Open(f with { Size = newSize, Validator = newValidator });
        Assert.Equal(0, again.Done.TotalLength);
        Assert.Equal(newSize, new FileInfo(f.LocalPath + ".gdpart").Length);
    }

    [Fact]
    public async Task UnreadableSidecar_RestartsFresh()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000);
        await using (var s = StagedFile.Open(f))
        {
            s.MarkDone(new ByteRange(0, 500));
        }
        File.WriteAllText(f.LocalPath + ".gdpart.json", "{ not json");
        await using var again = StagedFile.Open(f);
        Assert.Equal(0, again.Done.TotalLength);
    }

    [Fact]
    public async Task NullValidator_TrustsSizeAlone()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000, "v1");
        await using (var s = StagedFile.Open(f)) s.MarkDone(new ByteRange(0, 500));
        await using var again = StagedFile.Open(f with { Validator = null });
        Assert.Equal(500, again.Done.TotalLength);
    }

    [Fact]
    public async Task LegacyPartialFinalFile_IsAdoptedAsPrefix()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000, "v1");
        var data = Bytes(1000);
        Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
        File.WriteAllBytes(f.LocalPath, data[..400]);

        await using (var s = StagedFile.Open(f))
        {
            Assert.False(s.IsComplete);
            Assert.Equal([new ByteRange(0, 400)], s.Done.Ranges);
            Assert.False(File.Exists(f.LocalPath));
            Assert.True(File.Exists(f.LocalPath + ".gdpart"));
            await s.WriteAsync(400, data.AsMemory(400), default);
            s.MarkDone(new ByteRange(400, 1000));
            await s.CommitAsync();
        }
        Assert.Equal(data, File.ReadAllBytes(f.LocalPath));
    }

    [Fact]
    public async Task FinalFileAlreadyWhole_IsComplete()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 1000);
        var data = Bytes(1000);
        Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
        File.WriteAllBytes(f.LocalPath, data);

        await using (var s = StagedFile.Open(f))
        {
            Assert.True(s.IsComplete);
            Assert.Equal(1000, s.Done.TotalLength);
            await s.CommitAsync(); // no-op
        }
        Assert.Equal(data, File.ReadAllBytes(f.LocalPath));
        Assert.False(File.Exists(f.LocalPath + ".gdpart"));
        Assert.False(File.Exists(f.LocalPath + ".gdpart.json"));
    }

    [Fact]
    public async Task ZeroSize_CommitCreatesEmptyFile()
    {
        var f = new TransferFile("/r/empty.nfo", P("empty.nfo"), 0);
        await using (var s = StagedFile.Open(f)) await s.CommitAsync();
        Assert.True(File.Exists(f.LocalPath));
        Assert.Equal(0, new FileInfo(f.LocalPath).Length);
        Assert.False(File.Exists(f.LocalPath + ".gdpart"));
    }

    [Fact]
    public async Task UnknownSize_NeverResumesAndWritesNoSidecar()
    {
        var f = new TransferFile("/r/x.bin", P("x.bin"), -1);
        await using (var s = StagedFile.Open(f))
        {
            await s.WriteAsync(0, Bytes(100), default);
            s.MarkDone(new ByteRange(0, 100));
        }
        Assert.False(File.Exists(f.LocalPath + ".gdpart.json"));

        await using (var s = StagedFile.Open(f))
        {
            Assert.Equal(0, s.Done.TotalLength);
            Assert.Equal(0, new FileInfo(f.LocalPath + ".gdpart").Length);
            await s.WriteAsync(0, Bytes(50, 2), default);
            await s.CommitAsync();
        }
        Assert.Equal(Bytes(50, 2), File.ReadAllBytes(f.LocalPath));
    }

    [Fact]
    public async Task MarkDoneAfterCommit_DoesNotRecreateSidecar()
    {
        var f = new TransferFile("/r/a.rar", P("a.rar"), 10);
        await using var s = StagedFile.Open(f);
        await s.WriteAsync(0, Bytes(10), default);
        await s.CommitAsync();
        s.MarkDone(new ByteRange(0, 10));
        s.FlushState();
        Assert.False(File.Exists(f.LocalPath + ".gdpart.json"));
    }
}
