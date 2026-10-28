using System.Text.Json;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class HealthConfigAndBorrowLedgerTests
{
    [Fact]
    public void Existing_config_enables_passive_health_and_login_protection_only()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(config.Health.Enabled);
        Assert.True(config.Health.AnnounceSilenceAlertsEnabled);
        Assert.False(config.Health.TcpProbesEnabled);
        Assert.False(config.Health.AnnounceRecoveryEnabled);
        Assert.True(new PoolConfig().LoginRateLimitEnabled);
        Assert.Equal(8, new PoolConfig().MaxLoginAttemptsPerFiveMinutes);
        ConfigurationAutomation.Validate(config);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void Login_attempt_budget_rejects_out_of_range_values(int budget)
    {
        var config = new AppConfig { Servers = [new() { Pool = new() { MaxLoginAttemptsPerFiveMinutes = budget } }] };
        Assert.Throws<ArgumentException>(() => ConfigurationAutomation.Validate(config));
    }

    [Fact]
    public void Budget_edits_are_live_without_remount()
    {
        var server = new ServerConfig();
        var downloads = new DownloadConfig();
        var before = RuntimeConfigPolicy.Capture(server, downloads, "", 1);
        server.Pool.LoginRateLimitEnabled = false;
        server.Pool.MaxLoginAttemptsPerFiveMinutes = 12;
        var after = RuntimeConfigPolicy.Capture(server, downloads, "", 1);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(2, after.Changed(before, RuntimeConfigEffect.Live).Count);
    }

    [Fact]
    public void Active_lease_snapshots_are_detached_and_ending_one_does_not_drop_another()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ledger = new FtpBorrowLedger(() => now);
        var race = ledger.Begin(FtpBorrowPurpose.Race);
        now = now.AddSeconds(3);
        var poll = ledger.Begin(FtpBorrowPurpose.NukePoll);
        var snapshot = ledger.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot[1].BorrowedAtUtc - snapshot[0].BorrowedAtUtc);
        ledger.End(race);
        ledger.End(race);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(poll, Assert.Single(ledger.Snapshot()).LeaseId);
        ledger.End(poll);
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void Invalid_labels_cannot_leak_arbitrary_data_and_concurrent_leases_stay_unique()
    {
        var ledger = new FtpBorrowLedger();
        Parallel.For(0, 100, _ => ledger.Begin((FtpBorrowPurpose)int.MaxValue));
        var snapshot = ledger.Snapshot();
        Assert.Equal(100, snapshot.Select(s => s.LeaseId).Distinct().Count());
        Assert.All(snapshot, s => Assert.Equal(FtpBorrowPurpose.Unspecified, s.Purpose));
        Parallel.ForEach(snapshot, s => ledger.End(s.LeaseId));
        Assert.Empty(ledger.Snapshot());
    }
}
