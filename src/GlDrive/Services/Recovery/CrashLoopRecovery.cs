using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;

namespace GlDrive.Services.Recovery;

internal sealed record CrashRestartOrigin(string SessionId, string ChainId, bool WasSafeMode)
{
    internal static CrashRestartOrigin? Parse(string[] args)
    {
        // Deliberate restarts always start a new chain, even with conflicting stale arguments.
        if (args.Any(arg => arg is "--restart-after" or "--update-restart" or "--apply-update" or "--apply-update-task" or "--cleanup-old-updates" or "--rollback-update")) return null;
        string? Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length && args.Count(arg => arg == flag) == 1 ? args[index + 1] : null;
        }
        return Guid.TryParseExact(Value("--crash-restart"), "N", out var session) && session != Guid.Empty &&
            Guid.TryParseExact(Value("--crash-chain"), "N", out var chain) && chain != Guid.Empty
            ? new(session.ToString("N"), chain.ToString("N"), args.Contains("--crash-safe")) : null;
    }

    internal string Arguments(bool recovery) => $"--crash-restart {SessionId} --crash-chain {ChainId}" +
        (WasSafeMode ? " --crash-safe" : "") + (recovery ? " --recovery" : "");
}

internal sealed record CrashRecoveryEvent(string SessionId, string ChainId, DateTimeOffset TimeUtc, bool WasSafeMode, bool EnteredLoop);
internal sealed record CrashRecoveryDecision(bool SafeMode, int RecentCrashes, int SafeModeCrashes, int? RestartDelaySeconds);

internal static class CrashLoopPolicy
{
    internal static CrashRecoveryDecision Evaluate(IEnumerable<CrashRecoveryEvent> source, string chainId, DateTimeOffset now, RecoveryConfig settings)
    {
        var events = source.Where(e => e.ChainId == chainId && e.TimeUtc <= now)
            .DistinctBy(e => e.SessionId).ToArray();
        var recent = events.Count(e => e.TimeUtc >= now.AddMinutes(-Math.Clamp(settings.CrashLoopWindowMinutes, 1, 60)));
        var safe = settings.CrashLoopProtectionEnabled &&
            (recent >= Math.Clamp(settings.CrashLoopThreshold, 3, 20) || events.Length >= 3 && events.Any(e => e.EnteredLoop));
        var safeCrashes = events.Count(e => e.WasSafeMode);
        var delays = settings.SafeModeRetryDelaysSeconds ?? [];
        // If even recovery cannot stay up, give normal startup (and its updater) one
        // fresh chance after the configured backoff instead of leaving the box stopped.
        if (safe && safeCrashes >= Math.Min(delays.Length, 8)) safe = false;
        int? delay = !safe ? 3 : Math.Clamp(delays[safeCrashes], 3, 3600);
        return new(safe, recent, safeCrashes, delay);
    }
}

/// <summary>Bounded bookkeeping: corrupt input starts empty; failed writes retain memory.</summary>
internal sealed class CrashLoopHistory
{
    internal const int Capacity = 256;
    private readonly string _path;
    private readonly Action<string, string> _write;
    private readonly Action<string> _warn;
    private readonly List<CrashRecoveryEvent> _events = [];
    private bool _warned;

    internal CrashLoopHistory(string path, Action<string>? warn = null, Action<string, string>? write = null)
    {
        _path = path; _warn = warn ?? (_ => { }); _write = write ?? SecureFile.WriteAllTextRestricted;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Oversized recovery history");
            var entries = JsonSerializer.Deserialize<List<CrashRecoveryEvent>>(File.ReadAllText(path)) ?? throw new JsonException();
            if (entries.Any(e => e == null || !Guid.TryParseExact(e.SessionId, "N", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(e.ChainId, "N", out var chain) || chain == Guid.Empty || e.TimeUtc == default)) throw new JsonException();
            _events.AddRange(entries.DistinctBy(e => (e.ChainId, e.SessionId)).OrderBy(e => e.TimeUtc).TakeLast(Capacity));
        }
        catch
        {
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            Warn("Crash recovery history was unreadable; starting with empty evidence.");
        }
    }

