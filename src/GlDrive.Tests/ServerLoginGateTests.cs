using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Permit accounting is the #1 correctness concern of the account login gate: a
/// leaked or double-released permit drifts the live-login count and either
/// deadlocks the account or re-opens the 530 storm. These are pure/synchronous —
/// they use zero-timeout acquires (deterministic, no Task.Delay) so they never
/// join the flaky set.
/// </summary>
public class ServerLoginGateTests
{
    private static readonly TimeSpan Now = TimeSpan.Zero; // non-blocking acquire attempt

    [Fact]
    public async Task Acquire_up_to_limit_then_blocks_until_release()
    {
        var gate = new ServerLoginGate("t", limit: 2, maxLimit: 2);
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.Equal(2, gate.Held);

        // Third acquire exceeds the limit — times out immediately.
        Assert.False(await gate.TryAcquireAsync(default, Now));
        Assert.Equal(2, gate.Held);

        gate.Release();
        Assert.Equal(1, gate.Held);
        Assert.True(await gate.TryAcquireAsync(default, Now)); // slot freed
        Assert.Equal(2, gate.Held);
    }

    [Fact]
    public async Task Over_release_is_ignored_and_held_never_goes_negative()
    {
        var gate = new ServerLoginGate("t", 1, 1);
        gate.Release();          // nothing held — must be ignored
        gate.Release();
        Assert.Equal(0, gate.Held);

        // The ceiling is intact: exactly one permit is still acquirable.
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.False(await gate.TryAcquireAsync(default, Now));
        Assert.Equal(1, gate.Held);
    }

    [Fact]
    public async Task TightenTo_shrinks_the_effective_ceiling()
    {
        var gate = new ServerLoginGate("t", 3, 3);
        Assert.Equal(3, gate.Limit);

        gate.TightenTo(2);
        Assert.Equal(2, gate.Limit);

        // Only 2 permits are now obtainable even though the gate started at 3.
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.False(await gate.TryAcquireAsync(default, Now));
    }

    [Fact]
    public void TightenTo_is_shrink_only_and_idempotent()
    {
        var gate = new ServerLoginGate("t", 2, 4);
        gate.TightenTo(3);   // >= current limit (2) — no-op grow attempt
        Assert.Equal(2, gate.Limit);
        gate.TightenTo(2);   // equal — no-op
        Assert.Equal(2, gate.Limit);
        gate.TightenTo(1);   // shrink
        Assert.Equal(1, gate.Limit);
        gate.TightenTo(1);   // idempotent
        Assert.Equal(1, gate.Limit);
    }

    [Fact]
    public async Task Two_consumers_sharing_one_gate_share_the_account_cap()
    {
        // Two pools (e.g. main + spread) to the same account hold ONE gate. The cap
        // is account-wide: A taking 2 leaves only 1 for B even though each "pool"
        // might individually want more.
        var gate = new ServerLoginGate("acct", 3, 3);

        // Pool A
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.True(await gate.TryAcquireAsync(default, Now));
        // Pool B
        Assert.True(await gate.TryAcquireAsync(default, Now)); // 3rd, last
        Assert.False(await gate.TryAcquireAsync(default, Now)); // account-wide cap hit
        Assert.Equal(3, gate.Held);
    }

    [Fact]
    public void Registry_returns_same_gate_per_account_case_insensitive()
    {
        var a = ServerLoginGateRegistry.GetOrCreate("Host.EXAMPLE", 21, "User", 4, 1);
        var b = ServerLoginGateRegistry.GetOrCreate("host.example", 21, "user", 9, 9);
        Assert.Same(a, b); // same account key → same gate (first cap wins)

        var c = ServerLoginGateRegistry.GetOrCreate("host.example", 9999, "user", 4, 1);
        Assert.NotSame(a, c); // different port → different account
    }

