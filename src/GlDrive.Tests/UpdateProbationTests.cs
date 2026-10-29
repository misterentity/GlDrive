using System.IO;
using System.Security.AccessControl;
using System.Text.Json.Nodes;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpdateProbationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-probation-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly Version _current = new(3, 19, 0);
    private readonly List<(string Original, string Backup)> _backups = [];
    private string Generation => Path.GetFileName(_backups[0].Backup)[^36..^4];
    private string ReceiptPath => Path.Combine(_root, UpdateProbation.MetadataDirectory, Generation + ".json");

    public UpdateProbationTests()
    {
        Directory.CreateDirectory(_root);
        WriteBuild("old");
        UpdateChecker.BackUpInstalledFiles(_root, _backups);
        WriteBuild("new");
        File.WriteAllText(Path.Combine(_root, "new-only.txt"), "new content");
    }

    private UpdateProbationStore Store(Action<string, string>? write = null, Func<string, bool>? trust = null) =>
        new(_root, _current, trust ?? (path => Path.GetFullPath(path).StartsWith(_root, StringComparison.OrdinalIgnoreCase)),
            path => File.ReadAllText(path).StartsWith("old", StringComparison.Ordinal) ? new Version(3, 18, 0) : _current,
            write);

    private void WriteBuild(string marker)
    {
        File.WriteAllText(Path.Combine(_root, "GlDrive.exe"), marker + " executable");
        File.WriteAllText(Path.Combine(_root, "GlDrive.dll"), marker + " assembly");
        File.WriteAllText(Path.Combine(_root, "GlDrive.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "GlDrive.deps.json"), """
            {"targets":{"runtime":{"GlDrive/version":{"runtime":{"GlDrive.dll":{},"lib/net10.0/dependency.dll":{}}}}}}
            """);
        File.WriteAllText(Path.Combine(_root, "dependency.dll"), marker + " dependency");
    }

    [Fact]
    public void Previous_generation_survives_for_full_twenty_four_hours_then_cleans_up()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        Assert.Equal(_now.AddHours(24), store.HoldUntil(_now));
        Assert.Equal((0, _backups.Count), store.Cleanup(_now.AddHours(23.99)));
        Assert.All(_backups, backup => Assert.True(File.Exists(backup.Backup)));
        var result = store.Cleanup(_now.AddHours(24));
        Assert.Equal((_backups.Count, 0), result);
        Assert.False(File.Exists(ReceiptPath));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
    }

    [Fact]
    public async Task Eligible_expired_generation_retries_a_temporary_file_lock()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        using var retained = new FileStream(_backups[0].Backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(100); retained.Dispose(); });
        var result = store.CleanupWithRetry(TimeSpan.Zero, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20), () => _now.AddHours(24));
        await release;
        Assert.Equal((_backups.Count, 0), result);
        Assert.All(_backups, backup => Assert.False(File.Exists(backup.Backup)));
    }

    [Fact]
    public void Expected_probation_retention_does_not_spend_the_cleanup_retry_window()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        var waits = new List<TimeSpan>();
        var result = store.CleanupWithRetry(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1), () => _now.AddHours(1), waits.Add);
        Assert.Equal((0, _backups.Count), result);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(waits));
        Assert.All(_backups, backup => Assert.True(File.Exists(backup.Backup)));
    }

    [Fact]
    public void Legacy_discovery_is_read_only_and_first_observation_starts_probation()
    {
        foreach (var backup in _backups) File.SetLastWriteTimeUtc(backup.Backup, _now.AddYears(-2));
        var store = Store();
        Assert.True(store.GetStatus(_now).RollbackAvailable);
        Assert.False(Directory.Exists(Path.Combine(_root, UpdateProbation.MetadataDirectory)));
        Assert.Null(store.HoldUntil(_now));
        store.ObserveLegacy(_now);
        Assert.Equal(_now.AddHours(24), store.HoldUntil(_now.AddHours(1)));
        store.ObserveLegacy(_now.AddHours(12));
        Assert.Equal(_now.AddHours(24), store.HoldUntil(_now.AddHours(13)));
    }

    [Fact]
    public void Readback_failure_retains_every_previous_file_and_does_not_hold_updates()
    {
        var store = Store((_, _) => { });
        Assert.False(store.RecordInstallation(_backups, _now));
        Assert.Null(store.HoldUntil(_now));
        Assert.All(_backups, backup => Assert.True(File.Exists(backup.Backup)));
    }

    [Fact]
    public void Write_failure_retains_files_and_updates_remain_available()
    {
        var store = Store((_, _) => throw new IOException("synthetic write fault"));
        store.ObserveLegacy(_now);
        Assert.Null(store.HoldUntil(_now));
        Assert.Equal((0, _backups.Count), store.Cleanup(_now.AddDays(2)));
    }

    [Fact]
    public void Corrupt_receipt_is_quarantined_and_never_readopted()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        File.WriteAllText(ReceiptPath, "{ damaged receipt");
        Assert.Null(store.HoldUntil(_now));
        Assert.True(File.Exists(ReceiptPath + ".corrupt"));
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.Equal((0, _backups.Count), store.Cleanup(_now.AddDays(2)));
    }

    [Fact]
    public void Changed_backup_is_not_restored_or_deleted()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        File.WriteAllText(_backups[0].Backup, "changed previous file");
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.Throws<InvalidDataException>(() => store.Rollback(Generation, _now));
        store.Cleanup(_now.AddDays(2));
        Assert.True(File.Exists(_backups[0].Backup));
    }

    [Fact]
    public void Incomplete_or_ambiguous_legacy_generation_is_never_guessed()
    {
        var dependency = _backups.Single(p => Path.GetFileName(p.Original) == "dependency.dll");
        File.Delete(dependency.Backup);
        Assert.False(Store().GetStatus(_now).RollbackAvailable);
        File.WriteAllText(dependency.Backup, "old dependency");
        var other = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_root, "GlDrive.exe." + other + ".old"), "older incomplete build");
        Assert.False(Store().GetStatus(_now).RollbackAvailable);
    }

    [Fact]
    public void No_rollback_or_probation_hold_from_untrusted_installation()
    {
        var store = Store(trust: _ => false);
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.False(store.RecordInstallation(_backups, _now));
        Assert.Null(store.HoldUntil(_now));
        Assert.Throws<IOException>(() => store.Rollback(Generation, _now));
        Assert.All(_backups, backup => Assert.True(File.Exists(backup.Backup)));
    }

    [Fact]
    public void Rollback_restores_exact_previous_generation_and_removes_new_only_payload_once()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        store.Rollback(Generation, _now.AddHours(1));
        Assert.Equal("old executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
        Assert.Equal("old dependency", File.ReadAllText(Path.Combine(_root, "dependency.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "new-only.txt")));
        Assert.False(store.GetStatus(_now.AddHours(2)).RollbackAvailable);
        Assert.Null(store.HoldUntil(_now.AddHours(2)));
        Assert.Throws<InvalidDataException>(() => store.Rollback(Generation, _now.AddHours(2)));
        Assert.All(_backups, backup => Assert.True(File.Exists(backup.Backup)));
    }

    [Fact]
    public void Interrupted_restore_reverses_current_payload_and_cannot_repeat()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        var copies = 0;
        Assert.Throws<IOException>(() => store.Rollback(Generation, _now, (source, destination) =>
        {
            // Consumption is durable before the first original installed file is changed.
            Assert.True(JsonNode.Parse(File.ReadAllText(ReceiptPath))!["consumed"]!.GetValue<bool>());
            if (++copies == 2) throw new IOException("synthetic restore interruption");
            File.Copy(source, destination);
        }));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
        Assert.Equal("new assembly", File.ReadAllText(Path.Combine(_root, "GlDrive.dll")));
        Assert.Equal("new content", File.ReadAllText(Path.Combine(_root, "new-only.txt")));
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.Throws<InvalidDataException>(() => store.Rollback(Generation, _now));
    }

    [Fact]
    public void Changed_current_payload_is_not_overwritten_and_receipt_is_not_consumed()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        File.WriteAllText(Path.Combine(_root, "GlDrive.dll"), "new manual patch");
        Assert.Throws<InvalidDataException>(() => store.Rollback(Generation, _now));
        Assert.False(JsonNode.Parse(File.ReadAllText(ReceiptPath))!["consumed"]!.GetValue<bool>());
        Assert.Equal("new manual patch", File.ReadAllText(Path.Combine(_root, "GlDrive.dll")));
    }

    [Fact]
    public void Escaping_manifest_path_is_rejected_without_mutating_payload()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        var json = JsonNode.Parse(File.ReadAllText(ReceiptPath))!;
        json["previousFiles"]![0]!["path"] = "../outside.txt";
        File.WriteAllText(ReceiptPath, json.ToJsonString());
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.Throws<InvalidDataException>(() => store.Rollback(Generation, _now));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
    }

    [Fact]
    public void Future_receipt_neither_holds_updates_nor_permits_rollback()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now.AddDays(1)));
        Assert.Null(store.HoldUntil(_now));
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        Assert.Equal((0, _backups.Count), store.Cleanup(_now));
    }

    [Fact]
    public void Receipt_capacity_evicts_oldest_observations_and_keeps_new_evidence()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        var original = File.ReadAllText(ReceiptPath);
        for (var i = 0; i < 140; i++)
        {
            var generation = Guid.NewGuid().ToString("N");
            var json = JsonNode.Parse(original)!;
            json["generation"] = generation;
            json["createdUtc"] = _now.AddDays(-5).ToString("O");
            var path = Path.Combine(_root, UpdateProbation.MetadataDirectory, generation + ".json");
            File.WriteAllText(path, json.ToJsonString());
            File.SetLastWriteTimeUtc(path, _now.AddDays(-5).AddSeconds(i));
        }
        Assert.Equal(_now.AddHours(24), store.HoldUntil(_now));
        Assert.True(store.GetStatus(_now).RollbackAvailable);
        store.Rollback(Generation, _now);
        Assert.False(store.GetStatus(_now).RollbackAvailable);
    }

    [Fact]
    public void Mutation_lock_serializes_updates_and_rollbacks()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        using var held = store.AcquireMutationLock();
        Assert.Throws<IOException>(() => store.AcquireMutationLock());
        Assert.Throws<IOException>(() => store.Rollback(Generation, _now));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
    }

    [Fact]
    public void Concurrent_adoption_cannot_reset_a_consumed_generation()
    {
        var first = Store();
        var second = Store();
        using (first.AcquireMutationLock()) second.ObserveLegacy(_now);
        Assert.False(File.Exists(ReceiptPath));
        first.Rollback(Generation, _now);
        second.ObserveLegacy(_now.AddMinutes(1));
        Assert.True(JsonNode.Parse(File.ReadAllText(ReceiptPath))!["consumed"]!.GetValue<bool>());
        Assert.False(second.GetStatus(_now.AddMinutes(1)).RollbackAvailable);
    }

    [Fact]
    public void Production_poll_never_delays_hotfixes_for_retention_and_update_handoff_keeps_receipts()
    {
        var source = Source("Services/UpdateChecker.cs");
        var poll = source[source.IndexOf("public void StartPeriodicCheck", StringComparison.Ordinal)..];
        static void AssertNoRetentionGate(string source)
        {
            Assert.DoesNotContain("HoldUntil", source);
            Assert.DoesNotContain("IsProbationHoldingInstall", source);
        }
        AssertNoRetentionGate(poll);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoRetentionGate(poll.Replace(
            "string? skipReason =", "string? skipReason = IsProbationHoldingInstall() ? \"held\" :", StringComparison.Ordinal)));
        var skipGate = poll.IndexOf("if (skipReason == null)", StringComparison.Ordinal);
        var forceBusy = poll.IndexOf("ShouldForceDeferredInstall(heldFor)", StringComparison.Ordinal);
        Assert.True(skipGate >= 0 && forceBusy > skipGate);
        Assert.Contains("TimeSpan.FromHours(3)", source);
        var apply = source[source.IndexOf("public static void ApplyUpdate(int pid", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("internal static string UpdateCompletionPath", StringComparison.Ordinal)];
        Assert.Contains("UpdateProbation.ForInstalledRoot(installDir).AcquireMutationLock()", apply);
        Assert.Contains("UpdateProbation.ForInstalledRoot(installDir).RecordInstallation", apply);
        Assert.DoesNotContain("UpdateProbation.InstalledStore()", apply);
        Assert.Contains("validatedInstallDir != null && !anotherMutationMayOwnRelaunch", apply);
        Assert.Contains("if (!anotherMutationMayOwnRelaunch) SignalUpdateCompletion(pid);", apply);

        var rollback = source[source.IndexOf("internal static int RollbackFromLocalRequest", StringComparison.Ordinal)..];
        rollback = rollback[..rollback.IndexOf("public void StartPeriodicCheck", StringComparison.Ordinal)];
        Assert.DoesNotContain("WriteUpdateLog(", rollback);
        Assert.DoesNotContain("Path.GetTempPath()", rollback);
        Assert.DoesNotContain(".Kill(", rollback);
        Assert.Contains("UpdateProbation.LogRollbackFailure", rollback);
    }

    private static string Source(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "src", "GlDrive", relative);
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void New_backup_does_not_rename_recovery_metadata_or_mutation_lock()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        using (store.AcquireMutationLock()) { }
        var next = new List<(string Original, string Backup)>();
        UpdateChecker.BackUpInstalledFiles(_root, next);
        Assert.True(File.Exists(ReceiptPath));
        Assert.True(File.Exists(Path.Combine(_root, UpdateProbation.MutationLockFile)));
        Assert.DoesNotContain(next, item => UpdateProbation.IsMetadataPath(_root, item.Original));
    }

    [Fact]
    public void A_second_install_can_record_its_generation_during_the_first_retention_period()
    {
        Assert.True(Store().RecordInstallation(_backups, _now));
        var firstReceipt = ReceiptPath;
        var hotfixBackups = new List<(string Original, string Backup)>();
        UpdateChecker.BackUpInstalledFiles(_root, hotfixBackups);
        WriteBuild("hotfix");
        var hotfix = new UpdateProbationStore(_root, new Version(3, 19, 1), _ => true,
            path => File.ReadAllText(path).StartsWith("hotfix", StringComparison.Ordinal) ? new Version(3, 19, 1) : _current);
        Assert.True(hotfix.RecordInstallation(hotfixBackups, _now.AddMinutes(10)));
        Assert.True(hotfix.GetStatus(_now.AddMinutes(11)).RollbackAvailable);
        Assert.True(File.Exists(firstReceipt));
        Assert.Equal(_backups.Count, _backups.Count(backup => File.Exists(backup.Backup)));
    }

    [Theory]
    [InlineData("O:BAG:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;BU)", true)]
    [InlineData("O:BAG:BAD:(A;;FA;;;BA)(A;OICIIO;GA;;;CO)(A;;FR;;;BU)", true)]
    [InlineData("O:BUG:BAD:(A;;FA;;;BA)(A;;FR;;;BU)", false)]
    [InlineData("O:BAG:BAD:(A;;FA;;;BA)(A;;GW;;;BU)", false)]
    [InlineData("O:BAG:BAD:(A;;FA;;;BA)(A;;GA;;;BU)", false)]
    [InlineData("O:BAG:BAD:(A;;FA;;;BA)(A;;WD;;;BU)", false)]
    public void Privileged_restore_requires_trusted_owner_and_effective_write_acl(string sddl, bool expected) =>
        Assert.Equal(expected, UpdateInstallationTrust.HasProtectedDescriptor(new RawSecurityDescriptor(sddl)));

    [Fact]
    public void Writable_or_deletable_ancestor_cannot_protect_program_files()
    {
        Assert.False(UpdateInstallationTrust.HasProtectedDescriptor(new RawSecurityDescriptor("O:BAG:BAD:(A;;FA;;;BA)(A;;SD;;;BU)"), true));
        Assert.True(UpdateInstallationTrust.HasProtectedDescriptor(new RawSecurityDescriptor("O:BAG:BAD:(A;;FA;;;BA)(A;;0x4;;;BU)"), true));
    }

    [Fact]
    public void Untrusted_payload_subfolder_disables_rollback_but_not_cleanup_of_trusted_siblings()
    {
        var untrusted = Path.Combine(_root, "runtimes");
        Directory.CreateDirectory(untrusted);
        var preserved = Path.Combine(untrusted, "native.dll.old");
        File.WriteAllText(preserved, "untrusted content");
        File.WriteAllText(Path.Combine(untrusted, "native.dll"), "untrusted runtime dependency");
        var store = Store(trust: path => !Path.GetFullPath(path).StartsWith(untrusted, StringComparison.OrdinalIgnoreCase));
        Assert.True(store.HasPreviousFiles());
        Assert.False(store.RecordInstallation(_backups, _now));
        Assert.False(store.GetStatus(_now).RollbackAvailable);
        var orphan = Path.Combine(_root, "orphan.dll." + Guid.NewGuid().ToString("N") + ".old");
        File.WriteAllText(orphan, "incomplete prior generation");
        var result = store.Cleanup(_now.AddDays(2));
        Assert.Equal(_backups.Count + 1, result.Deleted);
        Assert.All(_backups, item => Assert.False(File.Exists(item.Backup)));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(preserved));
    }

    [Fact]
    public void Orphan_cleanup_keeps_verified_unexpired_receipt_files()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        var orphan = Path.Combine(_root, "abandoned.old");
        File.WriteAllText(orphan, "previous leftover");
        Assert.Equal((1, _backups.Count), store.Cleanup(_now.AddHours(1)));
        Assert.False(File.Exists(orphan));
        Assert.All(_backups, item => Assert.True(File.Exists(item.Backup)));
        Assert.True(store.GetStatus(_now.AddHours(1)).RollbackAvailable);
    }

    [Fact]
    public void A_successful_rollback_does_not_keep_duplicate_staging_builds()
    {
        var store = Store();
        Assert.True(store.RecordInstallation(_backups, _now));
        store.Rollback(Generation, _now);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, UpdateProbation.MetadataDirectory), "rollback-*"));
        Assert.Equal("old executable", File.ReadAllText(Path.Combine(_root, "GlDrive.exe")));
    }

    [Fact]
    public void Cleanup_bounds_failed_stages_and_removes_completed_stages_without_old_payloads()
    {
        foreach (var backup in _backups) File.Delete(backup.Backup);
        string Stage(int minutesAgo, string? marker = null)
        {
            var stage = Path.Combine(_root, UpdateProbation.MetadataDirectory, "rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "payload.dll"), "retained diagnostic content");
            if (marker != null) File.WriteAllText(Path.Combine(stage, marker), "verified");
            Directory.SetLastWriteTimeUtc(stage, _now.AddMinutes(-minutesAgo));
            return stage;
        }
        var oldFailed = Stage(60);
        var newestFailed = Stage(20);
        var completed = Stage(10, ".complete");
        var repair = Stage(2000, ".retain-for-repair");
        var store = Store();
        Assert.True(store.HasPreviousFiles());
        store.Cleanup(_now);
        Assert.False(Directory.Exists(oldFailed));
        Assert.True(Directory.Exists(newestFailed));
        Assert.False(Directory.Exists(completed));
        Assert.True(Directory.Exists(repair));
        store.Cleanup(_now.AddDays(2));
        Assert.False(Directory.Exists(newestFailed));
        Assert.True(Directory.Exists(repair)); // May contain the only surviving current files.
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}

public sealed class UpdateRollbackGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-rollback-guard-" + Guid.NewGuid().ToString("N"));
    private readonly Version _version = new(3, 19, 0, 0);

    [Fact]
    public void Old_build_sees_rejected_tag_and_cannot_clear_it_from_a_stale_attempt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".update-attempt"), "v3.18.0");
        File.WriteAllText(Path.Combine(_root, ".update-failures"), "v3.17.0|2\n");
        Assert.True(UpdateRollbackGuard.Prepare(_root, _version, out var message), message);
        Assert.True(UpdateRollbackGuard.IsPrepared(_root, _version));
        Assert.False(File.Exists(Path.Combine(_root, ".update-attempt")));
        var lines = File.ReadAllLines(Path.Combine(_root, ".update-failures"));
        Assert.Contains("v3.17.0|2", lines);
        Assert.Contains("v3.19.0|3", lines);
        Assert.Contains("3.19.0|3", lines);
        Assert.Contains("v3.19.0.0|3", lines);
    }

    [Fact]
    public void Failed_or_unverified_marker_write_prevents_rollback()
    {
        Assert.False(UpdateRollbackGuard.Prepare(_root, _version, out _, (_, _) => { }));
        Assert.False(UpdateRollbackGuard.IsPrepared(_root, _version));
        Assert.False(UpdateRollbackGuard.Prepare(_root, _version, out _, (_, _) => throw new IOException("fault")));
    }

    [Fact]
    public void Recreated_attempt_after_preparation_invalidates_child_readback()
    {
        Assert.True(UpdateRollbackGuard.Prepare(_root, _version, out _));
        File.WriteAllText(Path.Combine(_root, ".update-attempt"), "v3.20.0");
        Assert.False(UpdateRollbackGuard.IsPrepared(_root, _version));
    }

    [Fact]
    public void Unremovable_attempt_refuses_rollback()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".update-attempt"));
        Assert.False(UpdateRollbackGuard.Prepare(_root, _version, out _));
        Assert.False(UpdateRollbackGuard.IsPrepared(_root, _version));
    }

    [Fact]
    public void Oversized_attempt_is_preserved_and_refuses_destructive_recovery()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".update-attempt");
        File.WriteAllText(path, new string('x', 4097));
        Assert.False(UpdateRollbackGuard.Prepare(_root, _version, out _));
        Assert.Equal(4097, new FileInfo(path).Length);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
