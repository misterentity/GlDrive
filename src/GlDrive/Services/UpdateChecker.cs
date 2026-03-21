using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
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

    private readonly HttpClient _http;
    private readonly string _installPath;
    private CancellationTokenSource? _periodicCts;

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public event Action<GitHubRelease>? UpdateAvailable;
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

            // Compare major.minor.build (ignore revision)
            var current = new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build);
            var remote = release.ParsedVersion;
            var remoteNormalized = new Version(remote.Major, remote.Minor, Math.Max(remote.Build, 0));

            if (remoteNormalized > current)
            {
                Log.Information("Update available: {Current} → {Remote}", current, remoteNormalized);
                return release;
            }

            Log.Debug("Up to date: {Current} (latest: {Remote})", current, remoteNormalized);
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

        var tempZip = Path.Combine(Path.GetTempPath(), $"GlDrive-{release.TagName}.zip");
        Log.Information("Downloading update: {Url} → {Path}", asset.BrowserDownloadUrl, tempZip);

        // Stream download to disk to avoid buffering 150MB+ in memory
        using var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var fs = File.Create(tempZip))
        {
            await response.Content.CopyToAsync(fs);
        }

        Log.Information("Download complete ({Size} MB), launching updater",
            new FileInfo(tempZip).Length / (1024.0 * 1024));
        LaunchUpdater(tempZip);
    }

    private void LaunchUpdater(string zipPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"gldrive-update-{Guid.NewGuid():N}");

        // Clean previous extraction
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);

        Log.Information("Extracting update to {Path}", extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // Detect nested folder — if zip has single subfolder containing GlDrive.exe, use that
        var entries = Directory.GetDirectories(extractDir);
        if (entries.Length == 1 && File.Exists(Path.Combine(entries[0], "GlDrive.exe")))
        {
            Log.Information("Detected nested folder: {Path}", entries[0]);
            extractDir = entries[0];
        }

        if (!File.Exists(Path.Combine(extractDir, "GlDrive.exe")))
        {
            Log.Error("Extraction failed — GlDrive.exe not found in {Path}", extractDir);
            return;
        }

        // Clean up the downloaded zip
        try { File.Delete(zipPath); } catch { /* best effort */ }

        var pid = Environment.ProcessId;
        var exePath = Path.Combine(extractDir, "GlDrive.exe");
        var installDir = _installPath.TrimEnd(Path.DirectorySeparatorChar);

        Log.Information("Launching elevated updater: --apply-update {Pid} \"{ExtractDir}\" \"{InstallDir}\"",
            pid, extractDir, installDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--apply-update {pid} \"{extractDir}\" \"{installDir}\"",
            Verb = "runas",
            UseShellExecute = true
        });

        RestartRequested?.Invoke();
    }

    public static void ApplyUpdate(int pid, string extractDir, string installDir)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "gldrive-update.log");
        void LogUpdate(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* best effort */ }
        }

        try
        {
            LogUpdate($"ApplyUpdate started — pid={pid}, extractDir={extractDir}, installDir={installDir}");

            // Wait for the original process to exit
            try
            {
                var proc = Process.GetProcessById(pid);
                LogUpdate($"Waiting for PID {pid} to exit...");
                proc.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // Process already exited
                LogUpdate($"PID {pid} already exited");
            }

            // Small grace period for file handles to release
            Thread.Sleep(1000);

            // Rename existing files to .old (Windows allows renaming in-use files)
            LogUpdate("Renaming existing files to .old");
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                // Skip files already named .old
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Move(file, file + ".old", overwrite: true);
                }
                catch (Exception ex)
                {
                    LogUpdate($"  Warning: could not rename {file}: {ex.Message}");
                }
            }

            // Copy new files from extract dir to install dir
            LogUpdate("Copying new files");
            foreach (var sourceFile in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(extractDir, sourceFile);
                var destFile = Path.Combine(installDir, relativePath);

                var destDir = Path.GetDirectoryName(destFile)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                File.Copy(sourceFile, destFile, overwrite: true);
            }

            // Clean up extract dir
            LogUpdate("Cleaning up extract directory");
            try { Directory.Delete(extractDir, true); } catch { /* best effort */ }

            // If extractDir was a nested subfolder, also try to clean the parent temp extract dir
            var parentExtract = Path.Combine(Path.GetTempPath(), "gldrive-update-extract");
            if (Directory.Exists(parentExtract))
                try { Directory.Delete(parentExtract, true); } catch { /* best effort */ }

            // Launch the updated app
            var newExe = Path.Combine(installDir, "GlDrive.exe");
            LogUpdate($"Update complete, launching {newExe}");
            Process.Start(new ProcessStartInfo
            {
                FileName = newExe,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogUpdate($"Update FAILED: {ex}");
        }

        Environment.Exit(0);
    }

    public static void CleanupOldUpdateFiles()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(baseDir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(file); }
                catch { /* best effort — file may still be locked */ }
            }
        }
        catch
        {
            // Non-critical, swallow
        }
    }

    public void StartPeriodicCheck(CancellationToken cancellationToken = default)
    {
        _periodicCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _periodicCts.Token;

        _ = Task.Run(async () =>
        {
            // Initial delay — let the app finish starting up
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            while (!token.IsCancellationRequested)
            {
                var release = await CheckForUpdateAsync();
                if (release != null)
                    UpdateAvailable?.Invoke(release);

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
