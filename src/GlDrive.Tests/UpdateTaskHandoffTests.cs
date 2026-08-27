using System;
using System.IO;
using System.Text.Json;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The SYSTEM scheduled task replaces the interactive UAC prompt, so this hand-off file is
/// attacker-writable input to an elevated process. It is not the security boundary — the install
/// destination is pinned to the elevated process's own directory and the package is re-verified
/// against a compiled-in RSA key — but it should still refuse anything malformed, stale, or
/// pointed somewhere that is not a staging directory.
///
/// Background: auto-install came due at ~03:35 with nobody present, the UAC prompt timed out after
/// 120s, and ERROR_CANCELLED was recorded as a user decision — suppressing the release for 24h and
/// re-offering it at the same hour the next night.
/// </summary>
public sealed class UpdateTaskHandoffTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public UpdateTaskHandoffTests()
    {
        // Must contain a "Temp" segment to satisfy the staging-shape check.
        _dir = Path.Combine(Path.GetTempPath(), "gldrive-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "pending-update.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Creates a directory shaped like a real staging folder.</summary>
    private string StagingDir(bool complete = true)
    {
        var dir = Path.Combine(_dir, "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "update.zip"), "zip");
        File.WriteAllText(Path.Combine(dir, "checksums.sha256"), "hash");
        File.WriteAllText(Path.Combine(dir, "checksums.sha256.sig"), "sig");
        if (complete) File.WriteAllText(Path.Combine(dir, "asset-name.txt"), "GlDrive-v9.9.9-win-x64.zip");
        return dir;
    }

    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    // ---- accepts a well-formed record ------------------------------------------

    [Fact]
    public void Roundtrips_a_valid_handoff()
    {
        var pkg = StagingDir();
        UpdateTaskHandoff.Write(_path, new UpdateTaskHandoff(4242, pkg, "v9.9.9", Now.AddMinutes(-1)));

        var read = UpdateTaskHandoff.TryRead(_path, Now);

        Assert.NotNull(read);
        Assert.Equal(4242, read!.Pid);
        Assert.Equal(pkg, read.PackageDir);
        Assert.Equal("v9.9.9", read.Tag);
    }

    // ---- staleness and clock skew ----------------------------------------------

    [Fact]
    public void Rejects_a_stale_handoff()
    {
        var pkg = StagingDir();
        UpdateTaskHandoff.Write(_path,
            new UpdateTaskHandoff(1, pkg, "v9.9.9", Now - UpdateTaskHandoff.MaxAge - TimeSpan.FromMinutes(1)));

        Assert.Null(UpdateTaskHandoff.TryRead(_path, Now));
    }

    [Fact]
    public void Accepts_a_handoff_just_inside_the_age_limit()
    {
        // Guards against a check so strict it rejects everything, which would make the
        // "rejects stale" test pass for the wrong reason.
        var pkg = StagingDir();
        UpdateTaskHandoff.Write(_path,
            new UpdateTaskHandoff(1, pkg, "v9.9.9", Now - UpdateTaskHandoff.MaxAge + TimeSpan.FromMinutes(1)));

        Assert.NotNull(UpdateTaskHandoff.TryRead(_path, Now));
    }

    [Fact]
    public void Rejects_a_future_dated_handoff()
    {
        var pkg = StagingDir();
        UpdateTaskHandoff.Write(_path, new UpdateTaskHandoff(1, pkg, "v9.9.9", Now.AddHours(1)));

        Assert.Null(UpdateTaskHandoff.TryRead(_path, Now));
    }

    // ---- malformed input --------------------------------------------------------

    [Fact]
    public void Rejects_a_missing_file() =>
        Assert.Null(UpdateTaskHandoff.TryRead(Path.Combine(_dir, "nope.json"), Now));

    [Fact]
    public void Rejects_unparseable_json()
    {
        File.WriteAllText(_path, "{ this is not json");
        Assert.Null(UpdateTaskHandoff.TryRead(_path, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_nonpositive_pid(int pid)
    {
        var pkg = StagingDir();
        UpdateTaskHandoff.Write(_path, new UpdateTaskHandoff(pid, pkg, "v9.9.9", Now.AddMinutes(-1)));
        Assert.Null(UpdateTaskHandoff.TryRead(_path, Now));
    }

    [Fact]
    public void Rejects_an_empty_package_dir()
    {
        UpdateTaskHandoff.Write(_path, new UpdateTaskHandoff(1, "   ", "v9.9.9", Now.AddMinutes(-1)));
        Assert.Null(UpdateTaskHandoff.TryRead(_path, Now));
    }

    // ---- staging-directory shape ------------------------------------------------

    [Fact]
    public void Rejects_a_package_dir_with_no_Temp_segment()
    {
        var outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Windows", "System32");
        Assert.False(UpdateTaskHandoff.IsPlausiblePackageDir(outside));
    }

    /// <summary>
    /// A directory whose last segment is "Temp" is refused even when it is otherwise a perfectly
    /// well-formed staging folder, because the updater deletes the package directory recursively
    /// when it finishes and "delete the temp root" is not an outcome worth risking.
    ///
    /// The four package files are created here deliberately: an earlier version of this test
    /// passed the real temp root, which has no update.zip, so it was the missing-files check that
    /// rejected it and the guard under test could be deleted with the test still green.
    /// </summary>
    [Fact]
    public void Rejects_a_directory_named_Temp_even_when_fully_populated()
    {
        var tempNamed = Path.Combine(_dir, "Temp");
        Directory.CreateDirectory(tempNamed);
        foreach (var f in new[] { "update.zip", "checksums.sha256", "checksums.sha256.sig", "asset-name.txt" })
            File.WriteAllText(Path.Combine(tempNamed, f), "x");

        Assert.False(UpdateTaskHandoff.IsPlausiblePackageDir(tempNamed));
    }

    [Fact]
    public void Accepts_the_same_populated_directory_under_any_other_name()
    {
        // Control for the test above: proves the rejection is the NAME, not the contents.
        var normal = Path.Combine(_dir, "pkg-control");
        Directory.CreateDirectory(normal);
        foreach (var f in new[] { "update.zip", "checksums.sha256", "checksums.sha256.sig", "asset-name.txt" })
            File.WriteAllText(Path.Combine(normal, f), "x");

        Assert.True(UpdateTaskHandoff.IsPlausiblePackageDir(normal));
    }

    [Fact]
    public void Rejects_a_staging_dir_missing_a_package_file()
    {
        Assert.False(UpdateTaskHandoff.IsPlausiblePackageDir(StagingDir(complete: false)));
    }

    [Fact]
    public void Rejects_a_nonexistent_directory()
    {
        Assert.False(UpdateTaskHandoff.IsPlausiblePackageDir(
            Path.Combine(Path.GetTempPath(), "gldrive-does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Accepts_a_complete_staging_dir() =>
        Assert.True(UpdateTaskHandoff.IsPlausiblePackageDir(StagingDir()));

    // ---- clearing ---------------------------------------------------------------

    [Fact]
    public void Clear_removes_the_record_and_is_idempotent()
    {
        UpdateTaskHandoff.Write(_path, new UpdateTaskHandoff(1, StagingDir(), "v9.9.9", Now.AddMinutes(-1)));
        UpdateTaskHandoff.Clear(_path);
        Assert.False(File.Exists(_path));
        UpdateTaskHandoff.Clear(_path);   // must not throw
    }
}

/// <summary>
/// ShellExecute returns ERROR_CANCELLED (1223) for BOTH "user clicked No" and "prompt expired
/// unanswered". Only the first is a decision that should suppress a release for 24h. The two
/// observed failures sat at an invariant 131s and 132s — the 120s secure-desktop timeout plus
/// download — while a person answering takes seconds.
/// </summary>
public sealed class ElevationTimeoutAttributionTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(89)]
    public void Short_cancellations_are_real_declines(double seconds) =>
        Assert.False(UpdateChecker.ElevationLikelyTimedOut(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(90)]
    [InlineData(120)]   // the Windows secure-desktop default
    [InlineData(131)]   // v3.10.80, observed
    [InlineData(132)]   // v3.10.82, observed
    public void Long_cancellations_are_timeouts(double seconds) =>
        Assert.True(UpdateChecker.ElevationLikelyTimedOut(TimeSpan.FromSeconds(seconds)));
}
