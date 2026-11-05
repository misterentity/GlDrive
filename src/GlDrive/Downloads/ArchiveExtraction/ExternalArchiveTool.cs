using System.IO;
using Serilog;

namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// The fallback used when SharpCompress cannot read an archive. Seam for tests: the default
/// shells out to an installed UnRAR/7-Zip, a fake records the call.
/// </summary>
internal interface IExternalArchiveTool
{
    /// <summary>
    /// Extract <paramref name="archivePath"/> into <paramref name="outputDir"/>. Returns true on
    /// success, false if no tool was found (caller should then try the next fallback). Throws
    /// IOException on a non-zero exit code or timeout.
    /// </summary>
    bool TryExtract(string archivePath, string outputDir, string password, int overwriteMode,
        IProgress<ArchiveProgress> progress, CancellationToken ct);
}

internal sealed class ExternalArchiveTool : IExternalArchiveTool
{
    public static ExternalArchiveTool Default { get; } = new();

    // Common install paths for WinRAR's UnRAR and 7-Zip. Checked in order.
    private static readonly string[] ExternalToolCandidates =
    {
        @"C:\Program Files\WinRAR\UnRAR.exe",
        @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
        @"C:\Program Files\WinRAR\Rar.exe",
        @"C:\Program Files (x86)\WinRAR\Rar.exe",
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
    };

