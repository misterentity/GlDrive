using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Relay mode probes direct CPSV-PASV before falling back to piping through
/// local memory. A failed probe poisons BOTH connections (two fresh logins per
/// file) — on 2026-07-01 every one of the 852 delivered files on the
/// superbnc->zephyr route paid that price because the probe re-ran per file.
/// The route memory must (a) stop probing a route that just failed, (b) allow
/// a re-probe after the TTL, and (c) never cache routes without server ids.
/// </summary>
public class FxpRelayRouteMemoryTests : IDisposable
{
    public FxpRelayRouteMemoryTests() => FxpTransfer.ResetRelayRoutesForTests();
    public void Dispose() => FxpTransfer.ResetRelayRoutesForTests();

    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_route_attempts_direct()
    {
        Assert.True(FxpTransfer.ShouldAttemptDirect("src", "dst", Now));
    }

    [Fact]
    public void Failed_route_skips_direct_within_ttl()
    {
        FxpTransfer.RecordDirectFailure("src", "dst", Now);
        Assert.False(FxpTransfer.ShouldAttemptDirect("src", "dst", Now));
        Assert.False(FxpTransfer.ShouldAttemptDirect("src", "dst",
            Now + FxpTransfer.RelayRouteRetry - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Failed_route_reprobes_after_ttl()
    {
        FxpTransfer.RecordDirectFailure("src", "dst", Now);
        Assert.True(FxpTransfer.ShouldAttemptDirect("src", "dst", Now + FxpTransfer.RelayRouteRetry));
    }

    [Fact]
    public void Route_memory_is_directional()
    {
        FxpTransfer.RecordDirectFailure("src", "dst", Now);
        Assert.True(FxpTransfer.ShouldAttemptDirect("dst", "src", Now));
    }

    [Fact]
    public void Success_clears_the_route()
    {
        FxpTransfer.RecordDirectFailure("src", "dst", Now);
        FxpTransfer.RecordDirectSuccess("src", "dst");
        Assert.True(FxpTransfer.ShouldAttemptDirect("src", "dst", Now));
    }

    [Fact]
    public void First_failure_reports_true_for_prominent_log_then_false()
    {
        Assert.True(FxpTransfer.RecordDirectFailure("src", "dst", Now));
        Assert.False(FxpTransfer.RecordDirectFailure("src", "dst", Now.AddHours(7)));
    }

    [Fact]
    public void Missing_server_ids_never_cache()
    {
        Assert.False(FxpTransfer.RecordDirectFailure("", "dst", Now));
        Assert.False(FxpTransfer.RecordDirectFailure("src", "", Now));
        Assert.True(FxpTransfer.ShouldAttemptDirect("", "dst", Now));
        Assert.True(FxpTransfer.ShouldAttemptDirect("src", "", Now));
    }
}