    internal CrashRecoveryDecision Record(CrashRestartOrigin origin, DateTimeOffset now, RecoveryConfig settings)
    {
        // Keep this chain's confirmed-loop evidence until an explicit normal launch
        // chooses a new chain, even if the recovery window stays open for many days.
        _events.RemoveAll(e => e.TimeUtc > now || e.ChainId != origin.ChainId && e.TimeUtc < now.AddDays(-7));
        if (!_events.Any(e => e.SessionId == origin.SessionId && e.ChainId == origin.ChainId))
            _events.Add(new(origin.SessionId, origin.ChainId, now, origin.WasSafeMode, false));
        _events.Sort((first, second) => first.TimeUtc.CompareTo(second.TimeUtc));
        if (_events.Count > Capacity) _events.RemoveRange(0, _events.Count - Capacity);
        var decision = CrashLoopPolicy.Evaluate(_events, origin.ChainId, now, settings);
        if (decision.SafeMode && !_events.Any(e => e.ChainId == origin.ChainId && e.EnteredLoop))
        {
            var index = _events.FindLastIndex(e => e.ChainId == origin.ChainId);
            _events[index] = _events[index] with { EnteredLoop = true };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _write(_path, JsonSerializer.Serialize(_events));
        }
        catch { Warn("Crash recovery history could not be saved; retaining current evidence in memory."); }
        return decision;
    }

    internal IReadOnlyList<CrashRecoveryEvent> Snapshot() => _events.ToArray();
    private void Warn(string message) { if (_warned) return; _warned = true; try { _warn(message); } catch { } }
}

internal sealed record CrashRecoveryStartup(string SessionId, string ChainId, RecoveryConfig Settings, CrashRecoveryDecision Decision)
{
    internal const int MaxBootstrapProfileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal CrashRestartOrigin NextCrash => new(SessionId, ChainId, Decision.SafeMode);

    internal static CrashRecoveryStartup Prepare(string[] args, string profileRoot, DateTimeOffset now, Action<string>? warn = null)
    {
        var settings = ReadSettings(Path.Combine(profileRoot, "appsettings.json"));
        var origin = CrashRestartOrigin.Parse(args);
        if (origin != null && !HasCrashEvidence(profileRoot, origin)) origin = null;
        var chain = origin?.ChainId ?? Guid.NewGuid().ToString("N");
        var decision = origin == null ? new CrashRecoveryDecision(false, 0, 0, 3) :
            new CrashLoopHistory(Path.Combine(profileRoot, "watchdog-restarts.json"), warn).Record(origin, now, settings);
        if (origin?.WasSafeMode == true && !decision.SafeMode) chain = Guid.NewGuid().ToString("N");
        return new(Guid.NewGuid().ToString("N"), chain, settings, decision);
    }

    private static bool HasCrashEvidence(string profileRoot, CrashRestartOrigin origin)
    {
        var path = Path.Combine(profileRoot, ".running");
        if (!File.Exists(path)) return false;
        string? marker = null;
        try { marker = File.ReadAllText(path); } catch { }
        // Restart Manager reuses WER's registered arguments after an intended installer
        // shutdown. The marker, rather than those arguments, decides crash identity.
        return ExitMarker.ClassifyForWatchdog(true, marker) == WatchdogExitVerdict.Unclean &&
            !ExitMarker.BelongsToDifferentSession(marker, origin.SessionId);
    }

    internal static RecoveryConfig ReadSettings(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBootstrapText(path));
            return document.RootElement.TryGetProperty("recovery", out var value) ? value.Deserialize<RecoveryConfig>(Json) ?? new() : new();
        }
        catch { return new(); }
    }

    // Safe/offline modes must not call ConfigManager.Load: its migrations can save settings
    // and access Credential Manager before the user has chosen a recovery operation.
    internal static AppConfig ReadConfiguration(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(ReadBootstrapText(path), Json) ?? new();
            // Presentation/recovery must remain usable even when an ordinary section was
            // hand-edited to null. This is only an in-memory fallback, never a config save.
            var defaults = new AppConfig();
            foreach (var property in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
                if (property.GetValue(config) == null) property.SetValue(config, property.GetValue(defaults));
            return config;
        }
        catch { return new(); }
    }

    private static string ReadBootstrapText(string path)
    {
        // This read runs before the watchdog exists, and safe-mode presentation must
        // remain available if the normal profile is too large to load. The normal
        // ConfigManager path is unchanged; this bound only supplies recovery defaults.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBootstrapProfileBytes) throw new InvalidDataException("Profile exceeds recovery bootstrap read size");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + count > MaxBootstrapProfileBytes) throw new InvalidDataException("Profile grew during recovery bootstrap read");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