    private static string? FindExternalTool()
    {
        foreach (var p in ExternalToolCandidates)
            if (File.Exists(p)) return p;
        // Last-chance: PATH lookup for unrar / 7z
        foreach (var name in new[] { "unrar.exe", "UnRAR.exe", "7z.exe" })
        {
            var path = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, name))
                .FirstOrDefault(File.Exists);
            if (path != null) return path;
        }
        return null;
    }

    /// <summary>
    /// Shell out to an external UnRAR or 7-Zip binary to extract the archive.
    /// Returns true on success, false if no tool was found (caller should then try
    /// the next fallback). Throws IOException on non-zero exit code.
    /// </summary>
    public bool TryExtract(string archivePath, string outputDir, string password, int overwriteMode,
        IProgress<ArchiveProgress> progress, CancellationToken ct)
    {
        var tool = FindExternalTool();
        if (tool == null) return false;

        var fileName = Path.GetFileName(archivePath);
        Directory.CreateDirectory(outputDir);
        using var staging = new ExtractionStagingDirectory(outputDir);

        var isUnrar = Path.GetFileName(tool).StartsWith("unrar", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(tool).StartsWith("rar.exe", StringComparison.OrdinalIgnoreCase);
        var isSevenZip = Path.GetFileName(tool).StartsWith("7z", StringComparison.OrdinalIgnoreCase);

        // Pipe-deadlock avoidance: do NOT redirect stdout or stderr. The earlier
        // v1.44.64 approach used BeginOutputReadLine/BeginErrorReadLine to drain
        // asynchronously, but UnRAR's output on corrupted RAR sets (one CRC error
        // line per volume × 80+ volumes) still managed to stall, and the cost of
        // losing the exact error text in the log is lower than the cost of an
        // occasional deadlock.
        //
        // Instead, silence the tool entirely with -inul (UnRAR) / combined flags
        // (7-Zip), and rely on the exit code to determine success or failure.
        // stdin IS redirected so we can close it immediately, preventing the tool
        // from blocking on any read-from-stdin that isn't covered by -y.
        //
        // CreateNoWindow + UseShellExecute=false means the tool runs detached with
        // its own hidden console; unredirected writes go to that console and are
        // discarded by the OS.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = staging.Path,
        };

        if (isUnrar)
        {
            // UnRAR syntax:
            //   UnRAR x -y -o+ -inul [-p-] [-p<password>] <archive> <destDir>\
            psi.ArgumentList.Add("x");                     // extract with full paths
            psi.ArgumentList.Add("-y");                    // yes to all prompts
            if (overwriteMode == 0) psi.ArgumentList.Add("-o+");      // overwrite
            else if (overwriteMode == 1) psi.ArgumentList.Add("-o-"); // skip existing
            else psi.ArgumentList.Add("-or");                         // rename auto
            psi.ArgumentList.Add("-inul");                 // disable ALL output
            if (string.IsNullOrEmpty(password))
                psi.ArgumentList.Add("-p-");               // prevent password prompt
            else
                psi.ArgumentList.Add($"-p{password}");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add(staging.Path + Path.DirectorySeparatorChar);
        }
        else if (isSevenZip)
        {
            // 7z syntax:
            //   7z x -y -aoa [-p<password>] -bso0 -bse0 -bsp0 -o<destDir> <archive>
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add("-y");
            if (overwriteMode == 0) psi.ArgumentList.Add("-aoa");     // overwrite all
            else if (overwriteMode == 1) psi.ArgumentList.Add("-aos"); // skip existing
            else psi.ArgumentList.Add("-aou");                         // rename auto
            psi.ArgumentList.Add("-bso0");                 // stdout: no messages
            psi.ArgumentList.Add("-bse0");                 // stderr: no messages
            psi.ArgumentList.Add("-bsp0");                 // progress: no output
            if (!string.IsNullOrEmpty(password)) psi.ArgumentList.Add($"-p{password}");
            psi.ArgumentList.Add($"-o{staging.Path}");
            psi.ArgumentList.Add(archivePath);
        }
        else
        {
            Log.Warning("Extractor: unknown external tool shape: {Tool}", tool);
            return false;
        }

        Log.Information("Extractor: using external tool {Tool} for {File}", Path.GetFileName(tool), fileName);

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return false;

        // Close stdin immediately so the child can't block reading from it.
        // UnRAR's -y covers most interactive prompts, but some edge cases (like
        // file-in-use warnings) would otherwise read from stdin and block forever.
        try { proc.StandardInput.Close(); } catch { }

        // Show indeterminate progress while the external tool runs.
        var spinCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var pct = 0.0;
            while (!spinCts.IsCancellationRequested)
            {
                pct = (pct + 2) % 95;
                progress.Report(new ArchiveProgress(Percent: pct));
                try { await Task.Delay(500, spinCts.Token); } catch { break; }
            }
        }, spinCts.Token);

        // Hard timeout so a genuinely stuck tool can't hold up extraction forever.
        // 20 minutes covers extraction of very large multi-volume sets on slow disks,
        // while failing fast enough to unblock the queue when UnRAR can't cleanly exit
        // — observed in the wild on corrupt RAR sets where UnRAR extracts the data
        // correctly but then hangs in its finalize-with-CRC-errors code path.
        var startedAt = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(20);

        try
        {
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new OperationCanceledException(ct);
                }
                if (DateTime.UtcNow - startedAt > timeout)
                {
                    Log.Warning("Extractor: {Tool} timed out after {Minutes} min for {File}, killing",
                        Path.GetFileName(tool), timeout.TotalMinutes, fileName);
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new IOException($"{Path.GetFileName(tool)} timed out after {timeout.TotalMinutes} minutes");
                }
                proc.WaitForExit(500);
            }
        }
        finally
        {
            spinCts.Cancel();
            spinCts.Dispose();
        }

        if (proc.ExitCode != 0)
        {
            // UnRAR exit codes: 0=ok, 1=non-fatal warning, 2=fatal, 3=CRC error,
            // 4=locked, 5=write error, 6=open, 7=usage, 8=memory, 9=create, 10=no
            // files, 11=wrong password, 255=user stop. Any non-zero result is
            // incomplete and must retain the source archive for a later retry.
            var interpretedExit = proc.ExitCode;
            throw new IOException($"{Path.GetFileName(tool)} failed (exit {interpretedExit})");
        }

        CommitStagedOutput(staging.Path, outputDir, overwriteMode, ct);
        progress.Report(new ArchiveProgress(Percent: 100));
        Log.Information("Extractor: external tool completed for {File} (exit {Code})",
            fileName, proc.ExitCode);
        return true;
    }

    /// <summary>
    /// Move an external tool's output from its private staging directory into the real
    /// output folder, applying the overwrite mode and refusing any path that escapes it.
    /// The tool never writes into the output folder directly, so a failed run leaves nothing.
    /// </summary>
    internal static void CommitStagedOutput(string stagingDir, string outputDir,
        int overwriteMode, CancellationToken ct)
    {
        var safeOutput = Path.GetFullPath(outputDir);
        if (!safeOutput.EndsWith(Path.DirectorySeparatorChar)) safeOutput += Path.DirectorySeparatorChar;

        foreach (var stagedFile in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stagingDir, stagedFile);
            var destination = Path.GetFullPath(Path.Combine(safeOutput, relative));
            if (!destination.StartsWith(safeOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"External extractor produced an unsafe path: {relative}");

            if (File.Exists(destination))
            {
                if (overwriteMode == 1) continue;
                if (overwriteMode == 2) destination = ArchiveVolumes.GetUniqueFileName(destination);
            }

            var parent = Path.GetDirectoryName(destination);
            if (parent != null) Directory.CreateDirectory(parent);
            File.Move(stagedFile, destination, overwrite: overwriteMode == 0);
        }
    }

    internal sealed class ExtractionStagingDirectory : IDisposable
    {
        public string Path { get; }

        public ExtractionStagingDirectory(string outputDir)
        {
            Path = System.IO.Path.Combine(outputDir, $".gldrive-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            try { File.SetAttributes(Path, File.GetAttributes(Path) | FileAttributes.Hidden); } catch { }
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "Failed to remove extraction staging directory {Path}", Path); }
        }
    }
}
