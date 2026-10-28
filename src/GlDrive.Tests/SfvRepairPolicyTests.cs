using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class SfvRepairPolicyTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("oversized")]
    [InlineData("partial")]
    [InlineData("metadata")]
    public void Pending_repair_only_excludes_other_files_proven_complete_locally(string incompleteKind)
    {
        using var h = new Harness();
        Assert.True(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
        var healthy = new FtpDownloadFile("/release/healthy.rar", Path.Combine(h.Item.LocalPath, "healthy.rar"), 5);
        File.WriteAllBytes(healthy.LocalPath, Bytes("sound"));
        var absent = new FtpDownloadFile("/release/other.r00", Path.Combine(h.Item.LocalPath, "other.r00"), 5);
        if (incompleteKind != "missing")
            File.WriteAllText(absent.LocalPath, incompleteKind == "short" ? "tiny" : incompleteKind == "oversized" ? "longer" : "right");
        if (incompleteKind == "partial") File.WriteAllText(absent.LocalPath + ".gdpart", "part");
        if (incompleteKind == "metadata") File.WriteAllText(absent.LocalPath + ".gdpart.json", "{}");

        var selected = SfvRepair.SelectPayload(h.Item, [h.Plan.Files[0], healthy, absent]);

        Assert.Equal(new[] { h.Plan.Files[0], absent }, selected);
        Assert.Equal(Bytes("sound"), File.ReadAllBytes(healthy.LocalPath));
    }

    [Fact]
    public void Verified_cleanup_retains_untracked_sidecars_and_removes_only_its_matching_marker()
    {
        using var h = new Harness();
        File.WriteAllText(h.PayloadPath + ".gdpart", "old partial");
        Assert.True(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
        var quarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
        File.WriteAllBytes(h.PayloadPath, Bytes("right"));

        SfvRepair.Verified(h.Item, h.Persist);

        Assert.False(File.Exists(quarantine));
        Assert.Equal("old partial", File.ReadAllText(quarantine + ".gdpart"));
        Assert.True(Directory.Exists(Path.GetDirectoryName(quarantine)));
        Assert.False(File.Exists(Path.Combine(h.Item.LocalPath, ".gldrive-sfv-owner")));
        Assert.Equal(Bytes("right"), File.ReadAllBytes(h.PayloadPath));
    }

    [Fact]
    public void Verified_cleanup_preserves_marker_and_backups_if_marker_owner_changed()
    {
        using var h = new Harness();
        Assert.True(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
        var quarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
        var marker = Path.Combine(h.Item.LocalPath, ".gldrive-sfv-owner");
        var otherOwner = Guid.NewGuid().ToString("N");
        File.WriteAllText(marker, otherOwner);
        File.WriteAllBytes(h.PayloadPath, Bytes("right"));

        SfvRepair.Verified(h.Item, h.Persist);

        Assert.Equal(otherOwner, File.ReadAllText(marker));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(quarantine));
        Assert.Equal(Bytes("right"), File.ReadAllBytes(h.PayloadPath));
    }

    [Fact]
    public void Failed_verification_save_keeps_recovery_evidence_and_restart_preserves_correct_replacement()
    {
        using var h = new Harness();
        h.Item.SfvIntegrityHold = true;
        Assert.True(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
        var durableIntent = h.Serialized;
        var quarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
        Assert.True(File.Exists(quarantine));
        File.WriteAllBytes(h.PayloadPath, Bytes("right"));
        SfvRepair.Verified(h.Item, () => false);
        Assert.False(h.Item.SfvIntegrityHold);
        Assert.False(Assert.Single(h.Item.SfvRepairFiles).Pending);
        Assert.True(File.Exists(quarantine));
        var restored = JsonSerializer.Deserialize<DownloadItem>(durableIntent)!;
        Assert.True(restored.SfvIntegrityHold);
        Assert.True(Assert.Single(restored.SfvRepairFiles).Pending);

        // Existing quarantine proves the original move happened; recovery only
        // needs to verify the replacement and does not require another write.
        SfvRepair.ResumePending(restored, h.Plan, () => false);
        Assert.Equal(Bytes("right"), File.ReadAllBytes(h.PayloadPath));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(quarantine));
        Assert.Equal(1, Assert.Single(restored.SfvRepairFiles).Attempts);
    }

    [Fact]
    public void Failed_initial_budget_and_failed_replay_preserve_original_without_creating_quarantine()
    {
        using var h = new Harness();
        var before = Directory.GetFiles(h.Item.LocalPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.False(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, () => false).Queued);
        Assert.Equal(1, Assert.Single(h.Item.SfvRepairFiles).Attempts);
        Assert.Throws<IOException>(() => SfvRepair.ResumePending(h.Item, h.Plan, () => false));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PayloadPath));
        Assert.False(File.Exists(Assert.Single(h.Item.SfvRepairFiles).QuarantinePath));
        Assert.Equal(before, Directory.GetFiles(h.Item.LocalPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void Remote_source_match_does_not_authorize_a_local_path_absent_from_current_plan()
    {
        using var h = new Harness();
        var otherPath = Path.Combine(h.Item.LocalPath, "different.rar");
        var changedPlan = h.Plan with { Files = [new FtpDownloadFile("/release/disc.rar", otherPath, 5)] };
        var decision = SfvRepair.Prepare(h.Item, changedPlan, [h.Mismatch], 2, h.Persist);
        Assert.False(decision.Queued);
        Assert.Equal(0, Assert.Single(h.Item.SfvRepairFiles).Attempts);
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PayloadPath));
        Assert.False(File.Exists(otherPath));
    }

    [Fact]
    public void Interrupted_sidecar_quarantine_leaves_original_until_both_stale_parts_have_moved()
    {
        using var h = new Harness();
        var partial = h.PayloadPath + ".gdpart";
        var sidecar = h.PayloadPath + ".gdpart.json";
        File.WriteAllText(partial, "old partial");
        File.WriteAllText(sidecar, "old metadata");
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
            var quarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
            Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PayloadPath));
            Assert.False(File.Exists(quarantine));
            Assert.False(File.Exists(partial));
            Assert.Equal("old partial", File.ReadAllText(quarantine + ".gdpart"));
        }

        SfvRepair.ResumePending(h.Item, h.Plan, h.Persist);
        var finalQuarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
        Assert.False(File.Exists(h.PayloadPath));
        Assert.False(File.Exists(sidecar));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(finalQuarantine));
        Assert.Equal("old metadata", File.ReadAllText(finalQuarantine + ".gdpart.json"));
        Assert.Equal(1, Assert.Single(h.Item.SfvRepairFiles).Attempts);
    }

    [Fact]
    public void Published_quarantine_preserves_replacement_partial_on_next_recovery()
    {
        using var h = new Harness();
        File.WriteAllText(h.PayloadPath + ".gdpart", "old partial");
        File.WriteAllText(h.PayloadPath + ".gdpart.json", "old metadata");
        Assert.True(SfvRepair.Prepare(h.Item, h.Plan, [h.Mismatch], 2, h.Persist).Queued);
        var quarantine = Assert.Single(h.Item.SfvRepairFiles).QuarantinePath!;
        Assert.Equal("old partial", File.ReadAllText(quarantine + ".gdpart"));
        Assert.Equal("old metadata", File.ReadAllText(quarantine + ".gdpart.json"));
        File.WriteAllText(h.PayloadPath + ".gdpart", "new partial");
        File.WriteAllText(h.PayloadPath + ".gdpart.json", "new metadata");
        var restored = JsonSerializer.Deserialize<DownloadItem>(h.Serialized)!;

        SfvRepair.ResumePending(restored, h.Plan, () => false);
        Assert.Equal("new partial", File.ReadAllText(h.PayloadPath + ".gdpart"));
        Assert.Equal("new metadata", File.ReadAllText(h.PayloadPath + ".gdpart.json"));
        Assert.Equal("old partial", File.ReadAllText(quarantine + ".gdpart"));
        Assert.Equal("old metadata", File.ReadAllText(quarantine + ".gdpart.json"));
        Assert.Equal(1, Assert.Single(restored.SfvRepairFiles).Attempts);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-sfv-policy-" + Guid.NewGuid().ToString("N"));
        public DownloadItem Item { get; }
        public DownloadTreePlan Plan { get; }
        public string PayloadPath { get; }
        public string Serialized { get; private set; } = "";
        public SfvIssue Mismatch => new("disc.rar", SfvIssueKind.ChecksumMismatch, "CRC mismatch")
        {
            LocalPath = PayloadPath,
            ExpectedCrc = Crc32.HashToUInt32(Bytes("right")),
            ActualCrc = Crc32.HashToUInt32(Bytes("bad01"))
        };

        public Harness()
        {
            Directory.CreateDirectory(_root);
            Item = new DownloadItem { LocalPath = Path.Combine(_root, "release"), TotalBytes = 5, DownloadedBytes = 5 };
            PayloadPath = Path.Combine(Item.LocalPath, "disc.rar");
            Plan = new DownloadTreePlan(true, [new FtpDownloadFile("/release/disc.rar", PayloadPath, 5)], [Item.LocalPath]);
            SfvRepair.RecordOwnership(Item, Plan, Persist);
            Assert.Single(Item.SfvRepairFiles);
            File.WriteAllBytes(PayloadPath, Bytes("bad01"));
        }

        public bool Persist()
        {
            Serialized = JsonSerializer.Serialize(Item);
            return true;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
