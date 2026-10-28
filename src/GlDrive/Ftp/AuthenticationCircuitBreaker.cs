using System.Security.Cryptography;
using System.Text;
using Serilog;
using Failure = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;

namespace GlDrive.Ftp;

internal sealed record FtpLoginAttempt(string Key, long Generation, long Id, bool RecoveryProbe,
    bool Ghost, byte[] CredentialDigest, FtpLoginAccount Account, bool CredentialChanged = false);

/// <summary>Account-wide login admission; existing connected leases remain usable.</summary>
internal sealed class AuthenticationCircuitBreaker
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(5);
    // One stray fault (a TLS glitch on one pool's connect) must not stall every pool for
    // the account; only a burst of consecutive faults is a storm worth throttling.
    internal const int FaultBurstThreshold = 3;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly AuthHoldStore? _store;
    private long _nextAttempt;
    internal Func<FtpAuthenticationNotice, bool>? NotificationSink { get; set; }

    private sealed class State
    {
        internal bool Held;
        internal byte[]? CredentialDigest;
        internal string? ProtectedDigest;
        internal long Generation;
        internal long? ProbeId;
        internal DateTime NextProbeUtc;
        internal long NextProbeStamp;
        internal long FaultUntilStamp;
        internal DateTime FaultUntilUtc;
        internal int ConsecutiveFaults;
        internal bool NotificationDelivered;
        internal bool NotificationInFlight;
        internal long NotificationEpisode;
    }

    internal AuthenticationCircuitBreaker(TimeProvider? clock = null, AuthHoldStore? store = null)
    {
        _clock = clock ?? TimeProvider.System;
        _store = store;
        var now = UtcNow;
        foreach (var saved in store?.Load() ?? [])
        {
            if (string.IsNullOrWhiteSpace(saved.AccountKey)) continue;
            var remaining = saved.NextProbeAtUtc - now;
            remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining > ProbeInterval ? ProbeInterval : remaining;
            _states[saved.AccountKey] = new State
            {
                Held = true, CredentialDigest = store!.Unprotect(saved.ProtectedCredentialDigest),
                ProtectedDigest = saved.ProtectedCredentialDigest,
                NextProbeUtc = now + remaining, NextProbeStamp = After(remaining),
                NotificationDelivered = saved.NotificationDelivered
            };
        }
        // An unreadable store is quarantined by AuthHoldStore and we start with no holds.
        // Storage failure is not evidence of a rejected password, and holding every account
        // would stop all logins; a genuine rejection re-arms the hold on its first attempt.
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private long After(TimeSpan delay) => _clock.GetTimestamp() + (long)(delay.TotalSeconds * _clock.TimestampFrequency);

    private State Get(string key)
    {
        if (_states.TryGetValue(key, out var state)) return state;
        state = new State();
        _states[key] = state;
        return state;
    }

    internal bool IsHeld(FtpLoginAccount account)
    {
        lock (_lock) return _states.TryGetValue(account.Key, out var state) && state.Held;
    }

    internal bool HasChangedHeldCredentials(FtpLoginAccount account, string password)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        lock (_lock)
            return _states.TryGetValue(account.Key, out var state) && state.Held &&
                state.CredentialDigest is { } previous &&
                !CryptographicOperations.FixedTimeEquals(previous, digest);
    }

    internal DateTime? RetryAt(FtpLoginAccount account)
    {
        lock (_lock)
            return _states.TryGetValue(account.Key, out var state) && state.Held ? state.NextProbeUtc : null;
    }

    internal void RequestManualRetry(FtpLoginAccount account)
    {
        lock (_lock)
        {
            var state = Get(account.Key);
            ReadyRetry(state);
            SaveNoLock();
        }
    }

    private void ReadyRetry(State state)
    {
        state.Generation++;
        state.NextProbeUtc = UtcNow;
        state.NextProbeStamp = _clock.GetTimestamp();
        state.FaultUntilStamp = 0;
        state.ConsecutiveFaults = 0;
        // Keep any in-flight probe reserved until its actual connect task settles.
        // Resetting the generation must not admit a second concurrent handshake.
    }

    internal FtpLoginAttempt Begin(FtpLoginAccount account, string password, bool manualRetry, bool allowRecoveryProbe)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        FtpLoginAttempt? attempt = null;
        FtpLoginHeldException? blocked = null;
        var credentialChanged = false;
        lock (_lock)
        {
            var state = Get(account.Key);
            // A ghost login cannot test or reset a rejected normal account. Its
            // !username may itself be unsupported, even with a correct password.
            if (state.Held && !allowRecoveryProbe)
                blocked = Held(state);
            else
            {
                if (manualRetry) ReadyRetry(state);
                if (state.Held && state.CredentialDigest is { } previous
                    && !CryptographicOperations.FixedTimeEquals(previous, digest))
                {
                    ReadyRetry(state);
                    credentialChanged = true;
                    state.CredentialDigest = digest;
                    state.ProtectedDigest = _store?.Protect(digest);
                }
                if (state.Held)
                {
                    if (state.ProbeId != null || _clock.GetTimestamp() < state.NextProbeStamp)
                        blocked = Held(state);
                    else
                    {
                        state.ProbeId = ++_nextAttempt;
                        state.NextProbeUtc = UtcNow + ProbeInterval;
                        state.NextProbeStamp = After(ProbeInterval);
                        state.CredentialDigest = digest;
                        state.ProtectedDigest = _store?.Protect(digest);
                        // Claim and persist the next interval BEFORE opening a socket.
                        // A restart during the probe cannot create another immediate one.
                        if (!SaveNoLock())
                        {
                            state.ProbeId = null;
                            blocked = new("Authentication recovery probe blocked: hold state could not be saved", true, state.NextProbeUtc);
                        }
                        else attempt = new(account.Key, state.Generation, state.ProbeId.Value, true, false, digest, account, credentialChanged);
                    }
                }
                else if (!manualRetry && _clock.GetTimestamp() < state.FaultUntilStamp)
                    blocked = new("FTP login is backing off after a connection fault", false, state.FaultUntilUtc);
                else attempt = new(account.Key, state.Generation, ++_nextAttempt, false, !allowRecoveryProbe, digest, account);
            }
        }
        DeliverPending(account);
        if (blocked != null) throw blocked;
        return attempt!;
    }

    private static FtpLoginHeldException Held(State state) =>
        new("FTP authentication is on hold; change the password, retry manually, or wait for the next slow probe", true, state.NextProbeUtc);

    internal void Succeeded(FtpLoginAttempt attempt)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(attempt.Key, out var state)) return;
            if (state.ProbeId == attempt.Id) state.ProbeId = null;
            if (state.Generation != attempt.Generation || attempt.Ghost) return;
            if (attempt.RecoveryProbe)
            {
                state.Held = false;
                state.Generation++;
                state.NotificationDelivered = false;
                state.NotificationInFlight = false;
                state.NotificationEpisode++;
                SaveNoLock();
            }
            state.FaultUntilStamp = 0;
            state.ConsecutiveFaults = 0;
        }
    }

    internal void Failed(FtpLoginAttempt attempt, Failure failure)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(attempt.Key, out var state)) return;
            if (state.ProbeId == attempt.Id) state.ProbeId = null;
            if (state.Generation != attempt.Generation) return;
            // Rejection of !username can mean the feature is unsupported. It must
            // not falsely indict the password accepted by the ordinary account.
            if (attempt.Ghost && failure == Failure.AuthRejected) failure = Failure.ConnectFault;
            if (failure == Failure.AuthRejected)
            {
                if (!state.Held)
                {
                    state.Held = true;
                    state.Generation++; // Earlier parallel connections cannot close this new hold.
                    state.NotificationDelivered = false;
                    state.NotificationInFlight = false;
                    state.NotificationEpisode++;
                    state.CredentialDigest = attempt.CredentialDigest;
                    state.ProtectedDigest = _store?.Protect(attempt.CredentialDigest);
                }
                state.NextProbeUtc = UtcNow + ProbeInterval;
                state.NextProbeStamp = After(ProbeInterval);
                SaveNoLock();
            }
            else if (failure == Failure.ConnectFault && ++state.ConsecutiveFaults >= FaultBurstThreshold)
            {
                state.FaultUntilStamp = After(FaultBackoff);
                state.FaultUntilUtc = UtcNow + FaultBackoff;
            }
            // Abandonment, transport faults and BNC pressure during an auth probe
            // never clear the hold or shorten its already reserved 30-minute interval.
        }
        DeliverPending(attempt.Account);
    }

    private bool SaveNoLock() => _store?.Save(_states.Where(p => p.Value.Held).Select(p =>
        new StoredAuthHold(p.Key, p.Value.ProtectedDigest, p.Value.NextProbeUtc, p.Value.NotificationDelivered)).ToList()) ?? true;

    private void DeliverPending(FtpLoginAccount account)
    {
        State state;
        Func<FtpAuthenticationNotice, bool> sink;
        FtpAuthenticationNotice notice;
        long episode;
        lock (_lock)
        {
            state = Get(account.Key);
            if (!state.Held || state.NotificationDelivered || state.NotificationInFlight || NotificationSink == null) return;
            state.NotificationInFlight = true;
            episode = state.NotificationEpisode;
            sink = NotificationSink;
            notice = new(account, state.NextProbeUtc);
        }
        var delivered = false;
        try { delivered = sink(notice); }
        catch (Exception ex) { Log.Warning(ex, "Authentication notification failed"); }
        finally
        {
            lock (_lock)
            {
                if (state.NotificationEpisode == episode) state.NotificationInFlight = false;
                if (state.NotificationEpisode == episode && state.Held && delivered)
                {
                    state.NotificationDelivered = true;
                    SaveNoLock();
                }
            }
        }
    }
}
