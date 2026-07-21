using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Gate for auto-install timing. Returns false to defer (e.g. a spread race is in
    /// flight) — the update retries on the next <see cref="CheckInterval"/> tick. Null
    /// means "always OK to install now".
    /// </summary>
    public Func<bool>? CanInstallNow { get; set; }

    // Tag we've already attempted an auto-install for this process lifetime, so a
    // deferred-then-eligible release isn't downloaded twice concurrently.
    private string? _autoInstallAttemptedTag;

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
        var verifiedPackage = await VerifyDownloadHash(tempZip, release);
        if (verifiedPackage == null)
        {
            Log.Error("Update integrity check failed — hash mismatch or no checksum available");
            try { File.Delete(tempZip); } catch { }
            return;
        }

        Log.Information("Integrity verified, preparing updater");
        LaunchUpdater(verifiedPackage);
    }

    // PEM-encoded RSA public key used to verify checksums.sha256.sig signatures.
    // Private key lives in installer/keys/checksum-private.pem (gitignored).
    // Rotation: generate a new keypair, update this constant, ship the new public key in a release
    // signed by the OLD key. After widespread adoption, retire the old key.
    private const string ChecksumPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApLMNeSXwJZH7zeZNj/iK
        VaDDG1mn3+isBIhjnG2cKJH60WK1ImGJhTYmOGVhHKTzpKnZDJDKL6AsH3K70vG9
        GvI8MbMrX9NQpR1KCEU5NVEo3ENs27ObQ3CxFOtlJk31gcguTsUSPOQXqa0o3vec
        8yaK8w3dDHr/wJQmoupgvm+rTx94WR8ikBugK/ssB6EY6VIaBMHeAVe/EaGC0NBD
        HGuZtveNiNXfF8ezW96GE7EArrlu7LPeVDB3Jc7mFlh3RflO3kCSUF0FqwNB88hg
        MyJn6+aNupJRfki80D/tbtWLEHsDsGqIBQgywEc8Vpev6vb4XIcSgc9fNszgrFAU
        zQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private static bool VerifyChecksumSignature(string checksumText, string signatureText)
    {
        try
        {
            var sigBytes = Convert.FromBase64String(signatureText.Trim());
            using var rsa = RSA.Create();
            rsa.ImportFromPem(ChecksumPublicKeyPem);

            // BOM compatibility: PS 5.1's `Set-Content -Encoding UTF8` writes a BOM,
            // but HttpClient.GetStringAsync strips it during decode. Older releases
            // (v1.59–v1.62) signed BOM-included bytes; v1.63+ signs BOM-less bytes.
            // Try both representations so a verifier on either side of the change
            // can validate either generation of release.
            var withoutBom = Encoding.UTF8.GetBytes(checksumText);
            var withBom = new byte[withoutBom.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(withoutBom, 0, withBom, 3, withoutBom.Length);

            var ok = rsa.VerifyData(withoutBom, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                  || rsa.VerifyData(withBom,    sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok) Log.Error("checksums.sha256 signature verification FAILED — rejecting update");
            else Log.Information("checksums.sha256 signature verified");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Signature verification error — rejecting update");
            return false;
        }
    }

    private async Task<VerifiedUpdatePackage?> VerifyDownloadHash(string zipPath, GitHubRelease release)
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
            var checksumText = await _http.GetStringAsync(checksumAsset.BrowserDownloadUrl);
            var sigAsset = release.Assets.FirstOrDefault(a =>
                a.Name.Equals("checksums.sha256.sig", StringComparison.OrdinalIgnoreCase));
            if (sigAsset == null || !IsAllowedDownloadUrl(sigAsset.BrowserDownloadUrl))
            {
                Log.Error("Signed checksum asset is missing or has an untrusted URL — rejecting update");
                return null;
            }
            var signatureText = await _http.GetStringAsync(sigAsset.BrowserDownloadUrl);
            if (!VerifyChecksumSignature(checksumText, signatureText))
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
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));

            if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("SHA-256 verified: {Hash}", actualHash[..16] + "...");
                return new VerifiedUpdatePackage(zipPath, zipName, checksumText, signatureText);
            }

            Log.Error("SHA-256 mismatch! Expected={Expected}, Actual={Actual}",
                expectedHash[..16] + "...", actualHash[..16] + "...");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hash verification failed — rejecting update");
            return null;
        }
    }

    private void LaunchUpdater(VerifiedUpdatePackage package)
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GlDrive", ".updating");
        UpdateMarkerHmac.Write(appDataUpdating, pid);
        var appDataAuthorization = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GlDrive", ".update-auth");
        UpdateMarkerHmac.WriteAuthorization(appDataAuthorization, pid, packageDir, installDir);

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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch updater (UAC cancelled?)");
            try { File.Delete(appDataUpdating); } catch { }
            try { File.Delete(appDataAuthorization); } catch { }
            try { Directory.Delete(packageDir, true); } catch { }
            return;
        }

        // Signal the app to shut down NOW — updater is waiting for us to exit
        RestartRequested?.Invoke();
    }

    private static FileStream? OpenVerifiedUpdateArchive(string packageDir)
    {
        try
        {
            var checksumText = File.ReadAllText(Path.Combine(packageDir, "checksums.sha256"));
            var signatureText = File.ReadAllText(Path.Combine(packageDir, "checksums.sha256.sig"));
            var assetName = File.ReadAllText(Path.Combine(packageDir, "asset-name.txt")).Trim();
            if (!VerifyChecksumSignature(checksumText, signatureText)) return null;

            var packageVersion = ParseUpdateAssetVersion(assetName);
            if (packageVersion == null || packageVersion <= CurrentVersion)
            {
                Log.Error("Update package version is invalid or is not newer: {AssetName}", assetName);
                return null;
            }

            var expectedHash = checksumText.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2 &&
                    parts[1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[0])
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(expectedHash)) return null;

            var stream = new FileStream(Path.Combine(packageDir, "update.zip"), FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            stream.Position = 0;
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                Log.Error("Elevated update verification found a ZIP hash mismatch");
                return null;
            }
            return stream;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Elevated update package verification failed");
            return null;
        }
    }

    internal static Version? ParseUpdateAssetVersion(string assetName)
    {
        const string prefix = "GlDrive-v";
        const string suffix = "-win-x64.zip";
        if (!assetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !assetName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Version.TryParse(assetName[prefix.Length..^suffix.Length], out var version) ? version : null;
    }

    private static Process? LaunchViaDesktopShell(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(executable);
        return Process.Start(startInfo);
    }

    public static void ApplyUpdate(int pid, string extractDir, string installDir)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "gldrive-update.log");
        void LogUpdate(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }
        var backups = new List<(string Original, string Backup)>();
        var copiedDestinations = new List<string>();
        string? validatedInstallDir = null;

        try
        {
            // Validate paths to prevent arbitrary file overwrite
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var tempDir = Path.GetFullPath(Path.GetTempPath());
            var tempPrefix = tempDir.EndsWith(Path.DirectorySeparatorChar)
                ? tempDir
                : tempDir + Path.DirectorySeparatorChar;

            if (!fullExtract.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
            {
                LogUpdate($"SECURITY: extractDir is not in temp directory — aborting. extractDir={fullExtract}");
                throw new InvalidDataException("Update package directory is outside the system temp directory");
            }

            // Elevated update mode is only valid when launched from the installed executable.
            var callerExeDir = Path.GetFullPath(
                Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!);
            bool callerIsInstallDir = string.Equals(fullInstall, callerExeDir, StringComparison.OrdinalIgnoreCase);
            if (!callerIsInstallDir)
            {
                LogUpdate($"SECURITY: updater was not launched from installDir — aborting. installDir={fullInstall}, callerDir={callerExeDir}");
                throw new InvalidDataException("Updater executable is not running from the install directory");
            }
            validatedInstallDir = fullInstall;

            var authorizationPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", ".update-auth");
            if (!UpdateMarkerHmac.IsValidAuthorization(authorizationPath, pid, fullExtract, fullInstall))
            {
                LogUpdate("SECURITY: update authorization is missing, expired, or does not match staged files — aborting");
                throw new InvalidDataException("Update authorization is invalid");
            }
            using (var verifiedArchive = OpenVerifiedUpdateArchive(fullExtract))
            {
                if (verifiedArchive == null)
                {
                    LogUpdate("SECURITY: publisher signature, version, or archive hash is invalid — aborting");
                    throw new InvalidDataException("Update publisher verification failed");
                }
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

            // Revalidate after waiting so staging changes made while the original process
            // was shutting down cannot cross the elevation boundary.
            if (!UpdateMarkerHmac.IsValidAuthorization(authorizationPath, pid, fullExtract, fullInstall))
            {
                LogUpdate("SECURITY: staged update changed while waiting for shutdown — aborting");
                throw new InvalidDataException("Update package changed during shutdown");
            }
            using var verifiedZip = OpenVerifiedUpdateArchive(fullExtract);
            if (verifiedZip == null)
            {
                LogUpdate("SECURITY: update package failed publisher verification after shutdown — aborting");
                throw new InvalidDataException("Update publisher verification failed after shutdown");
            }
            try { File.Delete(authorizationPath); } catch { }

            // Rename existing files to .old
            LogUpdate("Renaming existing files to .old");
            var renamed = 0;
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var backup = file + ".old";
                    File.Move(file, backup, overwrite: true);
                    backups.Add((file, backup));
                    renamed++;
                }
                catch (Exception ex)
                {
                    throw new IOException($"Could not back up {file}: {ex.Message}", ex);
                }
            }
            LogUpdate($"Renamed {renamed} files");

            // Copy directly from the locked, publisher-verified ZIP. Mutable files in the
            // package directory are never executed or copied into the installation.
            LogUpdate("Copying new files from verified update archive");
            var copied = 0;
            var installDirCanonical = Path.GetFullPath(installDir);
            if (!installDirCanonical.EndsWith(Path.DirectorySeparatorChar))
                installDirCanonical += Path.DirectorySeparatorChar;

            using (var archive = new ZipArchive(verifiedZip, ZipArchiveMode.Read, leaveOpen: true))
            {
                var fileEntries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
                var executableEntry = fileEntries.FirstOrDefault(entry =>
                    entry.FullName.Replace('\\', '/').EndsWith("/GlDrive.exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.Equals("GlDrive.exe", StringComparison.OrdinalIgnoreCase));
                if (executableEntry == null)
                    throw new InvalidDataException("Verified update archive does not contain GlDrive.exe");

                var executablePath = executableEntry.FullName.Replace('\\', '/');
                var archivePrefix = executablePath[..^"GlDrive.exe".Length];
                foreach (var entry in fileEntries)
                {
                    var archivePath = entry.FullName.Replace('\\', '/');
                    if (!archivePath.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var relativePath = archivePath[archivePrefix.Length..];
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    var destCanonical = Path.GetFullPath(Path.Combine(installDir, relativePath));
                    if (!destCanonical.StartsWith(installDirCanonical, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Archive entry escapes install directory: {archivePath}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destCanonical)!);
                    copiedDestinations.Add(destCanonical);
                    using var source = entry.Open();
                    using var destination = new FileStream(destCanonical, FileMode.Create, FileAccess.Write, FileShare.None);
                    source.CopyTo(destination);
                    copied++;
                }
            }
            verifiedZip.Dispose();
            LogUpdate($"Copied {copied} files");

            // Clean up
            LogUpdate("Cleaning up update package");
            try { Directory.Delete(extractDir, true); } catch { }

            // Launch the updated app
            var newExe = Path.Combine(installDir, "GlDrive.exe");
            if (!File.Exists(newExe))
            {
                LogUpdate($"ERROR: {newExe} not found after copy!");
                throw new FileNotFoundException("Updated executable was not copied", newExe);
            }

            // Clean up the update marker so the watchdog doesn't interfere on next run
            try { File.Delete(Path.Combine(installDir, "..", "GlDrive", ".updating")); } catch { }
            var appDataUpdating = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", ".updating");
            try { File.Delete(appDataUpdating); } catch { }

            LogUpdate($"Update complete, launching {newExe}");
            var child = LaunchViaDesktopShell(newExe);

            if (child != null)
            {
                LogUpdate("Desktop shell accepted the application relaunch request");
            }
        }
        catch (Exception ex)
        {
            LogUpdate($"Update FAILED: {ex}");
            LogUpdate("Rolling back partial update");
            foreach (var copied in copiedDestinations.AsEnumerable().Reverse())
            {
                try { File.Delete(copied); }
                catch (Exception rollbackEx) { LogUpdate($"  Could not remove {copied}: {rollbackEx.Message}"); }
            }
            foreach (var (original, backup) in backups.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup)) File.Move(backup, original, overwrite: true);
                }
                catch (Exception rollbackEx) { LogUpdate($"  Could not restore {original}: {rollbackEx.Message}"); }
            }

            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");
            try { File.Delete(Path.Combine(appData, ".updating")); } catch { }
            try { File.Delete(Path.Combine(appData, ".update-auth")); } catch { }

            if (validatedInstallDir != null)
            {
                var restoredExe = Path.Combine(validatedInstallDir, "GlDrive.exe");
                if (File.Exists(restoredExe))
                {
                    try { LaunchViaDesktopShell(restoredExe); }
                    catch (Exception launchEx) { LogUpdate($"Could not relaunch restored app: {launchEx.Message}"); }
                }
            }
        }

        // Force-kill instead of normal teardown because loaded files may have been
        // renamed or replaced while this updater process was running.
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
                    if (AutoInstall && release.TagName != _autoInstallAttemptedTag)
                    {
                        bool ok = true;
                        try { ok = CanInstallNow?.Invoke() ?? true; }
                        catch (Exception ex) { Log.Debug(ex, "CanInstallNow predicate threw — assuming OK"); }

                        if (ok)
                        {
                            _autoInstallAttemptedTag = release.TagName;
                            Log.Information("Auto-installing update {Tag}", release.TagName);
                            try { await DownloadAndInstallAsync(release); }
                            catch (Exception ex)
                            {
                                // Allow a retry on the next tick if it failed.
                                _autoInstallAttemptedTag = null;
                                Log.Warning(ex, "Auto-install of {Tag} failed — will retry", release.TagName);
                            }
                        }
                        else
                        {
                            Log.Information("Update {Tag} ready but deferred (busy) — retrying next check", release.TagName);
                        }
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

/// <summary>
/// Writes and validates HMAC-SHA256-protected .updating marker files so the watchdog
/// can reject forged or replayed markers written by unprivileged user-context processes.
/// Key material is persisted via DPAPI (CurrentUser scope) in a companion .updating-key file.
/// </summary>
public static class UpdateMarkerHmac
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlDrive", ".updating-key");

    private static byte[] GetOrCreateKey()
    {
        if (File.Exists(KeyPath))
        {
            try
            {
                var enc = File.ReadAllBytes(KeyPath);
                return ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            }
            catch { }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(KeyPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(KeyPath, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return key;
    }

    private static byte[] ComputeHmac(byte[] key, string payload)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    public static void Write(string markerPath, int processId)
    {
        try
        {
            var key = GetOrCreateKey();
            var payload = $"{processId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Environment.ProcessPath ?? string.Empty}";
            var mac = Convert.ToHexString(ComputeHmac(key, payload));
            var dir = Path.GetDirectoryName(markerPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, payload + "\n" + mac);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write HMAC update marker at {Path}", markerPath);
        }
    }

    public static void WriteAuthorization(string markerPath, int processId, string extractDir, string installDir)
    {
        try
        {
            var key = GetOrCreateKey();
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var manifest = ComputeDirectoryManifest(fullExtract);
            var payload = $"{processId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{fullExtract}|{fullInstall}|{manifest}";
            var mac = Convert.ToHexString(ComputeHmac(key, payload));
            var dir = Path.GetDirectoryName(markerPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, payload + "\n" + mac);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write update authorization at {Path}", markerPath);
        }
    }

    public static bool IsValidAuthorization(
        string markerPath, int processId, string extractDir, string installDir)
    {
        if (!TryReadValidPayload(markerPath, out var payload)) return false;
        try
        {
            var parts = payload.Split('|');
            if (parts.Length != 5 || !int.TryParse(parts[0], out var markerPid) || markerPid != processId)
                return false;
            if (!long.TryParse(parts[1], out var ts) ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
                return false;

            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            if (!string.Equals(parts[2], fullExtract, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[3], fullInstall, StringComparison.OrdinalIgnoreCase))
                return false;

            var currentManifest = ComputeDirectoryManifest(fullExtract);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(parts[4]), Convert.FromHexString(currentManifest));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the marker exists AND has a valid HMAC AND the embedded timestamp
    /// is within 5 minutes of now. Plain (non-HMAC) markers from older versions are rejected.
    /// </summary>
    public static bool IsValid(string markerPath)
    {
        if (!TryReadValidPayload(markerPath, out var payload)) return false;
        var parts = payload.Split('|');
        return parts.Length >= 2 && long.TryParse(parts[1], out var ts) &&
               Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= 300;
    }

    private static bool TryReadValidPayload(string markerPath, out string payload)
    {
        payload = string.Empty;
        if (!File.Exists(markerPath)) return false;
        try
        {
            var text = File.ReadAllText(markerPath).Trim();
            var nl = text.IndexOf('\n');
            if (nl < 0) return false;
            payload = text[..nl].Trim();
            var storedMac = Convert.FromHexString(text[(nl + 1)..].Trim());
            var expectedMac = ComputeHmac(GetOrCreateKey(), payload);
            return CryptographicOperations.FixedTimeEquals(expectedMac, storedMac);
        }
        catch
        {
            payload = string.Empty;
            return false;
        }
    }

    private static string ComputeDirectoryManifest(string directory)
    {
        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse point in update staging: {file}");
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            manifestHash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            using var stream = File.OpenRead(file);
            manifestHash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(manifestHash.GetHashAndReset());
    }
}
