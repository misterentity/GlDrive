using System.IO;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The "this host's CPSV data peer stays TLS server" verdict (ioFTPD, e.g. sitea) lived only in
/// memory, so every restart re-learned it by failing two listings: on 2026-11-03 the 11:04 and
/// 16:06 startups each poisoned two sitea logins (a flood-ban-sensitive site), built an empty
/// search index for it and failed its stats refresh. The verdict now survives a restart until it
/// would have expired anyway.
/// </summary>
public sealed class CpsvVerdictPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gldrive-cpsv-verdicts-" + Guid.NewGuid().ToString("N"), "cpsv-verdicts.json");
    private readonly string _host = "persist-" + Guid.NewGuid().ToString("N") + ".test";

    public void Dispose()
    {
        CpsvDataHelper.DisableCpsvVerdictPersistenceForTests();
        CpsvDataHelper.ForgetHost(_host);
        try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch (IOException) { }
    }

    private void Restart()
    {
        CpsvDataHelper.DisableCpsvVerdictPersistenceForTests();
        CpsvDataHelper.ForgetHost(_host);
        Assert.False(CpsvDataHelper.IsCpsvUnusable(_host));
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
    }

    [Fact]
    public void A_switch_survives_a_restart()
    {
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
        Assert.True(CpsvDataHelper.RecordCpsvSwitch(_host, DateTime.UtcNow));

        Restart();

        Assert.True(CpsvDataHelper.IsCpsvUnusable(_host));
    }

    [Fact]
    public void An_expired_switch_is_not_restored()
    {
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
        CpsvDataHelper.RecordCpsvSwitch(_host, DateTime.UtcNow - CpsvDataHelper.CpsvSwitchLifetime - TimeSpan.FromMinutes(1));

        Restart();

        Assert.False(CpsvDataHelper.IsCpsvUnusable(_host));
    }

    [Fact]
    public void Expiry_is_persisted_so_a_restart_cannot_resurrect_it()
    {
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
        var start = DateTime.UtcNow - TimeSpan.FromHours(1);
        CpsvDataHelper.RecordCpsvSwitch(_host, start);
        Assert.False(CpsvDataHelper.IsCpsvUnusable(_host, start + CpsvDataHelper.CpsvSwitchLifetime));

        Restart();

        Assert.False(CpsvDataHelper.IsCpsvUnusable(_host));
    }

    [Fact]
    public void Renewal_by_standard_listings_is_persisted()
    {
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
        var now = DateTime.UtcNow;
        CpsvDataHelper.RecordCpsvSwitch(_host, now - TimeSpan.FromHours(5));
        CpsvDataHelper.RecordStandardListingSuccess(_host, now);

        Restart();

        // Without the renewal on disk the restored switch would expire one hour from now.
        Assert.True(CpsvDataHelper.IsCpsvUnusable(_host, now + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void An_unreadable_file_starts_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");
        CpsvDataHelper.EnableCpsvVerdictPersistence(_path);
        Assert.False(CpsvDataHelper.IsCpsvUnusable(_host));
    }
}
