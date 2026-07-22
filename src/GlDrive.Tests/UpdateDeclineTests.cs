using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the UAC nag loop: declining the elevation prompt was logged as an
/// [ERR] and only suppressed in memory, so every app restart re-downloaded the package and
/// re-prompted (observed 3x across 2026-07-20..21).
/// </summary>
public sealed class UpdateDeclineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gldrive-decline-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateDeclineTests() => Directory.CreateDirectory(_dir);

    private string Marker => Path.Combine(_dir, ".update-declined");

    [Fact]
    public void NoMarker_MeansNotDeclined() =>
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33"));

    [Fact]
    public void DeclinedTag_IsSuppressed()
    {
        File.WriteAllText(Marker, "v3.10.33");
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33"));
    }

    [Fact]
    public void NewerRelease_ResumesAutoInstall()
    {
        // The decline must not become a permanent opt-out of all future updates.
        File.WriteAllText(Marker, "v3.10.33");
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.34"));
    }

    [Fact]
    public void TrailingWhitespace_IsTolerated()
    {
        File.WriteAllText(Marker, "v3.10.33\r\n");
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33"));
    }

    [Fact]
    public void UnreadableMarker_FailsOpen()
    {
        // A directory where the file should be: reading throws, and the safe default is to
        // allow the update rather than silently wedge auto-install off forever.
        Directory.CreateDirectory(Marker);
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