    [Fact]
    public void Registry_usable_limit_is_cap_minus_headroom()
    {
        var gate = ServerLoginGateRegistry.GetOrCreate("usable.test", 2121, "u", cap: 3, headroom: 1);
        Assert.Equal(2, gate.Limit); // 3 - 1

        var floored = ServerLoginGateRegistry.GetOrCreate("usable.test2", 2121, "u", cap: 1, headroom: 5);
        Assert.Equal(1, floored.Limit); // never below 1
    }

    [Fact]
    public async Task Reserved_permit_is_protected_from_non_priority_callers()
    {
        // limit 2, reserve 1 for FXP. The main pool (non-priority) can take only 1;
        // the 2nd is held in reserve and remains obtainable by a priority (spread) caller.
        var gate = new ServerLoginGate("acct", limit: 2, maxLimit: 2, reserved: 1);
        Assert.Equal(1, gate.Reserved);

        Assert.True(await gate.TryAcquireAsync(default, Now));          // main #1 — ok
        Assert.False(await gate.TryAcquireAsync(default, Now));         // main #2 — blocked by sub-cap
        Assert.True(await gate.TryAcquireAsync(default, Now, priority: true)); // FXP — gets the reserved slot
        Assert.False(await gate.TryAcquireAsync(default, Now, priority: true)); // total cap now hit
        Assert.Equal(2, gate.Held);
    }

    [Fact]
    public async Task Releasing_priority_and_general_permits_restores_both_pools()
    {
        var gate = new ServerLoginGate("acct", 2, 2, reserved: 1);
        Assert.True(await gate.TryAcquireAsync(default, Now));                  // general
        Assert.True(await gate.TryAcquireAsync(default, Now, priority: true));  // reserved
        Assert.Equal(2, gate.Held);

        gate.Release(priority: true);   // frees a TOTAL permit only
        gate.Release();                 // frees a TOTAL + a general permit
        Assert.Equal(0, gate.Held);

        // Both kinds of slot are obtainable again.
        Assert.True(await gate.TryAcquireAsync(default, Now));                 // general restored
        Assert.True(await gate.TryAcquireAsync(default, Now, priority: true)); // reserved restored
    }

    [Fact]
    public async Task Reserved_zero_behaves_like_plain_gate()
    {
        // The default (reserved: 0) must be byte-for-byte the old behavior.
        var gate = new ServerLoginGate("acct", 2, 2);
        Assert.Equal(0, gate.Reserved);
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.True(await gate.TryAcquireAsync(default, Now));
        Assert.False(await gate.TryAcquireAsync(default, Now));
    }

    [Fact]
    public void Registry_reserves_up_to_two_fxp_permits_while_leaving_one_general()
    {
        var two = ServerLoginGateRegistry.GetOrCreate("rsv.test", 2121, "u", cap: 3, headroom: 1);
        Assert.Equal(2, two.Limit);
        Assert.Equal(1, two.Reserved); // usable 2 → reserve 1 for FXP

        var three = ServerLoginGateRegistry.GetOrCreate("rsv.test3", 2121, "u", cap: 4, headroom: 1);
        Assert.Equal(3, three.Limit);
        Assert.Equal(2, three.Reserved); // usable 3 → two concurrent FXP logins

        var one = ServerLoginGateRegistry.GetOrCreate("rsv.test2", 2121, "u", cap: 1, headroom: 0);
        Assert.Equal(1, one.Limit);
        Assert.Equal(0, one.Reserved); // only 1 login — nothing to reserve
    }

    [Fact]
    public async Task TightenTo_preserves_the_fxp_reservation()
    {
        // Start at 3 usable with 1 reserved (general sub-cap = 2). A 530 tightens
        // total to 2 → general must drop to 1, reserved stays 1.
        var gate = new ServerLoginGate("acct", limit: 3, maxLimit: 3, reserved: 1);
        gate.TightenTo(2);
        Assert.Equal(2, gate.Limit);

        Assert.True(await gate.TryAcquireAsync(default, Now));          // main #1
        Assert.False(await gate.TryAcquireAsync(default, Now));         // main #2 blocked (general now 1)
        Assert.True(await gate.TryAcquireAsync(default, Now, priority: true)); // FXP still has its reserved slot
    }
}
