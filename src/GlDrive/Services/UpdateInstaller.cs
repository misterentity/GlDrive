using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// The separate, elevated updater process: the <c>--apply-update</c>, <c>--apply-update-task</c>,
/// <c>--rollback-update</c> and <c>--cleanup-old-updates</c> entry points dispatched by
/// <see cref="Program"/>, and the install/rollback/cleanup steps they run. The in-app side that
/// polls, downloads, verifies and launches this process is <see cref="UpdateChecker"/>.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Registers (or refreshes) the SYSTEM update task from an ALREADY-ELEVATED context.
    ///
    /// Without this the task would only ever appear via the Inno installer, and an auto-update
    /// applied from the release zip would silently keep falling back to the UAC prompt this task
    /// exists to remove. Calling it at the end of every elevated apply means the feature
    /// bootstraps itself: one elevated install of any kind, and every later update is unattended.
    ///
    /// Best-effort by design. A failure here must never fail an update that has already succeeded,
    /// and the fallback path still works.
    /// </summary>
    internal static void TryRegisterUpdateTask(string installDir, Action<string> log)
        => TryRegisterUpdateTask(installDir, log, TimeSpan.FromSeconds(60));

    internal static void TryRegisterUpdateTask(string installDir, Action<string> log, TimeSpan timeout)
    {
        try
        {
            var script = Path.Combine(installDir, "register-update-task.ps1");
            if (!File.Exists(script))
            {
                log($"Update task registration skipped: {script} not present in this build");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-InstallDir");
            psi.ArgumentList.Add(installDir);

            var result = UpdateProcessRunner.Run(psi, timeout);
            if (result.TimedOut)
            {
                log("Update task registration timed out");
                return;
            }
            log($"Update task registration exit={result.ExitCode} {result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim());
        }
        catch (Exception ex)
        {
            log($"Update task registration failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Entry point for <c>--apply-update-task</c>: the SYSTEM scheduled task's action. Reads the
    /// hand-off, then runs the same verified install path the interactive updater uses. The
    /// install directory is this process's OWN directory — it is never taken from the hand-off,
    /// so a forged record cannot redirect where files land.
    /// </summary>
    public static int ApplyUpdateFromTask()
    {
        var path = UpdateTaskHandoff.DefaultPath;
        var taskLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GlDrive", "update-task.log");
        void TaskLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(taskLogPath)!);
                File.AppendAllText(taskLogPath,
                    $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        TaskLog("SYSTEM updater entry point started");
        var handoff = UpdateTaskHandoff.TryRead(path, DateTime.UtcNow,
            reason => TaskLog($"Rejected handoff: {reason}"));
        if (handoff is null)
        {
            Log.Warning("Update task ran with no valid pending hand-off at {Path} — nothing to do", path);
            return 1;
        }

        TaskLog($"Accepted handoff for {handoff.Tag}");
        var installDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        Log.Information("Update task applying {Tag} from {PackageDir} to {InstallDir}",
            handoff.Tag, handoff.PackageDir, installDir);

        // ApplyUpdate intentionally terminates this process after success OR rollback, so code
        // after the call (including a finally block) can never consume the request. Delete it
        // before dispatch to prevent a stale/replayable SYSTEM handoff.
        UpdateTaskHandoff.Clear(path);
        if (File.Exists(path))
        {
            TaskLog($"Could not consume handoff for {handoff.Tag}; refusing dispatch");
            return 1;
        }
        TaskLog($"Consumed handoff for {handoff.Tag}; dispatching updater");
        ApplyUpdate(handoff.Pid, handoff.PackageDir, installDir, viaScheduledTask: true);
        return 1; // ApplyUpdate normally terminates this process; never report a fallback as success.
    }

    /// <summary>
    /// Interactive elevation is authorized by a CurrentUser-DPAPI HMAC marker. The fixed-action
    /// SYSTEM task cannot use that user's DPAPI scope; instead it pins the destination to its own
    /// executable directory and verifies the publisher signature, version, and locked archive
    /// twice. See <see cref="UpdateTaskHandoff"/> for the complete task threat model.
    /// </summary>
    internal static bool RequiresUpdateAuthorization(bool viaScheduledTask) => !viaScheduledTask;

    private static FileStream? OpenVerifiedUpdateArchive(string packageDir)
    {
        try
        {
            var checksumText = File.ReadAllText(Path.Combine(packageDir, "checksums.sha256"));
            var signatureText = File.ReadAllText(Path.Combine(packageDir, "checksums.sha256.sig"));
            var assetName = File.ReadAllText(Path.Combine(packageDir, "asset-name.txt")).Trim();
            if (!UpdateChecksumSignature.Verify(checksumText, signatureText)) return null;

            var packageVersion = ParseUpdateAssetVersion(assetName);
            if (packageVersion == null || packageVersion <= UpdateChecker.CurrentVersion)
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

    internal static int ExtractVerifiedArchiveToDirectory(Stream zipStream, string stagingDir)
    {
        var stagingRoot = Path.GetFullPath(stagingDir);
        var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar)
            ? stagingRoot
            : stagingRoot + Path.DirectorySeparatorChar;

        if (Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            throw new InvalidDataException("Update staging directory is not empty");
        Directory.CreateDirectory(stagingRoot);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        var fileEntries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var executableEntry = fileEntries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/').EndsWith("/GlDrive.exe", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Equals("GlDrive.exe", StringComparison.OrdinalIgnoreCase));
        if (executableEntry == null)
            throw new InvalidDataException("Verified update archive does not contain GlDrive.exe");

        var executablePath = executableEntry.FullName.Replace('\\', '/');
        var archivePrefix = executablePath[..^"GlDrive.exe".Length];
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extracted = 0;

        foreach (var entry in fileEntries)
        {
            var archivePath = entry.FullName.Replace('\\', '/');
            if (!archivePath.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = archivePath[archivePrefix.Length..];
            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
            if (!destinationPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry escapes staging directory: {archivePath}");
            if (!destinations.Add(destinationPath))
                throw new InvalidDataException($"Archive contains duplicate destination: {archivePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = entry.Open();
            using var destination = new FileStream(destinationPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            extracted++;
        }

        if (!File.Exists(Path.Combine(stagingRoot, "GlDrive.exe")))
            throw new InvalidDataException("Verified update archive did not stage GlDrive.exe");
        return extracted;
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

    internal static int BackUpInstalledFiles(
        string installDir, ICollection<(string Original, string Backup)> backups)
    {
        // A still-running MCP process can retain DLLs from an earlier update. Never reuse or
        // overwrite those backups: Windows can allow the current DLL to be renamed while
        // denying deletion of its old mapped image. Keep the .old suffix for normal cleanup.
        var suffix = $".{Guid.NewGuid():N}.old";
        var renamed = 0;
        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || UpdateProbation.IsMetadataPath(installDir, file))
                continue;

            try
            {
                var backup = file + suffix;
                File.Move(file, backup, overwrite: false);
                // Record each successful move immediately, so a later failure can restore only
                // this attempt's files without touching any earlier, possibly locked backup.
                backups.Add((file, backup));
                renamed++;
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not back up {file}: {ex.Message}", ex);
            }
        }
        return renamed;
    }

    internal static void RollBackUpdateFiles(
        IEnumerable<string> copiedDestinations,
        IEnumerable<(string Original, string Backup)> backups,
        Action<string> log)
    {
        foreach (var copied in copiedDestinations.Reverse())
        {
            try { File.Delete(copied); }
            catch (Exception ex) { log($"  Could not remove {copied}: {ex.Message}"); }
        }
        foreach (var (original, backup) in backups.Reverse())
        {
            try
            {
                if (File.Exists(backup)) File.Move(backup, original, overwrite: true);
            }
            catch (Exception ex) { log($"  Could not restore {original}: {ex.Message}"); }
        }
    }

    internal static string UpdateInstallLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GlDrive", "update-install.log");

    internal static void WriteUpdateLog(string message, string tempLogPath, string sharedLogPath)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        foreach (var path in new[] { tempLogPath, sharedLogPath })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
            }
            catch { } // One inaccessible sink must not suppress the other or fail the update.
        }
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

    /// <param name="viaScheduledTask">
    /// True when invoked by the SYSTEM scheduled task rather than an interactive elevation. The
    /// caller-side temp check below compares against <c>Path.GetTempPath()</c>, which for SYSTEM
    /// is C:\Windows\Temp and never the interactive user's temp directory — so it would reject
    /// every legitimate package. <see cref="UpdateTaskHandoff.IsPlausiblePackageDir"/> has already
    /// enforced the equivalent constraint (a Temp path segment, not the temp root itself, holding
    /// the four staging files) before this is reached.
    /// </param>
    public static void ApplyUpdate(int pid, string extractDir, string installDir,
        bool viaScheduledTask = false)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "gldrive-update.log");
        var sharedLogPath = UpdateInstallLogPath;
        void LogUpdate(string msg) => WriteUpdateLog(msg, logPath, sharedLogPath);
        var backups = new List<(string Original, string Backup)>();
        var copiedDestinations = new List<string>();
        string? validatedInstallDir = null;
        FileStream? recoveryMutation = null;
        var anotherMutationMayOwnRelaunch = false;
        var exitCode = 1;

        try
        {
            // Validate paths to prevent arbitrary file overwrite
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var tempDir = Path.GetFullPath(Path.GetTempPath());
            var tempPrefix = tempDir.EndsWith(Path.DirectorySeparatorChar)
                ? tempDir
                : tempDir + Path.DirectorySeparatorChar;

            if (viaScheduledTask)
            {
                // SYSTEM's temp is not the user's; validate the shape instead of the prefix.
                if (!UpdateTaskHandoff.IsPlausiblePackageDir(fullExtract))
                {
                    LogUpdate($"SECURITY: task-mode extractDir failed staging validation — aborting. extractDir={fullExtract}");
                    throw new InvalidDataException("Update package directory is not a valid staging directory");
                }
            }
            else if (!fullExtract.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
            {
                LogUpdate($"SECURITY: extractDir is not in temp directory — aborting. extractDir={fullExtract}");
                throw new InvalidDataException("Update package directory is outside the system temp directory");
            }

            // Keep a known-good relaunch target even when validation fails before any
            // files are changed. Otherwise a rejected elevated handoff leaves the user
            // with no running application.
            if (File.Exists(Path.Combine(fullInstall, "GlDrive.exe")))
                validatedInstallDir = fullInstall;

            var callerExePath = Path.GetFullPath(Environment.ProcessPath ??
                Path.Combine(AppContext.BaseDirectory, "GlDrive.exe"));
            var callerExeDir = Path.GetFullPath(Path.GetDirectoryName(callerExePath)!);
            bool callerIsInstallDir = string.Equals(fullInstall, callerExeDir, StringComparison.OrdinalIgnoreCase);

            // Releases through 3.10.23 launched the newly extracted GlDrive.exe as the
            // elevated updater. Keep a constrained bridge for those clients: the legacy
            // elevated executable must be GlDrive.exe in the supplied staging folder.
            bool legacyExtractedHandoff = !callerIsInstallDir &&
                IsLegacyExtractedHandoff(callerExePath, fullExtract, fullInstall);
            if (!callerIsInstallDir && !legacyExtractedHandoff)
            {
                LogUpdate($"SECURITY: updater was not launched from installDir — aborting. installDir={fullInstall}, callerDir={callerExeDir}");
                throw new InvalidDataException("Updater executable is not running from the install directory");
            }

            var authorizationPath = Path.Combine(
                GlDrive.Util.AppDataPaths.Root, ".update-auth");

            if (legacyExtractedHandoff)
            {
                LogUpdate("Legacy extracted updater handoff accepted; sealing staged file manifest");
                UpdateMarkerHmac.WriteAuthorization(authorizationPath, pid, fullExtract, fullInstall);
            }

            if (RequiresUpdateAuthorization(viaScheduledTask) &&
                !UpdateMarkerHmac.IsValidAuthorization(authorizationPath, pid, fullExtract, fullInstall))
            {
                LogUpdate("SECURITY: update authorization is missing, expired, or does not match staged files — aborting");
                throw new InvalidDataException("Update authorization is invalid");
            }
            if (!legacyExtractedHandoff)
            {
                using var verifiedArchive = OpenVerifiedUpdateArchive(fullExtract);
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
            if (RequiresUpdateAuthorization(viaScheduledTask) &&
                !UpdateMarkerHmac.IsValidAuthorization(authorizationPath, pid, fullExtract, fullInstall))
            {
                LogUpdate("SECURITY: staged update changed while waiting for shutdown — aborting");
                throw new InvalidDataException("Update package changed during shutdown");
            }
            FileStream? verifiedZip = null;
            if (!legacyExtractedHandoff)
            {
                verifiedZip = OpenVerifiedUpdateArchive(fullExtract);
                if (verifiedZip == null)
                {
                    LogUpdate("SECURITY: update package failed publisher verification after shutdown — aborting");
                    throw new InvalidDataException("Update publisher verification failed after shutdown");
                }
            }
            if (RequiresUpdateAuthorization(viaScheduledTask))
            {
                try { File.Delete(authorizationPath); } catch { }
            }

            // LOAD-BEARING ORDER: decompress the whole archive to a staging folder BEFORE any
            // install file is renamed. .NET binds System.IO.Compression.Native.dll lazily, on the
            // first actual decompression. That used to happen at entry.Open() in the copy loop —
            // i.e. AFTER the rename below had moved that very DLL to .old — so every update died
            // with "ZLibException: The underlying compression routine could not be loaded
            // correctly" and fell into an endless fail -> rollback -> relaunch -> retry loop.
            // Extracting first means nothing after the rename needs anything but File.Copy.
            string? stagedDir = null;
            if (!legacyExtractedHandoff)
            {
                stagedDir = Path.Combine(fullExtract, "staged");
                var stagedCount = ExtractVerifiedArchiveToDirectory(verifiedZip!, stagedDir);
                verifiedZip = null; // ExtractVerifiedArchiveToDirectory disposes the stream
                LogUpdate($"Extracted {stagedCount} files from verified archive to staging");
            }

            // Rename existing files to .old
            if (UpdateInstallationTrust.IsProtected(installDir))
            {
                try { recoveryMutation = UpdateProbation.ForInstalledRoot(installDir).AcquireMutationLock(); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                {
                    anotherMutationMayOwnRelaunch = true;
                    throw;
                }
                if (!legacyExtractedHandoff && UpdateChecker.Normalize(AssemblyName.GetAssemblyName(Path.Combine(installDir, "GlDrive.dll")).Version!) != UpdateChecker.Normalize(UpdateChecker.CurrentVersion))
                    throw new IOException("The installed version changed while this updater was waiting");
            }
            LogUpdate("Renaming existing files to .old");
            var renamed = BackUpInstalledFiles(installDir, backups);
            LogUpdate($"Renamed {renamed} files");

            var copied = 0;
            var installDirCanonical = Path.GetFullPath(installDir);
            if (!installDirCanonical.EndsWith(Path.DirectorySeparatorChar))
                installDirCanonical += Path.DirectorySeparatorChar;

            if (legacyExtractedHandoff)
            {
                LogUpdate("Copying files from sealed legacy update staging");
                foreach (var source in UpdateMarkerHmac.EnumerateManifestFiles(fullExtract))
                {
                    var relativePath = Path.GetRelativePath(fullExtract, source);
                    if (string.IsNullOrWhiteSpace(relativePath))
                        continue;

                    var destCanonical = Path.GetFullPath(Path.Combine(installDir, relativePath));
                    if (!destCanonical.StartsWith(installDirCanonical, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Staged file escapes install directory: {relativePath}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destCanonical)!);
                    copiedDestinations.Add(destCanonical);
                    File.Copy(source, destCanonical, overwrite: true);
                    copied++;
                }
            }
            else
            {
                // Copy from the staging folder extracted above out of the locked,
                // publisher-verified ZIP. Nothing here decompresses, so the just-renamed
                // native compression DLL is not needed. Mutable files in the package
                // directory are still never executed or copied into the installation.
                LogUpdate("Copying new files from verified update staging");
                var stagedRoot = Path.GetFullPath(stagedDir!);
                foreach (var source in Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(stagedRoot, source);
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    var destCanonical = Path.GetFullPath(Path.Combine(installDir, relativePath));
                    if (!destCanonical.StartsWith(installDirCanonical, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Staged file escapes install directory: {relativePath}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destCanonical)!);
                    copiedDestinations.Add(destCanonical);
                    File.Copy(source, destCanonical, overwrite: true);
                    copied++;
                }
            }
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

            // The current binary is still the old updater. The receipt records versions from
            // the protected files, not this process's assembly version. Failure keeps backups.
            LogUpdate("Recording update recovery receipt");
            UpdateProbation.ForInstalledRoot(installDir).RecordInstallation(backups, DateTime.UtcNow, mutationLockHeld: recoveryMutation != null);

            // Clean up the update marker so the watchdog doesn't interfere on next run
            try { File.Delete(Path.Combine(installDir, "..", "GlDrive", ".updating")); } catch { }
            var appDataUpdating = Path.Combine(
                GlDrive.Util.AppDataPaths.Root, ".updating");
            try { File.Delete(appDataUpdating); } catch { }

            // We are elevated RIGHT NOW and the new files are in place. Register the SYSTEM
            // update task here so a box updated from the release zip (which never runs the Inno
            // installer) still gets it — otherwise this whole feature only ever reaches machines
            // that happen to run the full installer, and everything else keeps prompting for UAC.
            LogUpdate("Registering update tasks");
            TryRegisterUpdateTask(installDir, LogUpdate);

            LogUpdate($"Update complete, launching {newExe}");
            var child = LaunchViaDesktopShell(newExe);

            if (child != null)
            {
                LogUpdate("Desktop shell accepted the application relaunch request");
            }
            exitCode = 0;
        }
        catch (Exception ex)
        {
            LogUpdate($"Update FAILED: {ex}");
            LogUpdate("Rolling back partial update");
            RollBackUpdateFiles(copiedDestinations, backups, LogUpdate);

            var appData = GlDrive.Util.AppDataPaths.Root;
            try { File.Delete(Path.Combine(appData, ".updating")); } catch { }
            try { File.Delete(Path.Combine(appData, ".update-auth")); } catch { }

            if (validatedInstallDir != null && !anotherMutationMayOwnRelaunch)
            {
                var restoredExe = Path.Combine(validatedInstallDir, "GlDrive.exe");
                if (File.Exists(restoredExe))
                {
                    try { LaunchViaDesktopShell(restoredExe); }
                    catch (Exception launchEx) { LogUpdate($"Could not relaunch restored app: {launchEx.Message}"); }
                }
            }
        }

        recoveryMutation?.Dispose();
        // Signal the original interactive-session watchdog only after every install or rollback
        // operation is complete. A SYSTEM process cannot reliably launch a desktop application
        // into the signed-in user's session; the watchdog can.
        if (!anotherMutationMayOwnRelaunch) SignalUpdateCompletion(pid);

        // Force-kill instead of normal teardown because loaded files may have been
        // renamed or replaced while this updater process was running. Process.Kill()
        // always reports -1, which made successful scheduled installs look failed.
        // Preserve that native termination behavior while reporting the install result.
        LogUpdate($"Updater exiting with code {exitCode}");
        Thread.Sleep(2000);
        TerminateUpdaterProcess(exitCode);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void TerminateUpdaterProcess(int exitCode)
    {
        // -1 is Windows' current-process pseudo-handle. Terminating ourselves does not
        // return or run managed/native teardown; unlike Environment.Exit it cannot hang
        // on finalizers after the runtime files were renamed by the update.
        try { TerminateProcess(new IntPtr(-1), unchecked((uint)exitCode)); }
        catch { /* Continue to the original hard-stop fallback. */ }
        // Defensive only: self-termination should never return. Retain the old hard-stop
        // safeguard if Windows refuses it; do not run ordinary shutdown on replaced files.
        try { Process.GetCurrentProcess().Kill(); }
        catch { /* Never unwind through ordinary teardown of replaced files. */ }
        Environment.FailFast("Could not terminate the updater process");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static string UpdateCompletionPath(int pid) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GlDrive", $"update-complete-{pid}.signal");

    private static void SignalUpdateCompletion(int pid)
    {
        try
        {
            var path = UpdateCompletionPath(pid);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    internal static bool IsLegacyExtractedHandoff(
        string callerExePath, string extractDir, string installDir)
    {
        try
        {
            var fullCaller = Path.GetFullPath(callerExePath);
            var fullExtract = Path.GetFullPath(extractDir);
            var fullInstall = Path.GetFullPath(installDir);
            var callerDir = Path.GetFullPath(Path.GetDirectoryName(fullCaller)!);
            var stagedExe = Path.Combine(fullExtract, "GlDrive.exe");
            var installedExe = Path.Combine(fullInstall, "GlDrive.exe");
            var stagingName = Path.GetFileName(fullExtract.TrimEnd(Path.DirectorySeparatorChar));

            // The 3.10.23 watchdog can delete its marker before this elevated process
            // reads it, and an over-the-shoulder UAC credential can resolve a different
            // user profile. The marker therefore cannot participate in this one-time
            // legacy bridge. Constrain the handoff by exact staging layout instead, then
            // seal and revalidate the complete file manifest before any install changes.
            return string.Equals(Path.GetFileName(fullCaller), "GlDrive.exe", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(callerDir, fullExtract, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(fullCaller, stagedExe, StringComparison.OrdinalIgnoreCase) &&
                   stagingName.StartsWith("gldrive-update-", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(stagedExe) &&
                   File.Exists(Path.Combine(fullExtract, "GlDrive.dll")) &&
                   File.Exists(Path.Combine(fullExtract, "GlDrive.deps.json")) &&
                   File.Exists(Path.Combine(fullExtract, "GlDrive.runtimeconfig.json")) &&
                   File.Exists(installedExe);
        }
        catch
        {
            return false;
        }
    }

    internal static (int Deleted, int Remaining) DeleteOldUpdateFiles(string baseDir)
    {
        var deleted = 0;
        string[] oldFiles;
        try
        {
            oldFiles = Directory.GetFiles(
                Path.GetFullPath(baseDir), "*.old", SearchOption.AllDirectories);
        }
        catch
        {
            return (0, 1);
        }

        foreach (var file in oldFiles)
        {
            try { File.Delete(file); deleted++; }
            catch { }
        }

        return (deleted, oldFiles.Count(File.Exists));
    }

    /// <summary>
    /// Headless fixed-action scheduled-task entry point. The cleanup root is deliberately the
    /// executable's own directory and is never accepted from the command line or another file.
    /// </summary>
    public static int CleanupOldUpdateFilesFromTask()
    {
        // The updated desktop process can start a moment before the old SYSTEM updater exits.
        // Preserve every rollback file until that window closes, then retry while Windows
        // releases the updater's mapped runtime DLLs (34 were still locked on the first live
        // v3.10.103 cleanup pass and all disappeared as soon as the updater exited).
        var result = UpdateProbation.InstalledStore().CleanupWithRetry(
            initialDelay: TimeSpan.FromSeconds(10),
            retryWindow: TimeSpan.FromSeconds(30),
            retryDelay: TimeSpan.FromSeconds(1));
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GlDrive", "update-cleanup.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTime.UtcNow:O}] root={Path.GetFullPath(AppContext.BaseDirectory)} " +
                $"deleted={result.Deleted} retained={result.Retained}{Environment.NewLine}");
        }
        catch { }

        // Retention is expected during probation and is not a task failure.
        return 0;
    }

    internal static int RollbackFromLocalRequest(int pid, string generation)
    {
        if (pid <= 0 || !Guid.TryParseExact(generation, "N", out _)) return 1;
        var executable = Environment.ProcessPath;
        if (executable == null || !UpdateInstallationTrust.IsProtected(executable) ||
            !Path.GetFullPath(executable).Equals(Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "GlDrive.exe"), StringComparison.OrdinalIgnoreCase)) return 1;
        // Read only. An alternate administrator account has a different profile, so that
        // elevation is refused instead of accepting a caller-supplied profile directory.
        if (!UpdateRollbackGuard.IsPrepared(GlDrive.Util.AppDataPaths.Root, UpdateChecker.CurrentVersion))
            return RejectLocalRollback("The automatic-reinstall guard could not be verified in this Windows account. Use the same account for administrator approval.");
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.SessionId != Process.GetCurrentProcess().SessionId ||
                !string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                return RejectLocalRollback("The requesting application no longer matches this recovery request.");
            if (!parent.WaitForExit(60_000)) return RejectLocalRollback("GlDrive did not finish closing. No files were changed; close it and try recovery again.");
        }
        catch (ArgumentException) { /* The requesting process already exited cleanly. */ }
        catch { return RejectLocalRollback("The requesting application could not be verified. No files were changed."); }
        if (!UpdateRollbackGuard.IsPrepared(GlDrive.Util.AppDataPaths.Root, UpdateChecker.CurrentVersion))
            return RejectLocalRollback("Update state changed while GlDrive was closing. No files were changed.");
        var exitCode = 1;
        GlDrive.Config.ConfigDowngradeCompatibility.Preparation? profilePreparation = null;
        var payloadRestored = false;
        try
        {
            profilePreparation = GlDrive.Config.ConfigDowngradeCompatibility.Prepare(GlDrive.Config.ConfigManager.ConfigPath);
            UpdateProbation.InstalledStore().Rollback(generation, DateTime.UtcNow);
            payloadRestored = true;
            LaunchViaDesktopShell(executable);
            exitCode = 0;
        }
        catch (Exception error)
        {
            try { if (!payloadRestored) profilePreparation?.Restore(); }
            catch (Exception restoreError) { UpdateProbation.LogRollbackFailure("Profile restoration failed: " + restoreError); }
            UpdateProbation.LogRollbackFailure("Local rollback failed: " + error);
            ShowLocalRollbackFailure("Rollback did not complete. The helper attempted to restore the current build and retained its recovery files. See .update-recovery/rollback.log in the installation folder before trying manual repair.");
        }
        TerminateUpdaterProcess(exitCode);
        return exitCode;
    }

    private static int RejectLocalRollback(string message)
    {
        UpdateProbation.LogRollbackFailure("Local rollback refused: " + message);
        ShowLocalRollbackFailure(message);
        return 1;
    }

    private static void ShowLocalRollbackFailure(string message)
    {
        if (Environment.UserInteractive)
            try { RollbackMessageBox(IntPtr.Zero, message, "GlDrive recovery", 0x10); } catch { }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int RollbackMessageBox(IntPtr owner, string text, string caption, uint type);
}
