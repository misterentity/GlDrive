using System.Globalization;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class ExecutableExtensionsTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("Sample/keygen.com")]
    [InlineData(@"Sample\crack.bat")]
    [InlineData("install.msi")]
    [InlineData("thing.scr")]
    [InlineData("go.cmd")]
    [InlineData("x.vbs")]
    [InlineData("x.lnk")]
    public void ExecutableEntries_are_detected(string key) =>
        Assert.True(ExecutableExtensions.IsExecutable(key));

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("Subs/eng.srt")]
    [InlineData("release.nfo")]
    [InlineData("readme.exe.txt")]   // must NOT match — an EndsWith bug would
    [InlineData("execute.mkv")]
    [InlineData("disc.iso")]
    [InlineData("no_extension")]
    [InlineData("")]
    public void NonExecutableEntries_are_allowed(string key) =>
        Assert.False(ExecutableExtensions.IsExecutable(key));

    /// <summary>Windows resolves "evil.exe." to "evil.exe", so the trim must happen first.</summary>
    [Theory]
    [InlineData("evil.exe.")]
    [InlineData("evil.exe ")]
    [InlineData("evil.exe. . ")]
    public void TrailingDotsAndSpaces_are_trimmed_before_matching(string key) =>
        Assert.True(ExecutableExtensions.IsExecutable(key));

    [Fact]
    public void Matching_survives_a_Turkish_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.True(ExecutableExtensions.IsExecutable("x.PIF"));
            Assert.True(ExecutableExtensions.IsExecutable("x.MSI"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

}
