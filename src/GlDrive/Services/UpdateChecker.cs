using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Services;

public record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size);

public record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("assets")] GitHubAsset[] Assets)
{
    public Version? ParsedVersion => ParseTag(TagName);

    private static Version? ParseTag(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var v) ? v : null;
    }
}

public class UpdateChecker : IDisposable
{
    private sealed record VerifiedUpdatePackage(
        string ZipPath, string AssetName, string ChecksumText, string SignatureText);

    private const string RepoApiUrl = "https://api.github.com/repos/misterentity/GlDrive/releases/latest";
    // Lowered 24h -> 3h so an auto-install that's deferred (a race is running) retries
    // within hours instead of a full day. The check itself is a single cheap GitHub
    // API call.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);

    /// <summary>
    /// Host allowlist for update-artifact downloads. Even though GitHub's JSON response
    /// carries a browser_download_url over TLS, a compromised API response (MitM at a
    /// CDN/proxy, or a maintainer account takeover) could redirect the URL to an attacker
    /// host. Pin the allowed hosts so any such tampering fails closed.
    /// </summary>
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    private static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedDownloadHosts.Contains(uri.Host);
    }

    private readonly HttpClient _http;
    private readonly string _installPath;
    private CancellationTokenSource? _periodicCts;
    private string? _lastNotifiedTag;

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public event Action<GitHubRelease>? UpdateAvailable;

    /// <summary>
    /// When true (default), the periodic check downloads + installs a newer release
    /// automatically instead of only notifying. Without this, notify-only updates sat
    /// unapplied for days because they required a manual tray click (observed: stuck on
    /// 3.8.0 while 3.8.1/3.8.2 shipped).
    /// </summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>Live settings callbacks; absence preserves the established automatic updater.</summary>
    public Func<bool>? AutoInstallEnabled { get; set; }
    // Safe mode bounds network preparation so a stalled download cannot hold normal
    // recovery forever. Ordinary update downloads retain their established timing.
    internal TimeSpan? PreparationTimeout { get; set; }
    private readonly Recovery.UpdateRecoveryAdmission _recoveryAdmission = new();
    internal IDisposable? TryBeginRecoveryOperation() => _recoveryAdmission.TryBeginRecovery();
    internal bool TryStopForRecoveryRestart() => _recoveryAdmission.TryStopForRestart();
    internal void RecoveryRestartFailed() => _recoveryAdmission.RestartFailed();

    /// <summary>
    /// Gate for auto-install timing. Returns false to defer (e.g. a spread race is in
    /// flight) — the update retries on the next <see cref="CheckInterval"/> tick. Null
    /// means "always OK to install now".
    /// </summary>
    public Func<bool>? CanInstallNow { get; set; }

    // Tag we've already attempted an auto-install for this process lifetime, so a
    // deferred-then-eligible release isn't downloaded twice concurrently.
    private string? _autoInstallAttemptedTag;

    // Tag whose session latch we already handed back to the persisted 24h decline marker.
    // Bounds the handback to once per version per process so a marker that later becomes
    // unreadable can cost at most one further elevation prompt, never one per poll.
    private string? _declineLatchReleasedTag;

    // When the current release first got deferred by CanInstallNow, and for which tag.
    // CanInstallNow samples "is a race running?" at one instant every CheckInterval. On a
    // continuously-racing setup that sample is essentially never idle, so the courtesy gate
    // became an indefinite block: v3.10.37 was deferred on 3 consecutive polls across 10.5h
    // (2026-07-23 01:23/04:23/07:23) with no path to ever installing. The gate is a courtesy,
    // not a guarantee — past the grace period the update proceeds anyway.
    private string? _deferredTag;
    private DateTime _deferredSinceUtc;

    /// <summary>
    /// How long the busy gate may hold an update back before it installs regardless.
    /// Races resume after restart (the queue re-scans), so a bounded interruption is far
    /// cheaper than never shipping a fix to a machine that races around the clock.
    /// </summary>
    internal static readonly TimeSpan MaxInstallDeferral = TimeSpan.FromHours(12);

    /// <summary>
    /// Whether a busy-gated update has been held long enough to install regardless.
    /// Split out so the starvation rule is testable without driving the polling loop.
    /// </summary>
    internal static bool ShouldForceDeferredInstall(TimeSpan heldFor) => heldFor >= MaxInstallDeferral;

    /// <summary>
    /// How long the busy gate may hold an update before the hold is surfaced to the user
    /// (ERR log + tray notification). Set below <see cref="MaxInstallDeferral"/> so a
    /// continuous racer gets a heads-up — and a window to pause and apply now — before the
    /// forced install interrupts a transfer. A normal race (30-min hard ceiling) clears the
    /// gate long before this, so it never fires in ordinary operation.
    /// </summary>
    internal static readonly TimeSpan EscalateDeferralAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether a busy-gated update has been held long enough to escalate visibly. Split out
    /// so the rule is testable without driving the polling loop.
    /// </summary>
    internal static bool ShouldEscalateDeferral(TimeSpan heldFor) => heldFor >= EscalateDeferralAfter;

    /// <summary>
    /// Raised once per release when the busy gate has held an otherwise-ready update past
    /// <see cref="EscalateDeferralAfter"/>. Lets the UI surface a tray notification so a
    /// continuous racer can choose to pause and apply now, instead of the hold staying
    /// invisible in the log until the forced install interrupts a transfer.
    /// Args: the pending release, and how long it has been held.
    /// </summary>
    public event Action<GitHubRelease, TimeSpan>? UpdateInstallStalled;

    // Whether we have already escalated the CURRENT deferred tag, so the notification fires
    // once rather than on every 3h poll. Reset whenever the deferred tag changes or clears.
    private bool _deferralEscalated;

    /// <summary>
    /// Marker holding when the current release first got deferred. The clock MUST survive a
    /// restart: v3.10.38 kept it in a field, so every watchdog restart, manual restart, or
    /// crash-recovery relaunch reset it to zero. With a 3h poll interval, a box that restarts
    /// even once per 12h could never accumulate the deferral and the forced install never
    /// fired — the same indefinite starvation the deadline was added to end.
    /// </summary>
    private static string DeferralMarkerPath => UpdateStatePath(".update-deferred");

    /// <summary>
    /// When <paramref name="tagName"/> first got deferred, or null if this tag has no recorded
    /// hold (never deferred, superseded by a newer release, or the marker is unreadable).
    /// A start time in the future (clock skew, or a marker written under a wrong clock) is
    /// rejected rather than trusted — it would push the deadline out indefinitely.
    /// Split out with an explicit path so it can be exercised against a temp directory.
    /// </summary>
    internal static DateTime? ReadDeferralStartAt(string markerPath, string tagName, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(markerPath)) return null;
            var parts = File.ReadAllText(markerPath).Trim().Split('\t');
            if (parts.Length != 2) return null;
            if (!parts[0].Equals(tagName, StringComparison.OrdinalIgnoreCase)) return null;
            if (!DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var startUtc)) return null;
            startUtc = startUtc.ToUniversalTime();
            return startUtc > nowUtc ? null : startUtc;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read update-deferral marker");
            return null;
        }
    }

    /// <summary>Record (or refresh) the deferral start for a tag. One line: tag TAB ISO-8601 UTC.</summary>
    internal static void WriteDeferralStartAt(string markerPath, string tagName, DateTime startUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, $"{tagName}\t{startUtc.ToUniversalTime():O}");
        }
        catch (Exception ex) { Log.Debug(ex, "Could not write update-deferral marker"); }
    }

    /// <summary>Drop the deferral marker once the update is no longer being held back.</summary>
    internal static void ClearDeferralAt(string markerPath)
    {
        try { if (File.Exists(markerPath)) File.Delete(markerPath); }
        catch (Exception ex) { Log.Debug(ex, "Could not clear update-deferral marker"); }
    }

    /// <summary>
    /// Fired when the app should shut down immediately so the updater can replace files.
    /// The updater has already been launched and is waiting for this PID to exit.
    /// </summary>
    public event Action? RestartRequested;

    public UpdateChecker(string? installPath = null)
    {
        _installPath = installPath ?? AppContext.BaseDirectory;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GlDrive/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        ReconcileUpdateAttempt();
    }

    private static string UpdateStatePath(string name) => Path.Combine(
        GlDrive.Util.AppDataPaths.Root, name);

    /// <summary>
    /// Number of consecutive failed auto-install attempts of the SAME release before we stop
    /// retrying it. Without this, an update that always fails during ApplyUpdate becomes an
    /// infinite loop: the failure kills the app, rollback relaunches it, the 30s startup check
    /// finds the same release, and it tries again roughly every 40 seconds forever (observed
    /// with the ZLibException lazy-native-load bug — see the extraction ordering in ApplyUpdate).
    /// </summary>
    private const int MaxFailedInstallAttempts = 3;

    /// <summary>
    /// Drops the revision component and clamps an unset build to 0, so a version parsed from a
    /// release tag ("v3.10.90" -> 3.10.90 with Revision -1) is comparable to the running
    /// assembly version (3.10.90.0). BOTH halves of the update subsystem — the "is there
    /// something newer" check and the post-install reconciliation — compare through this, so
    /// they cannot drift apart again.
    /// </summary>
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// True when the running version satisfies an install attempt for <paramref name="attempted"/>.
    ///
    /// The question is "did we get OFF the old version?", NOT "did we land on exactly this build".
    /// An auto-install downloads whatever GitHub calls `latest`, so when a newer release is
    /// published between the tag being recorded and the installer running, we legitimately
    /// OVERSHOOT: attempted v3.10.90, ended up running 3.10.92. That is the update SUCCEEDING.
    /// Judging it by equality scored it a failure (production 2026-08-31 10:12:30, "Update attempt
    /// for v3.10.90 did not take effect (running 3.10.92.0) — failure 1/3"), and
    /// <see cref="MaxFailedInstallAttempts"/> such overshoots latch <see cref="IsUpdateBlocked"/>
    /// against that tag permanently — while never clearing the failure ledger, because only the
    /// success branch deletes it.
    /// </summary>
    internal static bool AttemptSatisfiedBy(Version attempted, Version running)
        => Normalize(running) >= Normalize(attempted);

    /// <summary>
    /// Called at startup. If we recorded an in-flight install attempt for some tag and we are NOT
    /// now running at least that version, the attempt failed — count it, and block the tag once it
    /// has failed too many times. Reaching the attempted version (or newer) clears all failure state.
    /// </summary>
    private static void ReconcileUpdateAttempt()
    {
        var attemptPath = UpdateStatePath(".update-attempt");
        try
        {
            if (!File.Exists(attemptPath)) return;
            var tag = File.ReadAllText(attemptPath).Trim();
            try { File.Delete(attemptPath); } catch { }
            if (string.IsNullOrEmpty(tag)) return;

            var attempted = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
            if (attempted != null && AttemptSatisfiedBy(attempted, CurrentVersion))
            {
                // The update landed. Forget every past failure.
                try { File.Delete(UpdateStatePath(".update-failures")); } catch { }
                if (Normalize(attempted) < Normalize(CurrentVersion))
                    Log.Information(
                        "Update to {Tag} landed on {Running} — a newer release published while the " +
                        "installer ran; treating as installed", tag, CurrentVersion);
                else
                    Log.Information("Update to {Tag} confirmed installed", tag);
                return;
            }

            var failures = ReadFailureCounts();
            failures[tag] = failures.GetValueOrDefault(tag) + 1;
            WriteFailureCounts(failures);
            Log.Warning("Update attempt for {Tag} did not take effect (running {Running}) — failure {Count}/{Max}",
                tag, CurrentVersion, failures[tag], MaxFailedInstallAttempts);
            if (failures[tag] >= MaxFailedInstallAttempts)
                Log.Error("Auto-install of {Tag} has failed {Count} times — giving up. " +
                          "Install manually from the GitHub release; see {InstallLog} for the cause.",
                    tag, failures[tag], UpdateInstaller.UpdateInstallLogPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not reconcile previous update attempt");
        }
    }

    private static Dictionary<string, int> ReadFailureCounts()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = UpdateStatePath(".update-failures");
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('|', 2);
                if (parts.Length == 2 && int.TryParse(parts[1], out var n))
                    result[parts[0]] = n;
            }
        }
        catch { }
        return result;
    }

    private static void WriteFailureCounts(Dictionary<string, int> counts)
    {
        try
        {
            var path = UpdateStatePath(".update-failures");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, counts.Select(kv => $"{kv.Key}|{kv.Value}"));
        }
        catch { }
    }

    internal static bool IsUpdateBlocked(string tag) =>
        ReadFailureCounts().GetValueOrDefault(tag) >= MaxFailedInstallAttempts;

    public async Task<GitHubRelease?> CheckForUpdateAsync()
    {
        try
        {
            Log.Debug("Checking for updates at {Url}", RepoApiUrl);
            var release = await _http.GetFromJsonAsync<GitHubRelease>(RepoApiUrl);
            if (release?.ParsedVersion == null)
            {
                Log.Debug("Could not parse release version from tag: {Tag}", release?.TagName);
                return null;
            }

            var currentMajor = CurrentVersion.Major;
            var currentMinor = CurrentVersion.Minor;
            var currentBuild = Math.Max(CurrentVersion.Build, 0);
            var remote = release.ParsedVersion;
            var remoteMajor = remote.Major;
            var remoteMinor = remote.Minor;
            var remoteBuild = Math.Max(remote.Build, 0);

            Log.Debug("Version check: running={Major}.{Minor}.{Build}, latest={RMajor}.{RMinor}.{RBuild}",
                currentMajor, currentMinor, currentBuild, remoteMajor, remoteMinor, remoteBuild);

            // Normalize away the revision quirk (a tag parses with Revision -1) and compare.
            // Same helper the post-install reconciler uses — see AttemptSatisfiedBy.
            bool isNewer = Normalize(remote) > Normalize(CurrentVersion);

            if (isNewer)
            {
                Log.Information("Update available: {Current}.{CMin}.{CBuild} → {RMaj}.{RMin}.{RBuild}",
                    currentMajor, currentMinor, currentBuild, remoteMajor, remoteMinor, remoteBuild);
                return release;
            }

            Log.Debug("Up to date: {Major}.{Minor}.{Build}", currentMajor, currentMinor, currentBuild);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            return null;
        }
    }

    public async Task DownloadAndInstallAsync(GitHubRelease release, CancellationToken cancellationToken = default)
    {
        using var preparation = CreatePreparationCancellation(cancellationToken, PreparationTimeout);
        var preparationToken = preparation.Token;
        preparationToken.ThrowIfCancellationRequested();
        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset == null)
        {
            Log.Warning("No win-x64 zip asset found in release {Tag}", release.TagName);
            return;
        }

        // Refuse to download from hosts not in the GitHub allowlist — defense against
        // a tampered or redirected GitHub API response.
        if (!IsAllowedDownloadUrl(asset.BrowserDownloadUrl))
        {
            Log.Error("Update asset URL not in allowed host list — rejecting. Url={Url}", asset.BrowserDownloadUrl);
            return;
        }

        // Don't re-download a release whose install has already failed repeatedly — that is the
        // fail/rollback/relaunch/retry loop. Manual install stays available via the tray.
        if (IsUpdateBlocked(release.TagName))
        {
            Log.Warning("Skipping auto-install of {Tag}: it has failed {Max} times already",
                release.TagName, MaxFailedInstallAttempts);
            return;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"GlDrive-{release.TagName}.zip");

        // Clean up stale temp file from a previous failed download attempt
        try { if (File.Exists(tempZip)) File.Delete(tempZip); }
        catch (IOException ex) { Log.Warning("Cannot remove stale update file {Path}: {Msg}", tempZip, ex.Message); }

        Log.Information("Downloading update: {Url} → {Path}", asset.BrowserDownloadUrl, tempZip);

        await DownloadAssetAsync(_http, new Uri(asset.BrowserDownloadUrl), tempZip,
            (url, path, token) => GlDrive.Downloads.Segmented.SegmentedHttpDownloader.DownloadAsync(url, path, null, token),
            preparationToken);

        Log.Information("Download complete ({Size:F1} MB), verifying integrity...",
            new FileInfo(tempZip).Length / (1024.0 * 1024));

        // Verify SHA-256 hash against checksums.sha256 asset
        var verifiedPackage = await VerifyDownloadHash(tempZip, release, preparationToken);
        if (verifiedPackage == null)
        {
            Log.Error("Update integrity check failed — hash mismatch or no checksum available");
            try { File.Delete(tempZip); } catch { }
            return;
        }

        Log.Information("Integrity verified, preparing updater");
        // This is the last cancellable boundary. Once the attempt marker and helper
        // handoff begin, the installer owns shutdown and must not be abandoned.
        preparationToken.ThrowIfCancellationRequested();
        // Record the in-flight attempt so the NEXT startup can tell whether it actually landed.
        // Written before launching, because a failed ApplyUpdate kills this process.
        try
        {
            var attemptPath = UpdateStatePath(".update-attempt");
            Directory.CreateDirectory(Path.GetDirectoryName(attemptPath)!);
            File.WriteAllText(attemptPath, release.TagName);
        }
        catch (Exception ex) { Log.Debug(ex, "Could not record update attempt marker"); }
        LaunchUpdater(verifiedPackage, release.TagName);
    }

    internal static CancellationTokenSource CreatePreparationCancellation(CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } duration) preparation.CancelAfter(duration);
        return preparation;
    }

    internal static async Task DownloadAssetAsync(HttpClient http, Uri url, string destination,
        Func<Uri, string, CancellationToken, Task> segmentedDownload, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await segmentedDownload(url, destination, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Multi-stream update download failed — retrying as a single stream");
            try { File.Delete(destination + ".gdpart"); File.Delete(destination + ".gdpart.json"); } catch { }
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(destination);
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async Task<(string Checksums, string Signature)> ReadSignedChecksumsAsync(
        HttpClient http, string checksumsUrl, string signatureUrl, CancellationToken cancellationToken)
    {
        var checksums = await http.GetStringAsync(checksumsUrl, cancellationToken);
        var signature = await http.GetStringAsync(signatureUrl, cancellationToken);
        return (checksums, signature);
    }

    private async Task<VerifiedUpdatePackage?> VerifyDownloadHash(string zipPath, GitHubRelease release, CancellationToken cancellationToken)
    {
        var checksumAsset = release.Assets.FirstOrDefault(a =>
            a.Name.Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase));

        if (checksumAsset == null)
        {
            Log.Warning("No checksums.sha256 asset found — rejecting update");
            return null;
        }

        if (!IsAllowedDownloadUrl(checksumAsset.BrowserDownloadUrl))
        {
            Log.Error("Checksum asset URL not in allowed host list — rejecting. Url={Url}", checksumAsset.BrowserDownloadUrl);
            return null;
        }
        try
        {
            var sigAsset = release.Assets.FirstOrDefault(a =>
                a.Name.Equals("checksums.sha256.sig", StringComparison.OrdinalIgnoreCase));
            if (sigAsset == null || !IsAllowedDownloadUrl(sigAsset.BrowserDownloadUrl))
            {
                Log.Error("Signed checksum asset is missing or has an untrusted URL — rejecting update");
                return null;
            }
            var (checksumText, signatureText) = await ReadSignedChecksumsAsync(_http,
                checksumAsset.BrowserDownloadUrl, sigAsset.BrowserDownloadUrl, cancellationToken);
            if (!UpdateChecksumSignature.Verify(checksumText, signatureText))
                return null;
            // Match against the actual asset name from GitHub, not the local temp filename
            var zipAsset = release.Assets.FirstOrDefault(a =>
                a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            var zipName = zipAsset?.Name ?? Path.GetFileName(zipPath);
            var expectedHash = checksumText.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2 && p[1].TrimStart('*').Equals(zipName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[0])
                .FirstOrDefault();

            if (string.IsNullOrEmpty(expectedHash))
            {
                Log.Warning("No matching hash for {File} in checksums.sha256", zipName);
                return null;
            }

            await using var fs = File.OpenRead(zipPath);
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, cancellationToken));

            if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("SHA-256 verified: {Hash}", actualHash[..16] + "...");
                return new VerifiedUpdatePackage(zipPath, zipName, checksumText, signatureText);
            }

            Log.Error("SHA-256 mismatch! Expected={Expected}, Actual={Actual}",
                expectedHash[..16] + "...", actualHash[..16] + "...");
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error(ex, "Hash verification failed — rejecting update");
            return null;
        }
    }

    /// <summary>Windows ERROR_CANCELLED — the user dismissed the UAC elevation prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Remember that the user declined elevation for a specific release tag, so auto-install
    /// stops re-prompting for it across restarts. Manual install from the tray is unaffected.
    /// </summary>
    private static void RecordDeclinedUpdate(string tagName) =>
        RecordDeclinedUpdateAt(UpdateStatePath(".update-declined"), tagName, DateTime.UtcNow);

    /// <summary>
    /// Forget a previous decline. Called once an elevated updater has actually been launched:
    /// the user granting elevation is the event the marker was waiting for.
    /// </summary>
    private static void ClearTimedOutUpdate() =>
        ClearDeclinedUpdateAt(UpdateStatePath(".update-timeout"));

    private static void ClearDeclinedUpdate() =>
        ClearDeclinedUpdateAt(UpdateStatePath(".update-declined"));

    internal static void ClearDeclinedUpdateAt(string markerPath)
    {
        try
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
        catch (Exception ex) { Log.Debug(ex, "Could not clear declined-update marker"); }
    }

    /// <summary>
    /// Record a decline as "not now", timestamped. One line: tag TAB ISO-8601 UTC — same shape
    /// as the deferral marker.
    /// </summary>
    internal static void RecordDeclinedUpdateAt(string markerPath, string tagName, DateTime declinedAtUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, $"{tagName}\t{declinedAtUtc.ToUniversalTime():O}");
        }
        catch (Exception ex) { Log.Debug(ex, "Could not record declined-update marker"); }
    }

    /// <summary>
    /// How long a declined release stays suppressed before auto-install offers it once more.
    ///
    /// v3.10.33 made the decline persistent to stop a restart nag loop, but persistent meant
    /// FOREVER for that tag, and the skip logged nothing — so one dismissed UAC prompt stranded
    /// this box on 3.10.39 through 18 polls / 51h of "Update available" with no diagnostic.
    /// A decline is a "not now", not a permanent opt-out: re-offer it on the next daily cycle.
    /// Declining again simply re-stamps the marker, so the nag loop v3.10.33 killed stays dead.
    /// </summary>
    internal static readonly TimeSpan DeclineSuppressionWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an UNANSWERED elevation prompt suppresses auto-install.
    ///
    /// Much shorter than <see cref="DeclineSuppressionWindow"/>, and deliberately not zero. A 24h
    /// window re-offers at the same hour that just failed, which is how two releases in a row had
    /// to be installed by hand. But no window at all means every 3h poll re-downloads the ~154 MB
    /// package and re-prompts forever — and on this path neither the .update-declined marker nor
    /// the .update-attempt brake is written, so nothing else bounds it.
    ///
    /// Four hours retries within the same day, so a prompt that expired overnight gets another go
    /// in working hours when somebody can actually answer it, at a cost of one or two re-downloads
    /// rather than eight a day.
    /// </summary>
    internal static readonly TimeSpan TimeoutSuppressionWindow = TimeSpan.FromHours(4);

    private static void RecordTimedOutUpdate(string tagName) =>
        RecordDeclinedUpdateAt(UpdateStatePath(".update-timeout"), tagName, DateTime.UtcNow);

    /// <summary>
    /// Whether the in-memory "already attempted this session" latch should be handed back to the
    /// persisted suppression markers for this tag.
    ///
    /// The latch exists so a failed launch does not re-download the package every poll. But a
    /// swallowed elevation failure returns NORMALLY, so nothing clears it — and then the latch,
    /// which is checked BEFORE the markers, makes their expiry unreachable and suppresses the
    /// version for the whole process lifetime.
    ///
    /// It must therefore be released for EVERY outcome that writes a persisted marker. v3.10.84
    /// added a second such outcome (an expired prompt writes .update-timeout instead of
    /// .update-declined) and this condition still read only the decline marker, so timeouts wedged
    /// the version exactly as before. Released at most once per tag per process, so a broken
    /// marker layer costs one extra prompt, never one per poll.
    /// </summary>
    internal static bool ShouldReleaseAttemptLatch(
        string tagName, string? alreadyReleasedTag, bool wasDeclined, bool wasTimedOut) =>
        tagName != alreadyReleasedTag && (wasDeclined || wasTimedOut);

    /// <summary>True when the elevation prompt for this tag expired unanswered recently.</summary>
    private static bool WasUpdateTimedOut(string tagName) =>
        WasUpdateDeclinedAt(UpdateStatePath(".update-timeout"), tagName, DateTime.UtcNow,
            TimeoutSuppressionWindow);

    /// <summary>True when the user already declined elevation for this exact release tag.</summary>
    private static bool WasUpdateDeclined(string tagName) =>
        WasUpdateDeclinedAt(UpdateStatePath(".update-declined"), tagName, DateTime.UtcNow);

    /// <summary>
    /// Marker-file check, split out so it can be exercised against a temp path instead of the
    /// real %AppData% state directory. Suppressed only when the marker names this exact tag AND
    /// the decline is still inside <see cref="DeclineSuppressionWindow"/>. Everything else fails
    /// open (allow the update): a newer release, an expired decline, a legacy tag-only marker
    /// with no timestamp, a future-dated stamp from clock skew, or an unreadable marker.
    /// Failing open matters more than honouring an ambiguous marker — the failure mode we are
    /// fixing is auto-install wedged off, silently, forever.
    /// </summary>
    internal static bool WasUpdateDeclinedAt(string markerPath, string tagName, DateTime nowUtc,
        TimeSpan? window = null)
    {
        try
        {
            if (!File.Exists(markerPath)) return false;
            var parts = File.ReadAllText(markerPath).Trim().Split('\t');
            // Legacy pre-v3.10.41 marker: bare tag, no timestamp. Treat as expired so an
            // already-stranded install self-heals on the first poll instead of needing the
            // file deleted by hand.
            if (parts.Length != 2) return false;
            if (!parts[0].Equals(tagName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var declinedAtUtc)) return false;
            declinedAtUtc = declinedAtUtc.ToUniversalTime();
            if (declinedAtUtc > nowUtc) return false; // clock skew — don't trust it
            return nowUtc - declinedAtUtc < (window ?? DeclineSuppressionWindow);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read declined-update marker");
            return false;
        }
    }

    /// <summary>
    /// Minimum time at the elevation prompt that indicates the Windows secure desktop timed it
    /// out rather than a person answering it.
    ///
    /// ShellExecute returns ERROR_CANCELLED (1223) for BOTH "user clicked No" and "prompt expired
    /// unanswered", so the code cannot tell them apart from the error alone — and for three
    /// releases it called both a decline. Timing separates them cleanly: the secure-desktop
    /// timeout is 120s by default, and the two observed failures sat at an invariant 131s and
    /// 132s, while a person answering takes seconds. An invariant duration across occurrences is
    /// a structural ceiling, not a human.
    /// </summary>
    internal static readonly TimeSpan ElevationTimeoutFloor = TimeSpan.FromSeconds(90);

    /// <summary>True when a 1223 that took this long is a timeout, not a user decision.</summary>
    internal static bool ElevationLikelyTimedOut(TimeSpan elapsed) => elapsed >= ElevationTimeoutFloor;

    /// <summary>
    /// Hands the staged package to the SYSTEM scheduled task registered by the installer, so the
    /// install needs no interactive elevation. Returns false when the task is absent (an install
    /// predating it, or one whose task was removed), leaving the caller to fall back to UAC.
    /// </summary>
    private static bool TryLaunchViaScheduledTask(string packageDir, int pid, string? tagName)
    {
        try
        {
            if (!ScheduledTaskExists(UpdateTaskHandoff.TaskName))
            {
                Log.Information("Update task {Task} is not registered — falling back to interactive elevation. " +
                                "Re-run the installer to enable unattended updates.", UpdateTaskHandoff.TaskName);
                return false;
            }

            UpdateTaskHandoff.Write(UpdateTaskHandoff.DefaultPath,
                new UpdateTaskHandoff(pid, packageDir, tagName ?? "(unknown)", DateTime.UtcNow));

            // /run takes no caller-supplied arguments: the task action is fixed at install time and
            // points at the installed executable, so nothing here can redirect what SYSTEM runs.
            var exit = RunSchtasks($"/run /tn \"{UpdateTaskHandoff.TaskName}\"");
            if (exit != 0)
            {
                Log.Warning("Update task {Task} failed to start (exit {Exit}) — falling back to " +
                            "interactive elevation", UpdateTaskHandoff.TaskName, exit);
                UpdateTaskHandoff.Clear(UpdateTaskHandoff.DefaultPath);
                return false;
            }

            Log.Information("Update {Tag} handed to the SYSTEM scheduled task — no elevation prompt needed",
                tagName ?? "(unknown)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not hand the update to the scheduled task — falling back to " +
                            "interactive elevation");
            UpdateTaskHandoff.Clear(UpdateTaskHandoff.DefaultPath);
            return false;
        }
    }

    private static bool ScheduledTaskExists(string taskName) =>
        RunSchtasks($"/query /tn \"{taskName}\"") == 0;

    private static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Bounded: schtasks /query and /run both return promptly; /run does NOT wait for the task.
        return UpdateProcessRunner.Run(psi, TimeSpan.FromSeconds(30)).ExitCode;
    }

    private void LaunchUpdater(VerifiedUpdatePackage package, string? tagName = null)
    {
        var packageDir = Path.Combine(Path.GetTempPath(), $"gldrive-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageDir);
        File.Move(package.ZipPath, Path.Combine(packageDir, "update.zip"), overwrite: true);
        File.WriteAllText(Path.Combine(packageDir, "checksums.sha256"), package.ChecksumText);
        File.WriteAllText(Path.Combine(packageDir, "checksums.sha256.sig"), package.SignatureText);
        File.WriteAllText(Path.Combine(packageDir, "asset-name.txt"), package.AssetName);

        var pid = Environment.ProcessId;
        var installDir = _installPath.TrimEnd(Path.DirectorySeparatorChar);

        Log.Information("Launching updater: --apply-update {Pid} \"{PackageDir}\" \"{InstallDir}\"",
            pid, packageDir, installDir);

        // Write HMAC-protected .updating marker so the watchdog won't restart on clean update exit
        var appDataUpdating = Path.Combine(
            GlDrive.Util.AppDataPaths.Root, ".updating");
        UpdateMarkerHmac.Write(appDataUpdating, pid);
        var appDataAuthorization = Path.Combine(
            GlDrive.Util.AppDataPaths.Root, ".update-auth");
        UpdateMarkerHmac.WriteAuthorization(appDataAuthorization, pid, packageDir, installDir);
        try { File.Delete(UpdateInstaller.UpdateCompletionPath(pid)); } catch { }

        // Prefer the SYSTEM scheduled task: no UAC prompt, so an install that comes due at 03:35
        // no longer depends on somebody being awake to approve it.
        if (TryLaunchViaScheduledTask(packageDir, pid, tagName))
        {
            // The SYSTEM task cannot resolve or decrypt a CurrentUser-DPAPI authorization
            // marker. Its boundary is the fixed task action plus publisher verification.
            try { File.Delete(appDataAuthorization); } catch { }
            ClearDeclinedUpdate();
            ClearTimedOutUpdate();
            RestartRequested?.Invoke();
            return;
        }

        var elevationStarted = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot locate installed GlDrive executable"),
                // ArgumentList avoids manual quoting and shell-injection via path components
                WorkingDirectory = installDir,
                Verb = "runas",
                UseShellExecute = true
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add(packageDir);
            psi.ArgumentList.Add(installDir);
            Process.Start(psi);

            ClearTimedOutUpdate();
            // Elevation was GRANTED — the reason the decline marker exists no longer holds.
            // Leaving it meant a successful manual tray install still left auto-install
            // suppressed for the rest of the 24h window, so the next release also had to be
            // installed by hand. Observed twice on 2026-08-14: the auto-install prompt fires
            // around 05:00 when nobody is at the machine, gets declined, and every later
            // version inherits the suppression until the window lapses.
            ClearDeclinedUpdate();
        }
        catch (Exception ex)
        {
            // Declining the elevation prompt is a user decision, not a fault. Record it so
            // the next app start doesn't immediately re-download the package and re-prompt:
            // _autoInstallAttemptedTag is in-memory only, so every restart used to nag again
            // and log this as an [ERR] (observed 3x over 2026-07-20..21).
            if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: ErrorCancelled })
            {
                // 1223 covers both "user clicked No" and "prompt expired unanswered". Only the
                // former is a decision worth suppressing a release for 24h; treating a timeout as
                // one re-offered the update at the SAME hour the next night and failed identically.
                if (ElevationLikelyTimedOut(elevationStarted.Elapsed))
                {
                    Log.Warning("Update {Tag}: elevation prompt expired unanswered after {Sec:F0}s " +
                                "(nobody at the machine) — retrying after {Hours:F0}h rather than " +
                                "suppressing for a day. Run the installer, or approve one prompt, " +
                                "to register the SYSTEM task and stop needing elevation at all.",
                        tagName ?? "(unknown)", elevationStarted.Elapsed.TotalSeconds,
                        TimeoutSuppressionWindow.TotalHours);
                    // Short brake so the retry lands in working hours instead of re-downloading
                    // ~154 MB on every 3h poll. Without it this path has no brake at all: the
                    // .update-declined marker is deliberately not written, and .update-attempt is
                    // dropped just below.
                    if (tagName != null) RecordTimedOutUpdate(tagName);
                }
                else
                {
                    Log.Warning("Update {Tag}: elevation prompt declined after {Sec:F0}s — skipping " +
                                "auto-install for this version. Install it from the tray menu when ready.",
                        tagName ?? "(unknown)", elevationStarted.Elapsed.TotalSeconds);
                    if (tagName != null) RecordDeclinedUpdate(tagName);
                }

                // Drop the in-flight marker: no elevated updater ever ran, so nothing was
                // renamed to .old and nothing was copied. Left behind, the NEXT startup's
                // ReconcileUpdateAttempt saw "attempted 3.10.46, running 3.10.45" and
                // counted a failed INSTALL that never happened. Three of those trip
                // IsUpdateBlocked, which kills auto-install AND the tray menu this very
                // warning tells the user to use — with no expiry.
                //
                // ONLY on the declined branch. The generic-failure branch below has no
                // other persisted brake, and there .update-attempt -> .update-failures is
                // what stops a genuinely broken launch from re-downloading ~150 MB on
                // every poll. A decline is already governed by the 24h .update-declined
                // marker written just above, so removing this one leaves a brake in place.
                try { File.Delete(UpdateStatePath(".update-attempt")); } catch { }
            }
            else
            {
                Log.Error(ex, "Failed to launch updater");
                // .update-attempt deliberately survives here — see above.
            }

            try { File.Delete(appDataUpdating); } catch { }
            try { File.Delete(appDataAuthorization); } catch { }
            try { Directory.Delete(packageDir, true); } catch { }
            return;
        }

        // Signal the app to shut down NOW — updater is waiting for us to exit
        RestartRequested?.Invoke();
    }

    public static void CleanupOldUpdateFiles()
    {
        // The ordinary process only observes. Receipts and the shared mutation lock live in
        // the protected install root; expected lack of write access is not a recovery fault.
        if (!UpdateProbation.InstalledStore().HasPreviousFiles()) return;

        // Auto-updates are applied by SYSTEM under Program Files, so their rollback files are
        // also SYSTEM-owned. A normal startup cannot remove them. Ask the separately registered,
        // fixed-action cleanup task to run our constrained headless entry point; /run cannot add
        // caller-controlled arguments or redirect the task to another directory.
        var exit = RunSchtasks($"/run /tn \"{UpdateTaskHandoff.CleanupTaskName}\"");
        if (exit == 0)
            Log.Information("Requested update recovery observation and cleanup of expired previous files");
        else
            Log.Warning("Could not start update cleanup task {Task} (exit {Exit}); cleanup will be " +
                        "retried at next startup", UpdateTaskHandoff.CleanupTaskName, exit);
    }

    private bool IsAutomaticInstallEnabled()
    {
        try { return AutoInstall && (AutoInstallEnabled?.Invoke() ?? true); }
        catch (Exception error) { Log.Debug(error, "Auto-install setting callback failed; retaining default"); return AutoInstall; }
    }

    /// <summary>Local offline recovery UI only. No profile or destination path crosses elevation.</summary>
    internal static UpdateRollbackLaunch TryLaunchRollback(string generation)
    {
        try
        {
            var status = UpdateProbation.GetStatus();
            if (!status.RollbackAvailable || status.Generation != generation) return new(false, status.Message);
            var executable = Environment.ProcessPath ?? throw new IOException("Installed executable is unavailable");
            if (!UpdateInstallationTrust.IsProtected(executable) || !Path.GetFullPath(executable).Equals(
                    Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "GlDrive.exe"), StringComparison.OrdinalIgnoreCase))
                return new(false, "Rollback requires the protected installed executable.");
            if (!UpdateRollbackGuard.Prepare(GlDrive.Util.AppDataPaths.Root, CurrentVersion, out var reason)) return new(false, reason);
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--rollback-update");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(generation);
            using var child = Process.Start(start);
            return child == null ? new(false, "Windows did not start the rollback helper.") :
                new(true, "Rollback was handed to the local helper. It will revalidate the files after this process exits.");
        }
        catch (Exception error) { return new(false, "Rollback was not started: " + error.Message); }
    }

    public void StartPeriodicCheck(CancellationToken cancellationToken = default)
    {
        _periodicCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _periodicCts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            while (!token.IsCancellationRequested)
            {
                var release = await CheckForUpdateAsync();
                if (release != null)
                {
                    if (release.TagName != _lastNotifiedTag)
                    {
                        _lastNotifiedTag = release.TagName;
                        UpdateAvailable?.Invoke(release);
                    }

                    // Auto-install: download + apply without a manual tray click. Gated
                    // by CanInstallNow so we don't restart mid-race; if deferred, the
                    // next tick retries. On success DownloadAndInstallAsync launches the
                    // updater and fires RestartRequested, so this process exits and the
                    // loop ends.
                    // Why auto-install is NOT running this tick. Every one of these used to be a
                    // silent `false` in the guard below, so an "Update available" line with no
                    // follow-up was indistinguishable from a healthy deferral. 18 such polls
                    // across 51h hid a wedged auto-installer (v3.10.41).
                    string? skipReason =
                        !IsAutomaticInstallEnabled() ? "auto-install disabled in config"
                        : release.TagName == _autoInstallAttemptedTag ? "already attempted this session"
                        : WasUpdateDeclined(release.TagName) ? "elevation prompt was declined recently"
                        : WasUpdateTimedOut(release.TagName) ? "elevation prompt expired unanswered recently — retrying later"
                        : null;

                    if (skipReason == null)
                    {
                        bool ok = true;
                        try { ok = CanInstallNow?.Invoke() ?? true; }
                        catch (Exception ex) { Log.Debug(ex, "CanInstallNow predicate threw — assuming OK"); }

                        if (!ok)
                        {
                            if (_deferredTag != release.TagName)
                            {
                                // Resume any hold this release already accumulated in a previous
                                // process — the deadline is wall-clock since the update first
                                // became installable, not since this process started.
                                var nowUtc = DateTime.UtcNow;
                                _deferredTag = release.TagName;
                                _deferralEscalated = false;
                                _deferredSinceUtc =
                                    ReadDeferralStartAt(DeferralMarkerPath, release.TagName, nowUtc) ?? nowUtc;
                                WriteDeferralStartAt(DeferralMarkerPath, release.TagName, _deferredSinceUtc);
                            }

                            var heldFor = DateTime.UtcNow - _deferredSinceUtc;
                            if (ShouldForceDeferredInstall(heldFor))
                            {
                                Log.Warning("Update {Tag} has been deferred for {Hours:F1}h (busy gate never cleared) — " +
                                            "installing anyway; in-flight races resume after restart",
                                    release.TagName, heldFor.TotalHours);
                                ok = true;
                            }
                            else if (!_deferralEscalated && ShouldEscalateDeferral(heldFor))
                            {
                                // Surface the stuck gate once: a continuous racer would otherwise
                                // see only routine "deferred (busy)" INF lines until the 12h force
                                // interrupts a transfer. ERR + a tray notification give a window to
                                // pause racing and apply it cleanly first.
                                _deferralEscalated = true;
                                Log.Error("Update {Tag} has been held back {Hours:F1}h by the busy gate " +
                                          "(active transfers never cleared). It will auto-install at {Max:F0}h; " +
                                          "pause racing to apply it now.",
                                    release.TagName, heldFor.TotalHours, MaxInstallDeferral.TotalHours);
                                try { UpdateInstallStalled?.Invoke(release, heldFor); }
                                catch (Exception ex) { Log.Debug(ex, "UpdateInstallStalled handler threw"); }
                            }
                        }

                        if (ok)
                        {
                            using var installation = _recoveryAdmission.TryBeginInstall();
                            if (installation == null)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(30), token);
                                continue;
                            }
                            _deferredTag = null;
                            _deferralEscalated = false;
                            ClearDeferralAt(DeferralMarkerPath);
                            _autoInstallAttemptedTag = release.TagName;
                            Log.Information("Auto-installing update {Tag}", release.TagName);
                            try { await DownloadAndInstallAsync(release, token); }
                            catch (Exception ex)
                            {
                                // Allow a retry on the next tick if it failed.
                                _autoInstallAttemptedTag = null;
                                Log.Warning(ex, "Auto-install of {Tag} failed — will retry", release.TagName);
                            }

                            // A declined elevation prompt is swallowed inside LaunchUpdater
                            // (it returns normally), so the catch above never runs and this
                            // session latch would suppress the version FOREVER — outliving
                            // the 24h persisted marker that v3.10.41 added precisely to make
                            // a decline expire. The latch is checked BEFORE WasUpdateDeclined,
                            // so leaving it set makes that expiry unreachable. Hand control
                            // back to the persisted marker, which is the intended suppressor.
                            //
                            // Two deliberate guards against turning a wedge into a nag loop:
                            //  - only release when the marker is actually READABLE and says
                            //    declined. WasUpdateDeclined fails OPEN by design, so if the
                            //    marker layer is broken we keep the latch and the old
                            //    (over-suppressing but quiet) behaviour.
                            //  - only release once per tag per process, so even if the marker
                            //    later becomes unreadable the user sees at most one further
                            //    prompt for this version, never a prompt every poll.
                            // Scoped to the declined case only: the other early returns
                            // (no asset, blocked, bad signature) must keep the latch so we
                            // don't re-download the package every poll.
                            // Timeouts must release the latch too. v3.10.84 stopped an expired
                            // prompt from writing .update-declined — correct in itself, but this
                            // release condition read ONLY that marker, so the timeout path never
                            // reached it and the latch wedged the version for the whole process
                            // lifetime: exactly the failure this block was written to prevent.
                            // Observed 2026-08-27: v3.10.87 timed out at 20:02 and every later
                            // poll logged "already attempted this session" while the warning one
                            // frame up promised "will retry". Control belongs to the persisted
                            // markers — 24h for a decline, 4h for a timeout.
                            if (ShouldReleaseAttemptLatch(release.TagName, _declineLatchReleasedTag,
                                    WasUpdateDeclined(release.TagName),
                                    WasUpdateTimedOut(release.TagName)))
                            {
                                _declineLatchReleasedTag = release.TagName;
                                _autoInstallAttemptedTag = null;
                            }
                        }
                        else
                        {
                            // Include the elapsed hold: three identical "deferred (busy)" lines
                            // looked like normal operation while the gate was in fact stuck.
                            Log.Information("Update {Tag} ready but deferred (busy) for {Hours:F1}h — " +
                                            "retrying next check (forced install after {Max:F0}h)",
                                release.TagName, (DateTime.UtcNow - _deferredSinceUtc).TotalHours,
                                MaxInstallDeferral.TotalHours);
                        }
                    }
                    else
                    {
                        Log.Information("Update {Tag} available but auto-install skipped: {Reason}. " +
                                        "Install it from the tray menu to apply it now.",
                            release.TagName, skipReason);
                    }
                }

                await Task.Delay(CheckInterval, token);
            }
        }, token);
    }

    public void StopPeriodicCheck()
    {
        _periodicCts?.Cancel();
        _periodicCts?.Dispose();
        _periodicCts = null;
    }

    public void Dispose()
    {
        StopPeriodicCheck();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
