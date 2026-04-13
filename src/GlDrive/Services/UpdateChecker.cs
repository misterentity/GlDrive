using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    private const string RepoApiUrl = "https://api.github.com/repos/misterentity/GlDrive/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

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
    }

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

            // Compare component by component to avoid Version class quirks
            bool isNewer = remoteMajor > currentMajor
                || (remoteMajor == currentMajor && remoteMinor > currentMinor)
                || (remoteMajor == currentMajor && remoteMinor == currentMinor && remoteBuild > currentBuild);

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

    public async Task DownloadAndInstallAsync(GitHubRelease release)
    {
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

        var tempZip = Path.Combine(Path.GetTempPath(), $"GlDrive-{release.TagName}.zip");

        // Clean up stale temp file from a previous failed download attempt
        try { if (File.Exists(tempZip)) File.Delete(tempZip); }
        catch (IOException ex) { Log.Warning("Cannot remove stale update file {Path}: {Msg}", tempZip, ex.Message); }

        Log.Information("Downloading update: {Url} → {Path}", asset.BrowserDownloadUrl, tempZip);

        // Stream download to disk to avoid buffering 150MB+ in memory
        using var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var fs = File.Create(tempZip))
        {
            await response.Content.CopyToAsync(fs);
        }

        Log.Information("Download complete ({Size:F1} MB), verifying integrity...",
            new FileInfo(tempZip).Length / (1024.0 * 1024));

        // Verify SHA-256 hash against checksums.sha256 asset
        if (!await VerifyDownloadHash(tempZip, release))
        {
            Log.Error("Update integrity check failed — hash mismatch or no checksum available");
            try { File.Delete(tempZip); } catch { }
            return;
        }

        Log.Information("Integrity verified, preparing updater");
        LaunchUpdater(tempZip);
    }

    private async Task<bool> VerifyDownloadHash(string zipPath, GitHubRelease release)
    {
        var checksumAsset = release.Assets.FirstOrDefault(a =>
            a.Name.Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase));

        if (checksumAsset == null)
        {
            Log.Warning("No checksums.sha256 asset found — rejecting update");
            return false;
        }

        if (!IsAllowedDownloadUrl(checksumAsset.BrowserDownloadUrl))
        {
            Log.Error("Checksum asset URL not in allowed host list — rejecting. Url={Url}", checksumAsset.BrowserDownloadUrl);
            return false;
        }

        try
        {
            var checksumText = await _http.GetStringAsync(checksumAsset.BrowserDownloadUrl);
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
                return false;
            }

            await using var fs = File.OpenRead(zipPath);
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));

            if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("SHA-256 verified: {Hash}", actualHash[..16] + "...");
                return true;
            }

            Log.Error("SHA-256 mismatch! Expected={Expected}, Actual={Actual}",
                expectedHash[..16] + "...", actualHash[..16] + "...");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hash verification failed — rejecting update");
            return false;
        }
    }

    private void LaunchUpdater(string zipPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"gldrive-update-{Guid.NewGuid():N}");

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);

        Log.Information("Extracting update to {Path}", extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // Detect nested folder
        var entries = Directory.GetDirectories(extractDir);
        if (entries.Length == 1 && File.Exists(Path.Combine(entries[0], "GlDrive.exe")))
        {
            Log.Information("Detected nested folder: {Path}", entries[0]);
            extractDir = entries[0];
        }

        var exePath = Path.Combine(extractDir, "GlDrive.exe");
        if (!File.Exists(exePath))
        {
            Log.Error("Extraction failed — GlDrive.exe not found in {Path}", extractDir);
            return;
        }

        // Verify Authenticode signature if the current binary is signed
        if (!VerifyAuthenticode(exePath))
        {
            Log.Error("Update binary failed Authenticode verification — aborting");
            try { Directory.Delete(extractDir, true); } catch { }
            return;
        }

        try { File.Delete(zipPath); } catch { }

        var pid = Environment.ProcessId;
        var installDir = _installPath.TrimEnd(Path.DirectorySeparatorChar);

        Log.Information("Launching updater: --apply-update {Pid} \"{ExtractDir}\" \"{InstallDir}\"",
            pid, extractDir, installDir);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--apply-update {pid} \"{extractDir}\" \"{installDir}\"",
                Verb = "runas",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch updater (UAC cancelled?)");
            return;
        }

        // Signal the app to shut down NOW — updater is waiting for us to exit
        RestartRequested?.Invoke();
    }

    private static bool VerifyAuthenticode(string filePath)
    {
        try
        {
            // Check if the CURRENT binary is signed — if not, skip Authenticode check
            // (development builds are unsigned)
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe == null) return true;

            X509Certificate2? currentCert = null;
            try
            {
                var raw = X509Certificate.CreateFromSignedFile(currentExe);
                currentCert = new X509Certificate2(raw);
            }
            catch (CryptographicException) { }

            if (currentCert == null)
            {
                Log.Debug("Current binary is unsigned — skipping Authenticode check on update");
                return true;
            }

            // Current binary IS signed — require the update to be signed by the same issuer
            X509Certificate2? updateCert = null;
            try
            {
                var raw = X509Certificate.CreateFromSignedFile(filePath);
                updateCert = new X509Certificate2(raw);
            }
            catch (CryptographicException) { }

            if (updateCert == null)
            {
                Log.Error("Update binary is not signed but current binary is — rejecting");
                return false;
            }

            // Compare issuer to prevent cross-signed attacks
            if (!currentCert.Issuer.Equals(updateCert.Issuer, StringComparison.Ordinal))
            {
                Log.Error("Update binary signed by different issuer: expected={Expected}, got={Got}",
                    currentCert.Issuer, updateCert.Issuer);
                return false;
            }

            Log.Information("Authenticode verification passed: {Subject}", updateCert.Subject);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Authenticode verification error — rejecting update");
            return false;
        }
    }

    public static void ApplyUpdate(int pid, string extractDir, string installDir)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "gldrive-update.log");
        void LogUpdate(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }

        try
        {
            // Validate paths to prevent arbitrary file overwrite
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var tempDir = Path.GetFullPath(Path.GetTempPath());

            if (!fullExtract.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                LogUpdate($"SECURITY: extractDir is not in temp directory — aborting. extractDir={fullExtract}");
                Environment.Exit(1);
            }

            if (!fullInstall.Contains("GlDrive", StringComparison.OrdinalIgnoreCase))
            {
                LogUpdate($"SECURITY: installDir does not look like a GlDrive installation — aborting. installDir={fullInstall}");
                Environment.Exit(1);
            }

            if (!File.Exists(Path.Combine(fullExtract, "GlDrive.exe")))
            {
                LogUpdate($"SECURITY: GlDrive.exe not found in extractDir — aborting");
                Environment.Exit(1);
            }

            LogUpdate($"ApplyUpdate started — pid={pid}, extractDir={fullExtract}, installDir={fullInstall}");

            // Wait for the original process to exit (up to 60s)
            try
            {
                var proc = Process.GetProcessById(pid);
                LogUpdate($"Waiting for PID {pid} to exit...");
                if (!proc.WaitForExit(60_000))
                {
                    LogUpdate($"PID {pid} did not exit in 60s — killing it");
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                }
            }
            catch (ArgumentException)
            {
                LogUpdate($"PID {pid} already exited");
            }

            // Grace period for file handles
            Thread.Sleep(2000);

            // Rename existing files to .old
            LogUpdate("Renaming existing files to .old");
            var renamed = 0;
            var failed = 0;
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Move(file, file + ".old", overwrite: true);
                    renamed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogUpdate($"  Warning: could not rename {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            LogUpdate($"Renamed {renamed} files, {failed} failed");

            // Copy new files
            LogUpdate("Copying new files from extract dir");
            var copied = 0;
            foreach (var sourceFile in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(extractDir, sourceFile);
                var destFile = Path.Combine(installDir, relativePath);

                var destDir = Path.GetDirectoryName(destFile)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                File.Copy(sourceFile, destFile, overwrite: true);
                copied++;
            }
            LogUpdate($"Copied {copied} files");

            // Clean up
            LogUpdate("Cleaning up extract directory");
            try { Directory.Delete(extractDir, true); } catch { }
            // Also clean parent if nested
            var parent = Directory.GetParent(extractDir);
            if (parent != null && parent.FullName.StartsWith(Path.GetTempPath()) &&
                parent.Name.StartsWith("gldrive-update-"))
            {
                try { parent.Delete(true); } catch { }
            }

            // Launch the updated app
            var newExe = Path.Combine(installDir, "GlDrive.exe");
            if (!File.Exists(newExe))
            {
                LogUpdate($"ERROR: {newExe} not found after copy!");
                return;
            }

            // Clean up the update marker so the watchdog doesn't interfere on next run
            try { File.Delete(Path.Combine(installDir, "..", "GlDrive", ".updating")); } catch { }
            var appDataUpdating = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", ".updating");
            try { File.Delete(appDataUpdating); } catch { }

            LogUpdate($"Update complete, launching {newExe}");
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = newExe,
                UseShellExecute = true
            });

            if (child != null)
            {
                // Wait for the child to actually start before we exit —
                // Process.Kill() is instant and could race with process creation
                try { child.WaitForInputIdle(5000); } catch { }
                LogUpdate($"Child process started: PID={child.Id}");
            }
        }
        catch (Exception ex)
        {
            LogUpdate($"Update FAILED: {ex}");
        }

        // Force-kill instead of Environment.Exit to prevent GnuTLS native DLL
        // teardown crash (DllNotFoundException in __scrt_uninitialize_type_info)
        // when running from the temp update directory
        Thread.Sleep(2000);
        Process.GetCurrentProcess().Kill();
    }

    public static void CleanupOldUpdateFiles()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(baseDir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(file); deleted++; }
                catch { }
            }
            if (deleted > 0)
                Log.Information("Cleaned up {Count} .old update files", deleted);
        }
        catch { }
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
                if (release != null && release.TagName != _lastNotifiedTag)
                {
                    _lastNotifiedTag = release.TagName;
                    UpdateAvailable?.Invoke(release);
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
