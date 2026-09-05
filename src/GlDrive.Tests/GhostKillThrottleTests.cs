using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.105 — production log 2026-09-04 03:02:31..03:02:51: seven
/// "Pool: ghost kill (once per episode)" lines in 20 s against one account. The
/// per-pool episode flag re-armed on every successful connect, and a kill makes
/// the next connect succeed, so the "once" guard was inert under pressure. Each
/// <c>!entity</c> login severed the account's live sessions, failing the in-flight
/// transfer whose failure had triggered it — a self-perpetuating loop. The pool's
/// own header comment records that 6 kills in 4 minutes tripped a multi-minute
/// BNC cooldown on 2026-05-20.
///
/// The fix is an account-level minimum interval on the shared factory.
/// </summary>
public class GhostKillThrottleTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 3, 2, 31, DateTimeKind.Utc);

    [Fact]
    public void FirstKillIsAllowed()
    {
        var t = new GhostKillThrottle(TimeSpan.FromSeconds(60));
        Assert.True(t.TryAcquire(T0));
        Assert.Equal(T0, t.LastKillUtc);
    }

    [Fact]
    public void SecondKillInsideIntervalIsRefused_AndReportsAge()
    {
        var t = new GhostKillThrottle(TimeSpan.FromSeconds(60));
        Assert.True(t.TryAcquire(T0));
        Assert.False(t.TryAcquire(T0.AddSeconds(3), out var since));
        Assert.Equal(TimeSpan.FromSeconds(3), since);
        // A refused attempt must NOT move the clock, or a steady stream of refusals
        // would keep the kill locked out forever.
        Assert.Equal(T0, t.LastKillUtc);
    }

    [Fact]
    public void KillAfterIntervalIsAllowedAgain()
    {
        var t = new GhostKillThrottle(TimeSpan.FromSeconds(60));
        Assert.True(t.TryAcquire(T0));
        Assert.False(t.TryAcquire(T0.AddSeconds(59.9)));
        Assert.True(t.TryAcquire(T0.AddSeconds(60)));
        Assert.Equal(T0.AddSeconds(60), t.LastKillUtc);
    }

    [Fact]
    public void ProductionStorm_SevenAttemptsIn20s_YieldsExactlyOneKill()
    {
        // The observed cadence: 31.7, 35.0, 38.3, 41.3, 44.7, 47.7, 51.1
        var offsets = new[] { 0.0, 3.3, 6.6, 9.6, 13.0, 16.0, 19.4 };
        var t = new GhostKillThrottle();
        var allowed = offsets.Count(o => t.TryAcquire(T0.AddSeconds(o)));
        Assert.Equal(1, allowed);
    }

    [Fact]
    public void ConcurrentCallersAtTheSameInstant_OnlyOneWins()
    {
        var t = new GhostKillThrottle(TimeSpan.FromSeconds(60));
        using var start = new ManualResetEventSlim(false);
        var wins = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (t.TryAcquire(T0)) Interlocked.Increment(ref wins);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);
        Assert.Equal(1, wins);
    }

    [Fact]
    public void DefaultIntervalOutlastsTheDeferredTeardownWindow()
    {
        // Quarantined connections hold their login for 20 s of deferred teardown
        // (SerializedGnuTlsStream lineage). A kill inside that window sees the same
        // "login limit" the previous one did and learns nothing — the interval must
        // be comfortably longer than the window it is waiting out.
        Assert.True(GhostKillThrottle.DefaultMinInterval >= TimeSpan.FromSeconds(40));
    }

    // ---- wiring: the factory consults the throttle BEFORE touching the network ----

    private static FtpClientFactory MakeFactory(GhostKillThrottle throttle)
    {
        // 127.0.0.1:1 refuses instantly; nothing here listens.
        var cfg = new ServerConfig
        {
            Connection = new ConnectionConfig { Host = "127.0.0.1", Port = 1, Username = "unit" },
        };
        return new FtpClientFactory(cfg, new CertificateManager("ghostkill-tests.json"), throttle);
    }

    [Fact]
    public async Task Factory_KillGhosts_IsSuppressedWhileThrottled_WithoutConnecting()
    {
        var throttle = new GhostKillThrottle(TimeSpan.FromHours(1));
        Assert.True(throttle.TryAcquire(DateTime.UtcNow)); // a kill "just happened"
        var last = throttle.LastKillUtc;

        var factory = MakeFactory(throttle);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var killed = await factory.KillGhosts(CancellationToken.None);

        Assert.False(killed);
        Assert.Equal(last, throttle.LastKillUtc);
        // No connect attempt: a refused loopback connect is fast, but the FluentFTP
        // client construction + TLS setup is not sub-100ms. 500 ms is generous.
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms — did it connect?");
    }

    [Fact]
    public async Task Factory_KillGhosts_ConsumesTheThrottleWhenPermitted()
    {
        var throttle = new GhostKillThrottle(TimeSpan.FromHours(1));
        var factory = MakeFactory(throttle);

        // The !user login attempt itself is what the BNC counts as a reconnect, so a
        // permitted attempt consumes the interval even though nothing answers here.
        var killed = await factory.KillGhosts(CancellationToken.None);

        Assert.True(killed);
        Assert.NotNull(throttle.LastKillUtc);
        Assert.False(throttle.TryAcquire(DateTime.UtcNow));
    }
}
