using System.IO;
using System.Reflection;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class VolumeSetReadinessIntegrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gldrive-volume-gate-").FullName;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualGate_HoldsIncompleteSet_ThenReadiesAfterMissingPartArrives(bool modern, bool sfv)
    {
        var first = Touch(modern ? "movie.part01.rar" : "movie.rar");
        var missing = modern ? "movie.part02.rar" : "movie.r00";
        if (sfv)
            File.WriteAllText(Path.Combine(_directory, "movie.sfv"), $"{missing}\t12345678\n");
        else
            Touch(modern ? "movie.part03.rar" : "movie.r01");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiting = Invoke<Task<ArchiveWaitOutcome>>("WaitForVolumeSetReady", first, cancellation.Token, 25_000);
        try
        {
            // The first volume settles at 6s and the set is sampled at 8s. A bypassed
            // gate would already have returned Ready while the missing file is absent.
            await Task.Delay(TimeSpan.FromMilliseconds(8500), cancellation.Token);
            Assert.False(waiting.IsCompleted, "The gate accepted an incomplete, quiet archive set.");
            Touch(missing);
            Assert.Equal(ArchiveWaitOutcome.Ready, await waiting.WaitAsync(TimeSpan.FromSeconds(12)));
        }
        finally
        {
            cancellation.Cancel();
            try { await waiting; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sampler_IncludesWriteLockedLaterParts_AndExcludesPrefixSiblingSets(bool modern)
    {
        var first = Touch(modern ? "movie.part01.rar" : "movie.rar");
        var next = Touch(modern ? "movie.part02.rar" : "movie.r00");
        Touch(modern ? "movie.extras.part03.rar" : "movie.extras.r01");
        Touch("movie.sfv");

        using (var writer = new FileStream(next, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var locked = Invoke<VolumeSetReadiness.Snapshot>("SampleVolumeSet", first);
            Assert.Equal(2, locked.Count);
            Assert.Equal(1, locked.LockedCount);
            Assert.False(VolumeSetReadiness.IsReady(locked, locked));
        }

        var settled = Invoke<VolumeSetReadiness.Snapshot>("SampleVolumeSet", first);
        Assert.Equal(new VolumeSetReadiness.Snapshot(2, 2, 0, 0), settled);
        Assert.True(VolumeSetReadiness.IsReady(settled, settled));
    }

    [Fact]
    public void ModernFingerprint_ChangesWhenAnotherPartArrives()
    {
        var first = Touch("movie.part01.rar");
        var before = Invoke<(int, long)>("ComputeVolumeSetFingerprint", first);
        Touch("movie.part02.rar");
        var after = Invoke<(int, long)>("ComputeVolumeSetFingerprint", first);

        Assert.Equal((1, 1L), before);
        Assert.Equal((2, 2L), after);
    }

    [Fact]
    public void OldStyleArchiveOpening_UsesOnlyTheSampledSet_InNumericOrder()
    {
        var first = Touch("movie.rar");
        Touch("movie.r02");
        Touch("movie.r00");
        Touch("movie.r01");
        Touch("movie.extras.r00");

        var volumes = Invoke<List<FileInfo>>("TryDiscoverRarVolumes", first);
        Assert.Equal(new[] { "movie.rar", "movie.r00", "movie.r01", "movie.r02" },
            volumes.Select(v => v.Name));
        Assert.Null(Invoke<List<FileInfo>?>("TryDiscoverRarVolumes", Touch("movie.part01.rar")));
    }

    [Fact]
    public void Sampler_UnobservableDirectory_IsNeverReady()
    {
        var path = Path.Combine(_directory, "absent", "movie.rar");
        var snapshot = Invoke<VolumeSetReadiness.Snapshot>("SampleVolumeSet", path);
        Assert.False(VolumeSetReadiness.IsReady(snapshot, snapshot));
    }

    private static T Invoke<T>(string method, params object[] arguments) =>
        (T)typeof(ExtractorWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments)!;

    private string Touch(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
