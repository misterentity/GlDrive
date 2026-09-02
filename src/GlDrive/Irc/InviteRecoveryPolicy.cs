namespace GlDrive.Irc;

/// <summary>
/// Decides when the standing invite-only retry should stop repeating itself and
/// escalate to a full IRC reconnect.
///
/// Why this exists (2026-08-31/09-01, v3.10.96): a network blip at 23:44 dropped all
/// three IRC connections. IRC reconnected in-process 75 seconds later and re-ran
/// SITE INVITE, which glftpd accepted ("Command Successful.") on BOTH zephyr and
/// superbnc. No INVITE ever arrived. The standing retry then re-issued an accepted
/// SITE INVITE 25 more times over the next 9h50m — every 30 minutes, on two
/// independent IRC networks — and every JOIN came back 473. All four invite-only
/// channels (#ent, #supers, #supers-chat, #supers-spam) stayed out. superbnc's
/// channels are the primary auto-race trigger, so that window produced zero
/// announces from the site.
///
/// At 09:35:52 an unrelated auto-update restarted the process. On the fresh
/// connection ONE SITE INVITE produced all three INVITEs in 746 ms, and there was
/// not another invite-only warning for the remaining 9.4 hours.
///
/// The FTP side was provably healthy across the whole window (SITE STATS parsed
/// cleanly every 5 minutes on zephyr, no stale-reply or CPSV desync signatures), and
/// inbound IRC was demonstrably being processed (the 473s themselves arrived). So the
/// command was issued correctly and accepted, and the invite still did not come. The
/// root cause lives on the far side of SITE INVITE — in the site bot's or the
/// network's view of our reconnected session — and is not determinable from our logs.
///
/// What IS determinable: repeating an action that has already failed 25 times is not
/// a recovery strategy, and the one action observed to work (tearing the IRC session
/// down and building a new one) was never attempted. This policy bounds the futile
/// loop and escalates to that action.
///
/// Deliberately conservative: escalation is capped to one forced reconnect per
/// <see cref="MinReconnectInterval"/> so a genuinely invite-only channel that nobody
/// will ever invite us to cannot turn into a reconnect loop.
/// </summary>
internal static class InviteRecoveryPolicy
{
    /// <summary>
    /// Join attempts (as counted by <c>_pendingInviteJoins</c>) that must accumulate
    /// before a forced reconnect is considered. The fast burst spends attempts 1-4 in
    /// ~30 seconds; the slow schedule then runs 5m, 15m, 15m, 30m, 30m… so attempt 8
    /// lands roughly 65 minutes after the first standing-retry warning. That is long
    /// enough to let a genuinely absent site bot come back on its own — the case the
    /// standing retry was built for — and short enough that a 9h50m outage becomes a
    /// ~1h one.
    /// </summary>
    internal const int EscalateAfterAttempts = 8;

    /// <summary>
    /// Floor between forced reconnects. A reconnect is cheap but not free: it drops
    /// every channel on that server, re-runs the FiSH key setup, and re-registers with
    /// the network. Two hours keeps the cost negligible while still giving a stuck
    /// session ~5 chances across an overnight outage instead of none.
    /// </summary>
    internal static readonly TimeSpan MinReconnectInterval = TimeSpan.FromHours(2);

    /// <summary>
    /// True when the invite-only retry for a channel has gone unanswered long enough
    /// that re-issuing it again is pointless and the session itself should be rebuilt.
    /// </summary>
    /// <param name="attempts">Consecutive failed join attempts for the channel.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="lastForcedReconnectUtc">
    /// When this server last forced a reconnect for this reason, or null if never.
    /// </param>
    internal static bool ShouldForceReconnect(
        int attempts, DateTime nowUtc, DateTime? lastForcedReconnectUtc)
    {
        if (attempts < EscalateAfterAttempts) return false;
        if (lastForcedReconnectUtc is not { } last) return true;
        return nowUtc - last >= MinReconnectInterval;
    }
}
