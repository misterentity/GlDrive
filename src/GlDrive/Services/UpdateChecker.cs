using System.Diagnostics;
using System.IO;
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

public class UpdateChecker
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

        using var response = await _http.GetAsync(asset.BrowserDownloadUrl);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(tempZip);
        await response.Content.CopyToAsync(fs);
        fs.Close();

        Log.Information("Download complete, launching updater");
        LaunchUpdater(tempZip);
    }

    private void LaunchUpdater(string zipPath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "gldrive-update.ps1");
        var exePath = Path.Combine(_installPath, "GlDrive.exe");
        var logPath = Path.Combine(Path.GetTempPath(), "gldrive-update.log");
        var extractTemp = Path.Combine(Path.GetTempPath(), "gldrive-update-extract");

        // Escape for PowerShell single-quoted strings
        string Esc(string s) => s.Replace("'", "''");

        var script = string.Join(Environment.NewLine,
            "# GlDrive auto-updater",
            $"Start-Transcript -Path '{Esc(logPath)}' -Force",
            "",
            "try {",
            "",
            "# Wait for GlDrive to exit",
            "$proc = Get-Process -Name 'GlDrive' -ErrorAction SilentlyContinue",
            "if ($proc) {",
            "    Write-Host 'Waiting for GlDrive to exit...'",
            "    $proc | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue",
            "    Start-Sleep -Seconds 2",
            "}",
            "",
            "# Extract to temp directory first",
            $"$extractDir = '{Esc(extractTemp)}'",
            "if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }",
            $"Write-Host 'Extracting to temp: ' $extractDir",
            $"Expand-Archive -Path '{Esc(zipPath)}' -DestinationPath $extractDir -Force",
            "",
            "# Detect nested folder — if zip contains a single subfolder with GlDrive.exe, use that",
            "$sourceDir = $extractDir",
            "$children = Get-ChildItem $extractDir",
            "if ($children.Count -eq 1 -and $children[0].PSIsContainer) {",
            "    $nested = $children[0].FullName",
            "    if (Test-Path (Join-Path $nested 'GlDrive.exe')) {",
            "        Write-Host \"Detected nested folder: $nested\"",
            "        $sourceDir = $nested",
            "    }",
            "}",
            "",
            "# Verify extraction produced GlDrive.exe",
            "if (-not (Test-Path (Join-Path $sourceDir 'GlDrive.exe'))) {",
            "    Write-Error 'Extraction failed — GlDrive.exe not found in extracted files'",
            "    throw 'Extraction failed'",
            "}",
            "",
            "# Copy extracted files over install directory (robocopy avoids PowerShell Copy-Item nested folder bug)",
            $"$installDir = '{Esc(_installPath)}'",
            "Write-Host \"Copying files to $installDir\"",
            "& robocopy $sourceDir $installDir /E /R:3 /W:1 /NFL /NDL /NJH /NJS",
            "if ($LASTEXITCODE -ge 8) { throw \"robocopy failed with exit code $LASTEXITCODE\" }",
            "",
            "# Clean up temp",
            "Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue",
            $"Remove-Item -Path '{Esc(zipPath)}' -Force -ErrorAction SilentlyContinue",
            "",
            "Write-Host 'Update complete, launching GlDrive...'",
            $"Start-Process '{Esc(exePath)}'",
            "",
            "} catch {",
            "    Write-Error \"Update failed: $_\"",
            "    Write-Host 'Press any key to exit...'",
            "    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')",
            "} finally {",
            "    Stop-Transcript",
            "}"
        );

        File.WriteAllText(scriptPath, script);
        Log.Information("Updater script written to {Path}", scriptPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-ExecutionPolicy Bypass -NoExit -File \"{scriptPath}\"",
            Verb = "runas",
            UseShellExecute = true
        });

        RestartRequested?.Invoke();
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
}
