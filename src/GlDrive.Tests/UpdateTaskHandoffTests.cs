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

    [Fact]
    public void System_task_uses_publisher_verification_not_current_user_DPAPI_authorization()
    {
        Assert.False(UpdateChecker.RequiresUpdateAuthorization(viaScheduledTask: true));
        Assert.True(UpdateChecker.RequiresUpdateAuthorization(viaScheduledTask: false));
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
/// The "already attempted this session" latch stops a failed launch re-downloading the package on
/// every poll. But a swallowed elevation failure returns NORMALLY, so nothing clears the latch —
/// and because it is checked BEFORE the persisted markers, it makes their expiry unreachable and
/// wedges the version for the entire process lifetime.
///
/// Observed in production 2026-08-27: v3.10.87's prompt expired at 20:02, and every later poll
/// logged "already attempted this session" while the warning one frame up promised "will retry".
/// The release condition read only the DECLINE marker, and v3.10.84 had deliberately stopped the
/// timeout path from writing one — so the new path could never reach it.
///
/// The rule: release the latch for every outcome that writes a persisted marker.
/// </summary>
public sealed class AttemptLatchReleaseTests
{
    private const string Tag = "v3.10.87";

    [Fact]
    public void Released_after_a_decline()
    {
        Assert.True(UpdateChecker.ShouldReleaseAttemptLatch(Tag, null,
            wasDeclined: true, wasTimedOut: false));
    }

    [Fact]
    public void Released_after_a_timeout()
    {
        // The regression: this returned false, so the timeout path wedged the version.
        Assert.True(UpdateChecker.ShouldReleaseAttemptLatch(Tag, null,
            wasDeclined: false, wasTimedOut: true));
    }

    [Fact]
    public void Not_released_when_neither_marker_is_set()
    {
        // Fail-safe: an unreadable/absent marker layer keeps the quiet, over-suppressing
        // behaviour rather than turning into a prompt on every poll.
        Assert.False(UpdateChecker.ShouldReleaseAttemptLatch(Tag, null,
            wasDeclined: false, wasTimedOut: false));
    }

    [Fact]
    public void Released_at_most_once_per_tag_per_process()
    {
        Assert.False(UpdateChecker.ShouldReleaseAttemptLatch(Tag, alreadyReleasedTag: Tag,
            wasDeclined: true, wasTimedOut: true));
    }

    [Fact]
    public void A_different_tag_may_still_be_released()
    {
        Assert.True(UpdateChecker.ShouldReleaseAttemptLatch("v3.10.88", alreadyReleasedTag: Tag,
            wasDeclined: false, wasTimedOut: true));
    }
}

/// <summary>
/// An unanswered elevation prompt must still apply SOME brake.
///
/// The 24h decline window re-offers at the same hour that just failed, which is how two releases
/// running had to be installed by hand — so the timeout path deliberately does not write it. But
/// that path also drops the .update-attempt marker, so without a brake of its own every 3h poll
/// would re-download the ~154 MB package and re-prompt forever. Four hours retries the same day,
/// in working hours, at a cost of one or two re-downloads instead of eight.
/// </summary>
public sealed class ElevationTimeoutBrakeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _marker;

    public ElevationTimeoutBrakeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gldrive-brake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _marker = Path.Combine(_dir, ".update-timeout");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Timeout_brake_is_much_shorter_than_the_decline_window()
    {
        // The whole point: a 24h suppression lands on the same bad hour tomorrow.
        Assert.True(UpdateChecker.TimeoutSuppressionWindow < UpdateChecker.DeclineSuppressionWindow);
        Assert.True(UpdateChecker.TimeoutSuppressionWindow > TimeSpan.Zero,
            "a zero window means re-downloading ~154 MB on every poll");
        Assert.True(UpdateChecker.TimeoutSuppressionWindow < TimeSpan.FromHours(12),
            "must retry within the same day so a daytime prompt gets a chance");
    }

    [Fact]
    public void Suppresses_within_the_timeout_window()
    {
        UpdateChecker.RecordDeclinedUpdateAt(_marker, "v9.9.9", Now.AddHours(-1));
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(_marker, "v9.9.9", Now,
            UpdateChecker.TimeoutSuppressionWindow));
    }

    [Fact]
    public void Retries_once_the_timeout_window_lapses()
    {
        UpdateChecker.RecordDeclinedUpdateAt(_marker, "v9.9.9",
            Now - UpdateChecker.TimeoutSuppressionWindow - TimeSpan.FromMinutes(1));
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(_marker, "v9.9.9", Now,
            UpdateChecker.TimeoutSuppressionWindow));
    }

    [Fact]
    public void A_stamp_older_than_the_timeout_window_but_inside_24h_still_retries()
    {
        // Guards the actual regression: reusing the 24h default here would keep suppressing.
        UpdateChecker.RecordDeclinedUpdateAt(_marker, "v9.9.9", Now.AddHours(-6));
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(_marker, "v9.9.9", Now,
            UpdateChecker.TimeoutSuppressionWindow));
        Assert.True(UpdateChecker.WasUpdateDeclinedAt(_marker, "v9.9.9", Now));   // default 24h
    }

    [Fact]
    public void A_different_tag_is_not_suppressed()
    {
        UpdateChecker.RecordDeclinedUpdateAt(_marker, "v9.9.9", Now.AddHours(-1));
        Assert.False(UpdateChecker.WasUpdateDeclinedAt(_marker, "v9.9.10", Now,
            UpdateChecker.TimeoutSuppressionWindow));
    }
}

