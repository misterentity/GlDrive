using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceLifetimePolicyTests
{
    private static readonly DateTime Now = new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Actual_progress_extends_idle_budget_but_not_absolute_cap()
    {
        Assert.Equal(RaceStopReason.None, Reason(TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(5)));
        Assert.Equal(RaceStopReason.Stall, Reason(TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(20)));
        Assert.Equal(RaceStopReason.Ceiling, Reason(TimeSpan.FromMinutes(90), TimeSpan.Zero));
        // Elapsed durations come from Stopwatch, independent of wall-clock rollback.
        Assert.Equal(RaceStopReason.Ceiling, RaceLifetimePolicy.TimeoutReason(TimeSpan.FromMinutes(90),
            TimeSpan.Zero, null, Now.AddDays(-1), 1200, 90, 10));
    }

    private static RaceStopReason Reason(TimeSpan elapsed, TimeSpan idle) =>
        RaceLifetimePolicy.TimeoutReason(elapsed, idle, null, Now, 1200, 90, 10);

    [Fact]
    public void First_upload_deadline_survives_attempts_and_new_progress()
    {
        Assert.Equal(RaceStopReason.Deadline, RaceLifetimePolicy.TimeoutReason(TimeSpan.FromSeconds(1),
            TimeSpan.Zero, Now.AddHours(-10), Now, 1200, 90, 10));
        Assert.False(RaceLifetimePolicy.CanResume(RaceStopReason.Stall, 0, Now, Now));
    }

    [Fact]
    public void Invalid_manual_values_get_bounded_safe_runtime_defaults()
    {
        Assert.Equal(RaceStopReason.Ceiling, RaceLifetimePolicy.TimeoutReason(TimeSpan.FromHours(24),
            TimeSpan.Zero, null, Now, 1200, int.MaxValue, int.MaxValue));
        var partial = new RacePartialState { FirstUploadAtUtc = Now };
        Assert.Equal(Now.AddHours(11), RaceLifetimePolicy.Deadline(partial, int.MaxValue));
        Assert.Equal(Now.AddHours(10), RaceLifetimePolicy.Deadline(partial, 0));
        Assert.Equal(DateTime.MaxValue, RaceLifetimePolicy.Deadline(partial with { FirstUploadAtUtc = DateTime.MaxValue }, 10));
    }

    [Theory]
    [InlineData(RaceStopReason.Ceiling, 0, true)]
    [InlineData(RaceStopReason.Stall, 2, true)]
    [InlineData(RaceStopReason.Stall, 3, false)]
    [InlineData(RaceStopReason.UserStop, 0, false)]
    [InlineData(RaceStopReason.AppShutdown, 0, false)]
    [InlineData(RaceStopReason.SourceGone, 0, false)]
    public void Only_three_automatic_partial_resumes(RaceStopReason reason, int resumes, bool expected) =>
        Assert.Equal(expected, RaceLifetimePolicy.CanResume(reason, resumes, Now.AddHours(1), Now));

    [Theory]
    [InlineData("/", "")]
    [InlineData("///", "")]
    [InlineData("/section/.meta", ".meta")]
    [InlineData("/section/../Release", "Release")]
    [InlineData("/section/Release*", "Release*")]
    [InlineData("/section*/Release", "Release")]
    [InlineData("/section name/Release", "Release")]
    [InlineData("/section/Release Name", "Release Name")]
    [InlineData("/section/[Release]", "[Release]")]
    [InlineData("/section/{Release}", "{Release}")]
    [InlineData("/section/Release?", "Release?")]
    [InlineData("/section/Release\r\nSITE WIPE /", "Release")]
    public void Unsafe_cleanup_roots_are_rejected(string path, string release) =>
        Assert.False(RaceLifetimePolicy.SafeRoot(path, release));

    [Fact]
    public void Exact_release_root_is_safe_and_account_identity_is_bound()
    {
        Assert.True(RaceLifetimePolicy.SafeRoot("/video/Example.2026.1080p-GROUP", "Example.2026.1080p-GROUP"));
        var connection = new ConnectionConfig { Host = "example.invalid", Port = 21, Username = "user" };
        var directory = new OwnedRaceDirectory { AccountIdentity = RaceLifetimePolicy.AccountIdentity(connection) };
        Assert.True(RaceLifetimePolicy.SameAccount(directory, connection));
        Assert.False(RaceLifetimePolicy.SameAccount(directory, new() { Host = "other.invalid", Port = 21, Username = "user" }));
        Assert.False(RaceLifetimePolicy.SameAccount(directory, new() { Host = "example.invalid", Port = 22, Username = "user" }));
        Assert.False(RaceLifetimePolicy.SameAccount(directory, new() { Host = "example.invalid", Port = 21, Username = "other" }));
        Assert.False(RaceLifetimePolicy.SameAccount(directory with { AccountIdentity = "" }, connection));
    }

    [Fact]
    public void Cleanup_requires_own_file_identity_and_preserves_complete_or_shared_content()
    {
        var directory = new OwnedRaceDirectory
        {
            RemotePath = "/video/Release", AttemptedFiles = new() { ["a.rar"] = 100 }
        };
        var state = new RacePartialState { ExpectedCount = 2, ExpectedFiles = new() { ["a.rar"] = 100, ["b.rar"] = 100 } };
        var ownPartial = new Dictionary<string, long> { ["a.rar"] = 50 };
        Assert.Equal(PartialCleanupVerdict.Wipe, RaceLifetimePolicy.CleanupVerdict("Release", directory, ownPartial, false, true, state, false));
        Assert.Equal(PartialCleanupVerdict.Shared, RaceLifetimePolicy.CleanupVerdict("Release", directory,
            new Dictionary<string, long> { ["foreign.rar"] = 100 }, false, false, state, false));
        Assert.Equal(PartialCleanupVerdict.Shared, RaceLifetimePolicy.CleanupVerdict("Release", directory,
            new Dictionary<string, long> { ["a.rar"] = 101 }, false, false, state, false));
        Assert.Equal(PartialCleanupVerdict.Shared, RaceLifetimePolicy.CleanupVerdict("Release", directory, ownPartial, false, true, state, true));
        Assert.Equal(PartialCleanupVerdict.Complete, RaceLifetimePolicy.CleanupVerdict("Release", directory, ownPartial, true, true, state, false));
        Assert.Equal(PartialCleanupVerdict.Complete, RaceLifetimePolicy.CleanupVerdict("Release",
            directory with { CompletionConfirmed = true }, ownPartial, false, true,
            state with { ExpectedCount = 1000 }, false));
    }

    [Fact]
    public void Without_sfv_inventory_or_missing_stub_cleanup_never_wipes()
    {
        var files = new Dictionary<string, long> { ["a.mkv"] = 100 };
        var directory = new OwnedRaceDirectory { RemotePath = "/video/Release", AttemptedFiles = files };
        var noInventory = new RacePartialState();
        Assert.Equal(PartialCleanupVerdict.Unproven,
            RaceLifetimePolicy.CleanupVerdict("Release", directory, files, false, false, noInventory, false));
        // A missing-part stub is positive evidence, so the owned partial may still be wiped.
        Assert.Equal(PartialCleanupVerdict.Wipe,
            RaceLifetimePolicy.CleanupVerdict("Release", directory, files, false, true, noInventory, false));
    }

    [Fact]
    public void Complete_size_and_inventory_heuristic_never_wipes_finished_release()
    {
        var files = new Dictionary<string, long> { ["a.rar"] = 100 };
        var directory = new OwnedRaceDirectory { RemotePath = "/video/Release", AttemptedFiles = files };
        var state = new RacePartialState { ExpectedCount = 1, ExpectedFiles = files };
        Assert.Equal(PartialCleanupVerdict.Complete, RaceLifetimePolicy.CleanupVerdict("Release", directory, files, false, false, state, false));
        Assert.Equal(PartialCleanupVerdict.Wipe, RaceLifetimePolicy.CleanupVerdict("Release", directory, files, false, true, state, false));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("CD1", false)]
    [InlineData("CD1/Disc1", false)]
    public void Nested_complete_disc_does_not_prove_whole_release_complete(string relativeDirectory, bool expected)
    {
        var marker = RaceLifetimePolicy.IsWholeReleaseCompletionMarker(relativeDirectory, "[ COMPLETE ]", ["[ COMPLETE ]"]);
        Assert.Equal(expected, marker);
        var owned = new OwnedRaceDirectory { RemotePath = "/video/Release", AttemptedFiles = new() { ["CD1/a.rar"] = 100 } };
        var files = new Dictionary<string, long> { ["CD1/a.rar"] = 100 };
        var partial = new RacePartialState { ExpectedCount = 2, ExpectedFiles = new() { ["CD1/a.rar"] = 100, ["CD2/b.rar"] = 100 } };
        Assert.Equal(expected ? PartialCleanupVerdict.Complete : PartialCleanupVerdict.Wipe,
            RaceLifetimePolicy.CleanupVerdict("Release", owned, files, marker, true, partial, false));
    }

    [Theory]
    [InlineData("[ Incomplete ]")]
    [InlineData("[###::::] - 25% Complete - [site]")]
    [InlineData("-MISSING-b.rar")]
    public void Incomplete_markers_prevent_count_only_completion(string marker)
    {
        var missing = RaceLifetimePolicy.IsIncompleteReleaseMarker(marker, 0);
        Assert.True(missing);
        var files = new Dictionary<string, long> { ["a.rar"] = 100 };
        Assert.Equal(PartialCleanupVerdict.Wipe, RaceLifetimePolicy.CleanupVerdict("Release",
            new() { RemotePath = "/video/Release", AttemptedFiles = files }, files, false, missing,
            new() { ExpectedCount = 1, ExpectedFiles = files }, false));
    }

    [Fact]
    public void Fresh_races_outrank_resumes_and_expired_cleanup_gets_a_turn()
    {
        var fresh = Request();
        var resume = fresh with { ResumeCount = 1 };
        var cleanup = fresh with { CleanupOnly = true };
        Assert.Equal([cleanup, fresh, resume], new[] { resume, cleanup, fresh }.OrderBy(RaceLifetimePolicy.QueuePriority));
    }

    [Fact]
    public void Cleanup_retries_are_bounded_and_exhaustion_remains_visible()
    {
        var request = Request() with { CleanupOnly = true };
        for (var i = 1; i <= RaceLifetimePolicy.MaxCleanupAttempts; i++)
        {
            request = RaceLifetimePolicy.RetryCleanup(request, Now);
            Assert.Equal(i, request.CleanupAttempts);
            if (i < RaceLifetimePolicy.MaxCleanupAttempts) Assert.Equal(Now.AddMinutes(5), request.NextProbeAtUtc);
        }
        Assert.False(request.IsDue(Now.AddYears(1)));
        Assert.True(request.CleanupOnly);
        Assert.Contains("manual attention", request.WaitReason);
    }

    [Fact]
    public void Direct_upload_progress_requires_growth_in_the_current_attempt()
    {
        var attempt = new UploadListingProgress();
        Assert.Equal(0, attempt.Observe(500)); // May be an old partial.
        Assert.Equal(0, attempt.Observe(500));
        Assert.Equal(25, attempt.Observe(525));
        Assert.Equal(0, attempt.Observe(-1)); // Unknown listing size is not movement.
        Assert.Equal(25, attempt.Observe(550));
        Assert.Equal(0, attempt.Observe(0)); // Truncation is not forward progress.
        Assert.Equal(10, attempt.Observe(10));
        var nextAttempt = new UploadListingProgress();
        Assert.Equal(0, nextAttempt.Observe(600)); // Never inherit the previous attempt's baseline.
        Assert.Equal(0, nextAttempt.Observe(600));
        Assert.Equal(10, nextAttempt.Observe(610));
    }

    [Fact]
    public void Listing_from_old_attempt_or_before_stor_cannot_prime_new_attempt()
    {
        var oldAttempt = new UploadListingProgress();
        var current = new UploadListingProgress();
        Assert.Equal(0, current.ObserveForAttempt(500, oldAttempt));
        Assert.Equal(0, current.ObserveForAttempt(600, null));
        Assert.Equal(0, current.ObserveForAttempt(700, current)); // Still its first valid observation.
        Assert.Equal(0, current.ObserveForAttempt(900, oldAttempt));
        Assert.Equal(50, current.ObserveForAttempt(750, current));
    }

    internal static PersistedRace Request() => new("video", "Release", ["source", "dest"], SpreadMode.Race,
        "source", "/video/Release", Now);
}
