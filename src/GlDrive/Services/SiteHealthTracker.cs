using System.IO;
using System.Text.Json;
using GlDrive.Ftp;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Services;

public enum SiteHealthState { Healthy, Degraded, Down, Quarantined, AuthRejected }
public enum SiteFailureKind { Unknown, Unreachable, Timeout, Refused, LoginLimit, LocalCapacity, Authentication, BncCooldown }
public sealed record SiteHealthSnapshot(string ServerId, SiteHealthState State, SiteFailureKind FailureClass,
    DateTimeOffset? FirstFailureUtc, int Attempts, int EscalationStep, DateTimeOffset? NextRetryUtc,
    DateTimeOffset? LastProbeUtc, DateTimeOffset? LastSuccessUtc, bool PersistenceHealthy, string Message);
public sealed record SiteHealthNotice(string ServerId, SiteHealthState State, bool Recovered, string Message);

internal sealed record SiteHealthEntry
{
    public string ServerId { get; init; } = "";
    public string EndpointIdentity { get; init; } = "";
    public SiteHealthState State { get; init; } = SiteHealthState.Degraded;
    public SiteFailureKind FailureClass { get; init; }
    public DateTimeOffset? FirstFailureUtc { get; init; }
    public int Attempts { get; init; }
    public int EscalationStep { get; init; }
    public DateTimeOffset? NextRetryUtc { get; init; }
    public DateTimeOffset? LastProbeUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? LastObservationUtc { get; init; }
    public bool OutageNotified { get; init; }
}

/// <summary>Persistent outage episodes. It never opens a socket or schedules a timer.</summary>
public sealed class SiteHealthTracker
{
    private readonly Lock _sync = new();
    private readonly Action<string> _write;
    private Dictionary<string, SiteHealthEntry> _entries = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public bool PersistenceHealthy { get; private set; } = true;

