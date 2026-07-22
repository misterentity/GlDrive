using System.IO;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-07-21 watch-folder retry loop: an incomplete multi-volume
/// set was retried five times per cycle (re-reading tens of GB each pass) and the cycle
/// restarted on every subsequent watcher event.
/// </summary>
public sealed class ExtractFailureClassifierTests
{
    [Theory]
    // Verbatim SharpCompress text from gldrive-20260721.log.
    [InlineData("Multi-part rar file is incomplete.  Entry expects a new volume: movie.mkv")]
    [InlineData("Entry expects a new volume: something.mkv")]
    [InlineData("Cannot find volume release.r05")]
    [InlineData("Next volume is missing")]
    [InlineData("Unexpected end of archive")]
    public void Classify_IncompleteVolumeSet_IsPermanent(string message) =>
        Assert.Equal(ExtractFailureKind.Permanent, ExtractFailureClassifier.Classify(message));

    [Theory]
    [InlineData("The process cannot access the file because it is being used by another process.")]
    [InlineData("Access is denied.")]
    [InlineData("The process cannot access the file 'x.rar'")]
    public void Classify_LockedFile_StaysTransient(string message) =>
        Assert.Equal(ExtractFailureKind.Transient, ExtractFailureClassifier.Classify(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some brand new failure mode nobody has seen")]
    public void Classify_UnknownOrEmpty_DefaultsToTransient(string? message) =>
        Assert.Equal(ExtractFailureKind.Transient, ExtractFailureClassifier.Classify(message));

    [Fact]
    public void Classify_LockedWordingWins_WhenBothMarkersPresent()
    {
        // A still-copying volume can mention both; keeping its retries is the safer default.
        const string msg = "The process cannot access the file because it is being used " +
                           "by another process. Entry expects a new volume: movie.mkv";
        Assert.Equal(ExtractFailureKind.Transient, ExtractFailureClassifier.Classify(msg));
    }

    [Fact]
    public void Classify_Exception_WalksInnerExceptions()
    {
        var inner = new InvalidOperationException(
            "Multi-part rar file is incomplete.  Entry expects a new volume: movie.mkv");
        var outer = new Exception("Extraction failed", new Exception("wrapper", inner));

        Assert.Equal(ExtractFailureKind.Permanent, ExtractFailureClassifier.Classify(outer));
    }

    [Fact]
    public void Classify_Exception_WithNoPermanentMarker_IsTransient()
    {
        var ex = new IOException("The device is not ready");
        Assert.Equal(ExtractFailureKind.Transient, ExtractFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_NullException_IsTransient() =>
        Assert.Equal(ExtractFailureKind.Transient,
            ExtractFailureClassifier.Classify((Exception?)null));
}
