using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using FluentFTP.Exceptions;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;
using Failure = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;

namespace GlDrive.Ftp;

public enum LoginHoldReason { None, Refused, LoginLimit, BncCooldown, Unreachable, ConnectTimeout }

public sealed record LoginBudgetSnapshot(bool Enabled, int Limit, int AttemptsInWindow, int ReservedGhostSlots,
    DateTime? RetryAtUtc, LoginHoldReason HoldReason, bool PersistenceHealthy);

internal sealed record GovernedLoginAttempt(string Key, long Id, long Generation, bool Ghost, bool Manual,
    DateTime PreviousGhostUtc, long PreviousGhostStamp);

/// <summary>Counts only explicit new login attempts. It never gates an already connected pool lease.</summary>
internal sealed class LoginAttemptGovernor
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan GhostInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private bool _persistenceHealthy = true;
    private long _nextId;

    private sealed record AttemptStamp(long Id, long Stamp, DateTime Utc, bool Ghost);
    private sealed class State
    {
        internal readonly List<AttemptStamp> Attempts = [];
        internal bool Enabled = true;
        internal int Limit = 8;
        internal DateTime HoldUntilUtc;
        internal long HoldUntilStamp;
        internal LoginHoldReason Reason;
        internal long Generation;
        internal DateTime GhostUntilUtc;
        internal long GhostUntilStamp;
        internal long LastGhostId;
        internal bool ManualRequested;
        internal DateTime LastDenialUntilUtc;
    }
    private sealed record StoredAttempt(DateTime AtUtc, bool Ghost);
    private sealed record StoredState(string Key, StoredAttempt[] Attempts, DateTime HoldUntilUtc,
        LoginHoldReason Reason, DateTime GhostUntilUtc);

    internal LoginAttemptGovernor(TimeProvider? clock = null, string? stateFilePath = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = stateFilePath;
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new JsonException("FTP login budget state is too large");
            var rows = JsonSerializer.Deserialize<StoredState[]>(File.ReadAllText(_path));
            if (rows == null || rows.Length > 1024 || rows.Any(row => row.Key.Length != 64 ||
                !row.Key.All(Uri.IsHexDigit) || row.Attempts.Length > 1024 || !Enum.IsDefined(row.Reason) ||
                row.Attempts.Any(attempt => attempt.AtUtc.Kind != DateTimeKind.Utc)))
                throw new JsonException("Invalid FTP login budget state");
            foreach (var row in rows)
            {
                var state = new State { Reason = row.Reason };
                foreach (var attempt in row.Attempts)
                {
                    var remaining = Clamp(attempt.AtUtc + Window - UtcNow, Window);
                    if (remaining > TimeSpan.Zero)
                        state.Attempts.Add(new(++_nextId, After(remaining - Window), UtcNow + remaining - Window, attempt.Ghost));
                }
                var hold = Clamp(row.HoldUntilUtc - UtcNow, TimeSpan.FromHours(2));
                state.HoldUntilUtc = UtcNow + hold;
                state.HoldUntilStamp = After(hold);
                var ghost = Clamp(row.GhostUntilUtc - UtcNow, GhostInterval);
                state.GhostUntilUtc = UtcNow + ghost;
                state.GhostUntilStamp = After(ghost);
                _states.Add(row.Key, state);
            }
        }
        catch (Exception ex)
        {
            // A local bookkeeping file must never stop every FTP login; keep the
            // unreadable copy for diagnosis and start with an empty budget.
            _states.Clear();
            Log.Warning(ex, "FTP login budget state is unreadable; quarantined it and starting with an empty budget");
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
        }
    }

    internal DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private long After(TimeSpan delay) => _clock.GetTimestamp() + (long)(delay.TotalSeconds * _clock.TimestampFrequency);
    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) => value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;

    private State Get(string key)
    {
        if (_states.TryGetValue(key, out var state)) return state;
        if (_states.Count >= 1024)
            throw new FtpLoginHeldException("FTP login budget account capacity reached; retry manually after reviewing configuration", false, null, loginRateHold: true);
        return _states[key] = new();
    }

    private void Prune(State state)
    {
        var cutoff = After(-Window);
        state.Attempts.RemoveAll(attempt => attempt.Stamp <= cutoff);
    }

    internal void RequestManualRetry(FtpLoginAccount account)
    {
        lock (_sync) Get(account.Key).ManualRequested = true;
    }

    internal bool ConsumeManualRetry(FtpLoginAccount account)
    {
        lock (_sync)
        {
            var state = Get(account.Key);
            var requested = state.ManualRequested;
            state.ManualRequested = false;
            return requested;
        }
    }

    internal GovernedLoginAttempt Begin(FtpLoginAccount account, PoolConfig? policy, bool manualRetry, bool ghost)
    {
        lock (_sync)
        {
            var state = Get(account.Key);
            if (policy != null)
            {
                state.Enabled = policy.LoginRateLimitEnabled;
                state.Limit = Math.Clamp(policy.MaxLoginAttemptsPerFiveMinutes, 2, 100);
            }
            Prune(state);
            var manual = manualRetry || state.ManualRequested;
            var now = _clock.GetTimestamp();
            if (state.Enabled && !manual)
            {
                var ghostRecovery = ghost && state.Reason == LoginHoldReason.LoginLimit;
                if (state.HoldUntilStamp > now && !ghostRecovery)
                    Deny(state, "FTP new logins are on an account-wide hold", state.HoldUntilUtc);
                if (ghost && state.GhostUntilStamp > now)
                    Deny(state, "FTP ghost recovery is rate limited", state.GhostUntilUtc);
                // Leave one slot for the existing !user recovery path. It still
                // counts toward the same total and has an account-wide minute gap.
                var ordinary = state.Attempts.Where(attempt => !attempt.Ghost).ToArray();
                if (state.Attempts.Count >= state.Limit || !ghost && ordinary.Length >= state.Limit - 1)
                {
                    var relevant = state.Attempts.Count >= state.Limit ? state.Attempts : ordinary.AsEnumerable();
                    var oldest = relevant.MinBy(attempt => attempt.Stamp)!;
                    var remaining = TimeSpan.FromSeconds((oldest.Stamp + (long)(Window.TotalSeconds * _clock.TimestampFrequency) - now) /
                        (double)_clock.TimestampFrequency);
                    Deny(state, "Account login cap reached — new-login rate budget exhausted", UtcNow + remaining);
                }
            }
            state.ManualRequested = false;
            var id = ++_nextId;
            var attempt = new GovernedLoginAttempt(account.Key, id, state.Generation, ghost, manual,
                state.GhostUntilUtc, state.GhostUntilStamp);
            state.Attempts.Add(new(id, now, UtcNow, ghost));
            if (state.Attempts.Count > 1024) state.Attempts.RemoveAt(0);
            if (ghost)
            {
                state.LastGhostId = id;
                state.GhostUntilUtc = UtcNow + GhostInterval;
                state.GhostUntilStamp = After(GhostInterval);
            }
            // The in-memory budget still applies when the file cannot be written (Save
            // warns); persistence only carries the window across a restart.
            Save();
            return attempt;
        }
    }

    private static void Deny(State state, string message, DateTime? retry)
    {
        var marker = retry is { } deadline
            ? new DateTime(deadline.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, deadline.Kind)
            : DateTime.MaxValue;
        if (state.LastDenialUntilUtc != marker)
        {
            state.LastDenialUntilUtc = marker;
            Log.Information("{Reason}; retry after {RetryAtUtc}", message, retry);
        }
        throw new FtpLoginHeldException(message, false, retry, loginRateHold: true);
    }

    internal void CancelBeforeConnect(GovernedLoginAttempt attempt)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(attempt.Key, out var state)) return;
            state.Attempts.RemoveAll(entry => entry.Id == attempt.Id);
            if (attempt.Ghost && state.LastGhostId == attempt.Id)
            {
                state.GhostUntilStamp = attempt.PreviousGhostStamp;
                state.GhostUntilUtc = attempt.PreviousGhostUtc;
            }
            Save();
        }
    }

    internal void Complete(GovernedLoginAttempt attempt, Failure? failure, bool timedOut, bool bncCooldown)
    {
        lock (_sync)
        {
            var state = Get(attempt.Key);
            if (failure == null)
            {
                if (state.Generation == attempt.Generation)
                {
                    state.HoldUntilStamp = 0;
                    state.HoldUntilUtc = default;
                    state.Reason = LoginHoldReason.None;
                }
            }
            else if (failure != Failure.CallerAbandoned)
            {
                var (delay, reason) = bncCooldown ? (TimeSpan.FromHours(2), LoginHoldReason.BncCooldown)
                    : failure == Failure.BncLoginLimit ? (TimeSpan.FromSeconds(90), LoginHoldReason.LoginLimit)
                    : failure == Failure.ServerRefused ? (TimeSpan.FromSeconds(90), LoginHoldReason.Refused)
                    : failure == Failure.HostUnreachable ? (TimeSpan.FromMinutes(1), LoginHoldReason.Unreachable)
                    : timedOut ? (TimeSpan.FromMinutes(1), LoginHoldReason.ConnectTimeout)
                    : (TimeSpan.Zero, LoginHoldReason.None);
                if (delay > TimeSpan.Zero && After(delay) > state.HoldUntilStamp)
                {
                    state.HoldUntilStamp = After(delay);
                    state.HoldUntilUtc = UtcNow + delay;
                    state.Reason = reason;
                    state.Generation++;
                }
            }
            Save();
        }
    }

    internal LoginBudgetSnapshot Snapshot(FtpLoginAccount account)
    {
        lock (_sync)
        {
            // Diagnostics must not create account state or consume bookkeeping capacity.
            if (!_states.TryGetValue(account.Key, out var state))
                return new(true, 8, 0, 1, null, LoginHoldReason.None, _persistenceHealthy);
            var now = _clock.GetTimestamp();
            var cutoff = now - (long)(Window.TotalSeconds * _clock.TimestampFrequency);
            var attempts = state.Attempts.Where(attempt => attempt.Stamp > cutoff).ToArray();
            var ordinary = attempts.Where(attempt => !attempt.Ghost).ToArray();
            var hold = state.Enabled && state.HoldUntilStamp > now;
            DateTime? retry = hold ? state.HoldUntilUtc : null;
            if (attempts.Length >= state.Limit || ordinary.Length >= state.Limit - 1)
            {
                var relevant = attempts.Length >= state.Limit ? attempts : ordinary;
                var oldest = relevant.MinBy(attempt => attempt.Stamp)!;
                var remaining = TimeSpan.FromSeconds((oldest.Stamp +
                    (long)(Window.TotalSeconds * _clock.TimestampFrequency) - now) / (double)_clock.TimestampFrequency);
                var rateRetry = UtcNow + remaining;
                if (retry == null || rateRetry > retry) retry = rateRetry;
            }
            return new(state.Enabled, state.Limit, attempts.Length, 1, state.Enabled ? retry : null,
                hold ? state.Reason : LoginHoldReason.None, _persistenceHealthy);
        }
    }

    private bool Save()
    {
        if (_path == null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(_states.Select(pair => new StoredState(pair.Key,
                pair.Value.Attempts.Select(attempt => new StoredAttempt(attempt.Utc, attempt.Ghost)).ToArray(),
                pair.Value.HoldUntilUtc, pair.Value.Reason, pair.Value.GhostUntilUtc)).ToArray()));
            _persistenceHealthy = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_persistenceHealthy) Log.Warning(ex, "FTP login budget state could not be saved");
            _persistenceHealthy = false;
            return false;
        }
    }

    internal static bool IsTimeout(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            if (cause.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                cause.Message.Contains("handshake", StringComparison.OrdinalIgnoreCase) ||
                ContainsReadWord(cause.Message)) return false;
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            var message = cause.Message;
            if (cause is SocketException { SocketErrorCode: SocketError.TimedOut } ||
                message.Contains("Timed out trying to connect", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection timed out", StringComparison.OrdinalIgnoreCase) ||
                cause is TimeoutException && message.Contains("connect", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Only a server reply that explicitly throttles earns the two-hour hold. A bare 421/450 is
    /// how most FTP daemons say "too many users" or "service not available", and a login-limit
    /// reply already has its own 90 s hold with ghost recovery. GlDrive's own exceptions (the
    /// pool's "Server in BNC cooldown" fast-fail) are local state, never server evidence.
    /// </summary>
    internal static bool IsBncCooldown(Exception error)
    {
        if (ConnectFailureClassifier.HasLoginLimitError(error)) return false;
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            var message = cause.Message ?? "";
            var serverReply = cause is FtpCommandException || StartsWithServerReply(message);
            if (serverReply && ThrottlePhrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static readonly string[] ThrottlePhrases =
        ["cooldown", "cool down", "cool-down", "rate limit", "rate-limit", "throttl", "too fast", "too frequent"];

    // Classifying a failed login must not itself throw a regex deadline exception and
    // replace the original authentication failure. These two lexical checks need no regex.
    private static bool ContainsReadWord(string message)
    {
        for (var start = 0; start < message.Length;)
        {
            if (!IsWordCharacter(message[start])) { start++; continue; }
            var end = start + 1;
            while (end < message.Length && IsWordCharacter(message[end])) end++;
            var word = message.AsSpan(start, end - start);
            if (word.Equals("read", StringComparison.OrdinalIgnoreCase) || word.Equals("reading", StringComparison.OrdinalIgnoreCase)) return true;
            start = end;
        }
        return false;
    }

    private static bool StartsWithServerReply(string message)
    {
        var text = message.AsSpan().TrimStart();
        return text.Length >= 3 && text[0] is '4' or '5' && char.IsAsciiDigit(text[1]) && char.IsAsciiDigit(text[2]) &&
            (text.Length == 3 || !IsWordCharacter(text[3]));
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) ||
        char.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.ConnectorPunctuation;
}
