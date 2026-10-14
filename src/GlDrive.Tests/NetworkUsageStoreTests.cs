using System.IO;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class NetworkUsageStoreTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-usage-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "network-usage.json");
    private DateTime _now = new(2026, 10, 14, 23, 59, 0); // Wednesday

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private NetworkUsageStore NewStore() => new(FilePath, () => _now);

    [Fact]
    public void Daily_rolls_over_at_local_midnight()
    {
        var store = NewStore();
        store.Add("eth", 100);
        Assert.Equal(100, store.UsedThisPeriod("eth", AllowancePeriod.Daily));

        _now = new DateTime(2026, 10, 15, 0, 0, 1);
        Assert.Equal(0, store.UsedThisPeriod("eth", AllowancePeriod.Daily));
        store.Add("eth", 5);
        Assert.Equal(5, store.UsedThisPeriod("eth", AllowancePeriod.Daily));
        Assert.Equal(105, store.UsedThisPeriod("eth", AllowancePeriod.Weekly));
    }

    [Fact]
    public void Weekly_rolls_over_on_monday()
    {
        _now = new DateTime(2026, 10, 12, 0, 0, 0); // Monday
        var store = NewStore();
        store.Add("eth", 60);
        _now = new DateTime(2026, 10, 18, 23, 0, 0); // Sunday, same week
        store.Add("eth", 40);

        _now = new DateTime(2026, 10, 18, 23, 59, 59);
        Assert.Equal(100, store.UsedThisPeriod("eth", AllowancePeriod.Weekly));
        _now = new DateTime(2026, 10, 19, 0, 0, 0); // Monday
        Assert.Equal(0, store.UsedThisPeriod("eth", AllowancePeriod.Weekly));
        Assert.Equal(100, store.UsedThisPeriod("eth", AllowancePeriod.Monthly));
    }

    [Fact]
    public void Monthly_rolls_over_on_the_first()
    {
        _now = new DateTime(2026, 10, 2, 12, 0, 0);
        var store = NewStore();
        store.Add("eth", 10);
        _now = new DateTime(2026, 10, 31, 23, 0, 0);
        store.Add("eth", 20);
        Assert.Equal(30, store.UsedThisPeriod("eth", AllowancePeriod.Monthly));

        _now = new DateTime(2026, 11, 1, 0, 0, 0);
        Assert.Equal(0, store.UsedThisPeriod("eth", AllowancePeriod.Monthly));
    }

    [Fact]
    public void Period_start_math()
    {
        var wed = new DateOnly(2026, 10, 14);
        Assert.Equal(new DateOnly(2026, 10, 12), NetworkUsageStore.PeriodStart(wed, AllowancePeriod.Weekly));
        Assert.Equal(new DateOnly(2026, 10, 12), NetworkUsageStore.PeriodStart(new DateOnly(2026, 10, 12), AllowancePeriod.Weekly));
        Assert.Equal(new DateOnly(2026, 10, 12), NetworkUsageStore.PeriodStart(new DateOnly(2026, 10, 18), AllowancePeriod.Weekly));
        Assert.Equal(new DateOnly(2026, 10, 1), NetworkUsageStore.PeriodStart(wed, AllowancePeriod.Monthly));
        Assert.Equal(wed, NetworkUsageStore.PeriodStart(wed, AllowancePeriod.Daily));
    }

    [Fact]
    public void Usage_survives_a_restart()
    {
        var store = NewStore();
        store.Add("eth", 1234);
        store.Add(NetworkPath.DefaultId, 99);
        store.Flush();

        var reloaded = NewStore();
        Assert.Equal(1234, reloaded.UsedThisPeriod("eth", AllowancePeriod.Daily));
        Assert.Equal(99, reloaded.UsedThisPeriod(NetworkPath.DefaultId, AllowancePeriod.Monthly));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_starts_empty_and_is_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = NewStore();
        Assert.Equal(0, store.UsedThisPeriod("eth", AllowancePeriod.Daily));
        store.Add("eth", 7);
        store.Flush();
        Assert.Equal(7, NewStore().UsedThisPeriod("eth", AllowancePeriod.Daily));
    }

    [Fact]
    public void Missing_file_starts_empty()
    {
        Assert.Equal(0, NewStore().UsedThisPeriod("eth", AllowancePeriod.Monthly));
    }

    [Fact]
    public void Reset_clears_one_network()
    {
        var store = NewStore();
        store.Add("eth", 10);
        store.Add("tether", 20);
        store.Reset("eth");
        Assert.Equal(0, store.UsedThisPeriod("eth", AllowancePeriod.Daily));
        Assert.Equal(20, NewStore().UsedThisPeriod("tether", AllowancePeriod.Daily));
        Assert.Equal(0, NewStore().UsedThisPeriod("eth", AllowancePeriod.Daily));
    }

    [Fact]
    public void IsOverAllowance_uses_the_network_settings()
    {
        var store = NewStore();
        var capped = new NetworkPath("tether", "Phone", System.Net.IPAddress.Loopback,
            new NetworkPathConfig { AllowanceGb = 1, Period = AllowancePeriod.Daily });
        var noPeriod = capped with { Settings = new NetworkPathConfig { AllowanceGb = 1, Period = AllowancePeriod.None } };
        var noAllowance = capped with { Settings = new NetworkPathConfig { AllowanceGb = 0, Period = AllowancePeriod.Daily } };

        store.Add("tether", Gb - 1);
        Assert.False(store.IsOverAllowance(capped));
        store.Add("tether", 1);
        Assert.True(store.IsOverAllowance(capped));
        Assert.False(store.IsOverAllowance(noPeriod));
        Assert.False(store.IsOverAllowance(noAllowance));
        Assert.False(store.IsOverAllowance(NetworkPath.Default));

        _now = _now.AddDays(1);
        Assert.False(store.IsOverAllowance(capped));
    }
}
