using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the UAC decline path.
///
/// v3.10.33 fixed a nag loop: declining the elevation prompt was logged as an [ERR] and only
/// suppressed in memory, so every app restart re-downloaded the package and re-prompted
/// (observed 3x across 2026-07-20..21).
///
/// v3.10.41 fixes that fix's over-correction: the suppression was PERMANENT for the declined
/// tag and completely silent, so one dismissed prompt stranded the app on an old build with no
/// diagnostic at all. Live proof: `.update-declined` = v3.10.40 written 2026-07-25 11:43, then
/// 18 consecutive "Update available: 3.10.39 -> 3.10.40" polls across 51h that logged nothing
/// further. A decline is now "not now" (bounded re-prompt window), never "never".
/// </summary>
public sealed class UpdateDeclineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gldrive-decline-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateDeclineTests() => Directory.CreateDirectory(_dir);

    private string Marker => Path.Combine(_dir, ".update-declined");

    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoMarker_MeansNotDeclined() =>
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", Now));

    [Fact]
    public void FreshDecline_IsSuppressed()
    {
        // Still inside the re-prompt window: honour the user's "not now".
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now);
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", Now.AddHours(1)));
    }

    [Fact]
    public void NewerRelease_ResumesAutoInstall()
    {
        // The decline must not become a permanent opt-out of all future updates.
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now);
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.34", Now));
    }

    [Fact]
    public void DeclineExpires_SoTheSameTagIsOfferedAgain()
    {
        // THE v3.10.41 BUG: this used to stay true forever, silently stranding the app.
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now);
        var afterWindow = Now + UpdateChecker.DeclineSuppressionWindow + TimeSpan.FromMinutes(1);
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", afterWindow));
    }

    [Fact]
    public void DeclineHoldsRightUpToTheWindowBoundary()
    {
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now);
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(
            Marker, "v3.10.33", Now + UpdateChecker.DeclineSuppressionWindow - TimeSpan.FromMinutes(1)));
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(
            Marker, "v3.10.33", Now + UpdateChecker.DeclineSuppressionWindow));
    }

    [Fact]
    public void LegacyTagOnlyMarker_IsTreatedAsExpired()
    {
        // Markers written before v3.10.41 carry no timestamp. Treating them as expired is what
        // self-heals an already-stranded install (this box: .update-declined = "v3.10.40") on
        // the first poll after upgrading, instead of requiring a manual file delete.
        File.WriteAllText(Marker, "v3.10.40");
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.40", Now));
    }

    [Fact]
    public void FutureDatedDecline_IsRejectedNotTrusted()
    {
        // Clock skew must not push the re-prompt out indefinitely — same guard the deferral
        // marker uses.
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now.AddDays(5));
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", Now));
    }

    [Fact]
    public void TrailingWhitespace_IsTolerated()
    {
        File.WriteAllText(Marker, $"v3.10.33\t{Now:O}\r\n");
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", Now.AddHours(1)));
    }

    [Fact]
    public void UnreadableMarker_FailsOpen()
    {
        // A directory where the file should be: reading throws, and the safe default is to
        // allow the update rather than silently wedge auto-install off forever.
        Directory.CreateDirectory(Marker);
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(Marker, "v3.10.33", Now));
    }

    [Fact]
    public void RoundTrip_WritesATimestampedMarker()
    {
        UpdateChecker.RecordDeclinedUpdateAt(Marker, "v3.10.33", Now);
        var parts = File.ReadAllText(Marker).Trim().Split('\t');
        Assert.Equal(2, parts.Length);
        Assert.Equal("v3.10.33", parts[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
