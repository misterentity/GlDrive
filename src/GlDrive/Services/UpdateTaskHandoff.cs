using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Hand-off record between the non-elevated app and the SYSTEM scheduled task that applies an
/// update, replacing the interactive UAC prompt.
///
/// WHY THIS EXISTS. The auto-updater waits for the app to be idle before installing. On this box
/// that reliably landed at ~03:35, raised a UAC prompt with nobody at the keyboard, and the
/// Windows secure desktop timed the prompt out after 120s. <c>ShellExecute</c> returns
/// <c>ERROR_CANCELLED</c> for a timeout and for a real "No", so the app recorded a user decision
/// that never happened and suppressed the release for 24h — which re-offered it at the same bad
/// hour. Every daytime auto-install succeeded (v3.10.79 09:31, v3.10.81 09:36); both ~03:35
/// attempts "declined" after an invariant 131s/132s. The idle gate selects for exactly the hours
/// no human is present, then demands a human approve elevation.
///
/// SECURITY MODEL. The scheduled task runs as SYSTEM, so this file is attacker-writable input to
/// an elevated process and is treated as untrusted. It is deliberately NOT the security boundary:
///   * It carries no install destination. <see cref="UpdateChecker.ApplyUpdate"/> already requires
///     the install directory to equal the elevated process's OWN directory, so the destination is
///     pinned by where the task's action lives (Program Files) and cannot be redirected from here.
///   * It carries no code. The package is re-verified inside the elevated process against an RSA
///     public key compiled into the binary, and rejected unless the version is strictly newer than
///     the running one.
/// The worst a forged hand-off achieves is pointing the updater at a different *validly signed,
/// strictly newer* GlDrive package — which is what the updater does anyway.
///
/// It still validates aggressively, because cheap checks that shrink an elevated process's input
/// space are worth having even when they are not load-bearing.
/// </summary>
internal sealed record UpdateTaskHandoff(
    int Pid,
    string PackageDir,
    string Tag,
    DateTime CreatedUtc)
{
    /// <summary>Name of the SYSTEM scheduled task registered by the installer.</summary>
    public const string TaskName = "GlDrive Update Installer";

    /// <summary>
    /// A hand-off older than this is refused. The app writes one immediately before triggering the
    /// task, so a stale record means the trigger failed or the file was planted — either way,
    /// re-running an old install is not what the user is waiting for.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// %ProgramData%\GlDrive\pending-update.json — readable by SYSTEM, which cannot resolve the
    /// interactive user's %AppData%. The installer creates the directory and grants Users write.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GlDrive", "pending-update.json");

    public static void Write(string path, UpdateTaskHandoff handoff)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(handoff, JsonOpts));
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Debug(ex, "Could not clear update hand-off"); }
    }

    /// <summary>
    /// Reads and validates a hand-off. Returns null — never a partially-trusted record — when
    /// anything is off. <paramref name="nowUtc"/> is injected so staleness is testable.
    /// </summary>
    public static UpdateTaskHandoff? TryRead(string path, DateTime nowUtc,
        Action<string>? rejectionDiagnostic = null)
    {
        UpdateTaskHandoff? Reject(string reason)
        {
            rejectionDiagnostic?.Invoke(reason);
            return null;
        }

        try
        {
            if (!File.Exists(path)) return Reject("handoff file does not exist");

            var handoff = JsonSerializer.Deserialize<UpdateTaskHandoff>(File.ReadAllText(path), JsonOpts);
            if (handoff is null) return Reject("handoff JSON was empty");

            if (handoff.Pid <= 0) return Reject("handoff PID was invalid");
            if (string.IsNullOrWhiteSpace(handoff.PackageDir))
                return Reject("handoff package directory was empty");

            // Future-dated stamps mean clock skew or tampering; don't trust either.
            if (handoff.CreatedUtc > nowUtc) return Reject("handoff timestamp was in the future");
            if (nowUtc - handoff.CreatedUtc > MaxAge) return Reject("handoff had expired");

            if (!IsPlausiblePackageDir(handoff.PackageDir))
                return Reject("package directory failed staging validation");

            return handoff;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update hand-off could not be read — ignoring");
            return Reject($"handoff read failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The package must be a staging directory of the shape LaunchUpdater creates: an absolute
    /// path under a Temp directory, holding the three files the elevated verifier reads.
    ///
    /// The elevated task runs as SYSTEM, whose <c>Path.GetTempPath()</c> is C:\Windows\Temp — NOT
    /// the interactive user's temp — so this cannot reuse the caller-side prefix check in
    /// ApplyUpdate. It matches on the path segment instead, which is what actually distinguishes
    /// a staging directory from an arbitrary location.
    /// </summary>
    internal static bool IsPlausiblePackageDir(string packageDir)
    {
        string full;
        try { full = Path.GetFullPath(packageDir); }
        catch { return false; }

        if (!Path.IsPathFullyQualified(full)) return false;

        var sep = Path.DirectorySeparatorChar;
        var segments = full.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Any(s => s.Equals("Temp", StringComparison.OrdinalIgnoreCase))) return false;

        // Refuse the temp root itself — the updater deletes this directory recursively when it
        // finishes, and "delete C:\Windows\Temp" is not an outcome worth risking on bad input.
        if (segments.Length == 0) return false;
        if (segments[^1].Equals("Temp", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Directory.Exists(full)) return false;

        return File.Exists(Path.Combine(full, "update.zip"))
            && File.Exists(Path.Combine(full, "checksums.sha256"))
            && File.Exists(Path.Combine(full, "checksums.sha256.sig"))
            && File.Exists(Path.Combine(full, "asset-name.txt"));
    }
}