/// <summary>
/// The scheduled task only ever exists if register-update-task.ps1 actually reaches the installed
/// directory. It originally shipped ONLY through the Inno installer, so a box updated from the
/// release zip never registered the task and silently kept using the UAC prompt the task exists to
/// remove — the feature would have been inert on the one path that matters most.
///
/// It is now published with the app, and the elevated updater runs it after a successful install.
/// This guards the shipping half: if the script stops being copied, the feature dies quietly, and
/// nothing else in the suite would notice.
/// </summary>
public sealed class UpdateTaskScriptShipsTests
{
    [Fact]
    public void Registration_script_is_published_next_to_the_app()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "register-update-task.ps1");
        Assert.True(File.Exists(path),
            $"register-update-task.ps1 must ship with the app (looked in {AppContext.BaseDirectory}). " +
            "Without it, zip auto-updates never register the SYSTEM update task.");
    }

    [Fact]
    public void Registration_script_registers_the_task_the_app_looks_for()
    {
        // The script's default -TaskName and the name the app queries must not drift apart:
        // if they do, the app falls back to UAC forever while the task sits there unused.
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "register-update-task.ps1"));
        Assert.Contains($"'{UpdateTaskHandoff.TaskName}'", script);
        Assert.Contains("--apply-update-task", script);
        Assert.Contains($"'{UpdateTaskHandoff.CleanupTaskName}'", script);
        Assert.Contains("--cleanup-old-updates", script);
    }
}

public sealed class OldUpdateCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gldrive-old-{Guid.NewGuid():N}");

    public OldUpdateCleanupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Deletes_only_old_files_recursively_from_the_fixed_root()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        var rootOld = Path.Combine(_root, "one.dll.old");
        var nestedOld = Path.Combine(nested, "two.exe.old");
        var keep = Path.Combine(nested, "keep.dll");
        File.WriteAllText(rootOld, "old");
        File.WriteAllText(nestedOld, "old");
        File.WriteAllText(keep, "current");

        var result = UpdateChecker.DeleteOldUpdateFiles(_root);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Remaining);
        Assert.False(File.Exists(rootOld));
        Assert.False(File.Exists(nestedOld));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Retries_files_that_are_temporarily_locked_by_the_exiting_updater()
    {
        var locked = Path.Combine(_root, "runtime.dll.old");
        File.WriteAllText(locked, "old");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(100);
            handle.Dispose();
        });

        var result = UpdateChecker.DeleteOldUpdateFilesWithRetry(
            _root, TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(25));
        await release;

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Remaining);
        Assert.False(File.Exists(locked));
    }

    [Fact]
    public void Headless_cleanup_never_accepts_a_caller_supplied_directory()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "GlDrive", "Services", "UpdateChecker.cs"));
        var entry = source.IndexOf("public static int CleanupOldUpdateFilesFromTask()", StringComparison.Ordinal);
        var end = source.IndexOf("public void StartPeriodicCheck", entry, StringComparison.Ordinal);
        var method = source[entry..end];

        Assert.Contains("AppContext.BaseDirectory", method);
        Assert.Contains("initialDelay: TimeSpan.FromSeconds(10)", method);
        Assert.Contains("retryWindow: TimeSpan.FromSeconds(30)", method);
        Assert.DoesNotContain("string baseDir", method);
        Assert.DoesNotContain("args", method);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "src", "GlDrive", "GlDrive.csproj"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate repository root");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