    public SiteHealthTracker(string path) : this(() => File.Exists(path) ? File.ReadAllText(path) : null, json =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SecureFile.WriteAllTextRestricted(path, json);
    }, () => File.Copy(path, path + ".corrupt", true)) { }

    /// <summary>
    /// Health state is advisory backoff. An unreadable or unwritable file must never hold every
    /// automatic login: the unreadable copy is quarantined and tracking restarts empty, and a
    /// failed write keeps the in-memory ladder (it only loses persistence across a restart).
    /// </summary>
    internal SiteHealthTracker(Func<string?> read, Action<string> write, Action? quarantine = null)
    {
        _write = write;
        try
        {
            var json = read();
            if (json == null) return;
            var loaded = JsonSerializer.Deserialize<List<SiteHealthEntry>>(json, JsonOptions)
                ?? throw new InvalidDataException("Empty health state");
            if (loaded.Count > 4096 || loaded.Any(e => e == null || string.IsNullOrWhiteSpace(e.ServerId) ||
                string.IsNullOrWhiteSpace(e.EndpointIdentity) || e.Attempts < 0 || e.EscalationStep is < 0 or > 3 ||
                (e.FirstFailureUtc != null && e.NextRetryUtc == null) ||
                (e.FirstFailureUtc == null && (e.Attempts > 0 || e.EscalationStep > 0 || e.OutageNotified)) ||
                !Enum.IsDefined(e.State) || !Enum.IsDefined(e.FailureClass)) ||
                loaded.Select(e => e.ServerId).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
                throw new InvalidDataException("Invalid health state");
            _entries = loaded.ToDictionary(entry => entry.ServerId, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _entries = new(StringComparer.Ordinal);
            PersistenceHealthy = false;
            Log.Warning(ex, "Site health state could not be read; quarantined it and starting with no outage history");
            try { quarantine?.Invoke(); } catch { }
        }
    }

    private SiteHealthEntry Current(string serverId, string identity) =>
        _entries.TryGetValue(serverId, out var entry) && entry.EndpointIdentity == identity
            ? entry : new() { ServerId = serverId, EndpointIdentity = identity };

    public SiteHealthSnapshot Get(string serverId, string identity)
    {
        lock (_sync)
        {
            var entry = Current(serverId, identity);
            return new(serverId, entry.State, entry.FailureClass,
                entry.FirstFailureUtc, entry.Attempts, entry.EscalationStep, entry.NextRetryUtc,
                entry.LastProbeUtc, entry.LastSuccessUtc, PersistenceHealthy,
                entry.FirstFailureUtc == null ? entry.LastSuccessUtc == null ? "No FTP health observation yet" : "FTP recovered" :
                SiteHealthPolicy.Describe(entry.FailureClass));
        }
    }

    public bool CanAttempt(string serverId, string identity, DateTimeOffset now)
    {
        lock (_sync) return Current(serverId, identity).NextRetryUtc is not { } due || due <= now;
    }

    public SiteHealthNotice? Failed(string serverId, string identity, SiteFailureKind failure, DateTimeOffset now)
    {
        lock (_sync)
        {
            // A login limit is the account's own sessions holding its slots: the site answered,
            // so it is not an outage, and the ladder would also block the !user ghost recovery.
            if (failure is SiteFailureKind.LocalCapacity or SiteFailureKind.LoginLimit) return null;
            var previous = Current(serverId, identity);
            if (previous.LastObservationUtc >= now) return null;
            var first = previous.FirstFailureUtc ?? now;
            var attempts = Math.Min(previous.Attempts + 1L, int.MaxValue);
            var elapsed = now >= first ? now - first : TimeSpan.Zero;
            var delay = SiteHealthPolicy.RetryDelay((int)attempts, elapsed, previous.EscalationStep, failure);
            var state = SiteHealthPolicy.StateFor(failure, elapsed);
            var next = previous with
            {
                FirstFailureUtc = first, Attempts = (int)attempts, FailureClass = failure, State = state,
                EscalationStep = elapsed >= TimeSpan.FromMinutes(30) ? Math.Min(3, previous.EscalationStep + 1) : previous.EscalationStep,
                NextRetryUtc = now.Add(delay), OutageNotified = true, LastObservationUtc = now
            };
            if (!Save(next)) return null;
            return previous.OutageNotified ? null : new(serverId, state, false, SiteHealthPolicy.Describe(failure));
        }
    }

    public SiteHealthNotice? Recovered(string serverId, string identity, DateTimeOffset now)
    {
        lock (_sync)
        {
            var previous = Current(serverId, identity);
            if (previous.LastObservationUtc >= now) return null;
            var next = previous with
            {
                State = SiteHealthState.Healthy, FailureClass = SiteFailureKind.Unknown, FirstFailureUtc = null,
                Attempts = 0, EscalationStep = 0, NextRetryUtc = null, LastSuccessUtc = now, OutageNotified = false,
                LastObservationUtc = now
            };
            if (!Save(next)) return null;
            return previous.OutageNotified ? new(serverId, SiteHealthState.Healthy, true, "FTP connection recovered") : null;
        }
    }

    /// <summary>Imports A9's existing rejection evidence without inventing another login attempt.</summary>
    public SiteHealthNotice? AuthenticationHeld(string serverId, string identity, DateTimeOffset now, DateTimeOffset? retryAt)
    {
        lock (_sync)
        {
            var previous = Current(serverId, identity);
            var due = retryAt ?? now.AddMinutes(30);
            if (previous.NextRetryUtc > due) due = previous.NextRetryUtc.Value;
            var next = previous with
            {
                FirstFailureUtc = previous.FirstFailureUtc ?? now,
                State = SiteHealthState.AuthRejected, FailureClass = SiteFailureKind.Authentication,
                NextRetryUtc = due, OutageNotified = true, LastObservationUtc = previous.LastObservationUtc ?? now
            };
            if (previous == next || !Save(next)) return null;
            return previous.OutageNotified ? null : new(serverId, next.State, false, SiteHealthPolicy.Describe(next.FailureClass));
        }
    }

    public bool TryReserveProbe(string serverId, string identity, DateTimeOffset now)
    {
        lock (_sync)
        {
            var entry = Current(serverId, identity);
            if (!SiteHealthPolicy.ProbeAllowed(entry.FailureClass, entry.FirstFailureUtc, entry.LastProbeUtc, now)) return false;
            return Save(entry with { LastProbeUtc = now });
        }
    }

    private bool Save(SiteHealthEntry entry)
    {
        var next = new Dictionary<string, SiteHealthEntry>(_entries, StringComparer.Ordinal) { [entry.ServerId] = entry };
        if (next.Count > 4096) return false; // one entry per configured server; never reached in practice
        _entries = next;
        try
        {
            _write(JsonSerializer.Serialize(next.Values.ToArray(), JsonOptions));
            PersistenceHealthy = true;
        }
        catch (Exception ex)
        {
            if (PersistenceHealthy)
                Log.Warning(ex, "Site health persistence failed; the retry ladder continues in memory");
            PersistenceHealthy = false;
        }
        return true;
    }
}

public static class SiteHealthPolicy
{
    public static bool ExistingConnectionCanRecover(SiteFailureKind failure) => failure != SiteFailureKind.Authentication;

    public static TimeSpan RetryDelay(int attempt, TimeSpan outage, int escalationStep, SiteFailureKind failure)
    {
        var delay = outage < TimeSpan.FromMinutes(30) ? MountRetryPolicy.DelayFor(attempt) :
            escalationStep <= 0 ? TimeSpan.FromMinutes(15) : escalationStep == 1 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(4);
        // Existing authentication/BNC protection remains a floor, never a reason
        // to shorten the persisted long-outage ladder.
        var floor = failure == SiteFailureKind.Authentication ? TimeSpan.FromMinutes(30) :
            failure == SiteFailureKind.BncCooldown ? TimeSpan.FromHours(2) : TimeSpan.Zero;
        return delay > floor ? delay : floor;
    }

    public static SiteHealthState StateFor(SiteFailureKind failure, TimeSpan outage) =>
        failure == SiteFailureKind.Authentication ? SiteHealthState.AuthRejected :
        failure == SiteFailureKind.BncCooldown || outage >= TimeSpan.FromMinutes(30) ? SiteHealthState.Quarantined :
        failure is SiteFailureKind.Unreachable or SiteFailureKind.Timeout ? SiteHealthState.Down : SiteHealthState.Degraded;

    public static bool ProbeAllowed(SiteFailureKind failure, DateTimeOffset? firstFailure, DateTimeOffset? previousProbe, DateTimeOffset now) =>
        failure is SiteFailureKind.Unreachable or SiteFailureKind.Timeout &&
        firstFailure.HasValue && now - firstFailure >= TimeSpan.FromMinutes(30) &&
        (!previousProbe.HasValue || now - previousProbe >= TimeSpan.FromMinutes(15));

    public static string Describe(SiteFailureKind failure) => failure switch
    {
        SiteFailureKind.Unreachable => "FTP host is unreachable",
        SiteFailureKind.Timeout => "FTP connection attempt timed out",
        SiteFailureKind.Authentication => "FTP credentials were rejected",
        SiteFailureKind.BncCooldown => "FTP service requested a cooldown",
        SiteFailureKind.LoginLimit => "FTP account login limit was reached",
        SiteFailureKind.LocalCapacity => "Local FTP login budget is occupied",
        SiteFailureKind.Refused => "FTP connection was refused",
        _ => "FTP connection is unavailable; failure cause is not established"
    };
}
