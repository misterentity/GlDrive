using System.IO;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// Extracts ONE archive (or volume set) into one output folder: SharpCompress archive mode,
/// then the external UnRAR/7-Zip fallback, then the streaming reader. Knows nothing about
/// queues, watch folders or retries — <see cref="ArchiveExtractionEngine"/> owns those.
/// </summary>
internal sealed class ArchiveUnpacker(IExternalArchiveTool externalTool)
{
    /// <summary>
    /// Extract archive on a dedicated BelowNormal priority thread to avoid UI lag.
    /// Progress is reported through <paramref name="progress"/>, throttled to max 10 updates/sec.
    /// </summary>
    internal Task ExtractArchiveAsync(string archivePath, string outputDir, string password, int overwriteMode,
        IProgress<ArchiveProgress> progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ExtractArchive(archivePath, outputDir, password, overwriteMode, progress, ct);
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = $"Extract-{Path.GetFileName(archivePath)}"
        };
        thread.Start();
        return tcs.Task;
    }

    internal void ExtractArchive(string archivePath, string outputDir, string password, int overwriteMode,
        IProgress<ArchiveProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var fileName = Path.GetFileName(archivePath);

        var readerOptions = new ReaderOptions
        {
            Password = string.IsNullOrEmpty(password) ? null : password
        };

        var lastProgressUpdate = DateTime.MinValue;

        // Try archive-based extraction first (supports random access, multi-volume).
        //
        // For old-style scene multi-volume RARs (.rar + .r00 + .r01 + ... + .r99 [+ .s00...])
        // SharpCompress's single-path auto-detection can fail with
        // "Unknown Rar Header: <byte>" when it can't correctly walk to the next volume.
        // Explicitly enumerating the volumes in the same directory and passing the
        // full list to ArchiveFactory.OpenArchive(IReadOnlyList<FileInfo>, ...) bypasses
        // SharpCompress's discovery entirely and reads the volumes as given.
        try
        {
            IArchive archive;
            var volumes = ArchiveVolumes.TryDiscoverRarVolumes(archivePath);
            if (volumes != null && volumes.Count > 1)
            {
                Log.Debug(
                    "Extractor: opening {Count}-volume RAR set starting at {First}",
                    volumes.Count, fileName);
                archive = ArchiveFactory.OpenArchive(volumes, readerOptions);
            }
            else
            {
                archive = ArchiveFactory.OpenArchive(archivePath, readerOptions);
            }

            using var _ = archive;

            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            progress.Report(new ArchiveProgress(EntryCount: entries.Count));

            var safeDirPath = Path.GetFullPath(outputDir);
            if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
                safeDirPath += Path.DirectorySeparatorChar;
            var processed = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Key == null) continue;

                // Path traversal protection
                var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Skipping path traversal entry: {Key}", entry.Key);
                    continue;
                }

                // Archives are allowed through the torrent content gate — they must be, it is
                // how releases ship — so this is where their contents actually become files.
                // The watch folders are the same folders torrents save to.
                if (ExecutableExtensions.IsExecutable(entry.Key))
                {
                    Log.Warning("Skipping executable archive entry: {Key}", entry.Key);
                    continue;
                }

                // Overwrite handling
                if (File.Exists(fullPath))
                {
                    switch (overwriteMode)
                    {
                        case 1: // Skip
                            processed++;
                            continue;
                        case 2: // Rename
                            fullPath = ArchiveVolumes.GetUniqueFileName(fullPath);
                            break;
                        // case 0: Overwrite — just extract normally
                    }
                }

                using (var entryStream = entry.OpenEntryStream())
                    ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct);

                processed++;

                // Throttle progress updates to max ~10/sec to avoid flooding the UI
                var now = DateTime.UtcNow;
                if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
                {
                    lastProgressUpdate = now;
                    var pct = entries.Count > 0 ? (double)processed / entries.Count * 100 : 100;
                    progress.Report(new ArchiveProgress(Percent: pct));
                }
            }

            progress.Report(new ArchiveProgress(Percent: 100));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidFormatException
                                      or SharpCompress.Common.MultiVolumeExtractionException)
        {
            // SharpCompress has known issues with scene-style multi-volume RAR sets
            // (.rar + .r00 + .r01 + ...). Both archive-mode and streaming-reader mode
            // fail for these — the streaming reader throws MultiVolumeExtractionException
            // explicitly, and archive-mode throws "Unknown Rar Header" garbage.
            //
            // When that happens, fall back to the external UnRAR.exe or 7z.exe binary
            // if one is installed on the system. WinRAR is the canonical reference
            // implementation and handles every edge case; 7-Zip covers most RAR5 files.
            Log.Warning(
                "Extractor: SharpCompress archive-mode failed ({Msg}), trying external tool: {File}",
                ex.Message, fileName);

            if (!externalTool.TryExtract(archivePath, outputDir, password, overwriteMode, progress, ct))
            {
                // No external tool available — fall through to the streaming reader as
                // a last-ditch attempt. Will still fail for multi-volume sets, but might
                // work for certain single-file archive formats.
                Log.Warning("Extractor: no external unrar/7z found, falling back to streaming reader");
                using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);
                ExtractWithReader(fileStream, outputDir, readerOptions, overwriteMode, progress, ct);
            }
        }
    }

    private static void ExtractWithReader(Stream stream, string outputDir,
        ReaderOptions options, int overwriteMode, IProgress<ArchiveProgress> progress, CancellationToken ct)
    {
        var safeDirPath = Path.GetFullPath(outputDir);
        if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
            safeDirPath += Path.DirectorySeparatorChar;
        var processed = 0;
        var lastProgressUpdate = DateTime.MinValue;

        using var reader = ReaderFactory.OpenReader(stream, options);
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.Entry.IsDirectory) continue;
            var key = reader.Entry.Key;
            if (key == null) continue;

            var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, key));
            if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Second independent extraction loop (zip/7z/tar). Guarding only the first one
            // would be the same "invariant enforced at one call site" mistake this codebase
            // has already shipped three times.
            if (ExecutableExtensions.IsExecutable(key))
            {
                Log.Warning("Skipping executable archive entry: {Key}", key);
                continue;
            }

            if (File.Exists(fullPath))
            {
                switch (overwriteMode)
                {
                    case 1: processed++; continue;
                    case 2: fullPath = ArchiveVolumes.GetUniqueFileName(fullPath); break;
                }
            }

            using var entryStream = reader.OpenEntryStream();
            ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct);

            processed++;
            var now = DateTime.UtcNow;
            if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
            {
                lastProgressUpdate = now;
                // Indeterminate for streaming
                progress.Report(new ArchiveProgress(Percent: -1, EntryCount: processed));
            }
        }

        progress.Report(new ArchiveProgress(Percent: 100));
    }

    /// <summary>
    /// Check if every file entry exists in the output directory at its expected size.
    /// Partial output must never suppress a later retry. Throws when the archive cannot be
    /// opened; the caller decides what an unverifiable archive means.
    /// </summary>
    internal static bool AllEntriesPresent(string archivePath, string outputDir)
    {
        if (!Directory.Exists(outputDir)) return false;

        var options = new ReaderOptions();
        IArchive archive;
        var volumes = ArchiveVolumes.TryDiscoverRarVolumes(archivePath);
        if (volumes != null && volumes.Count > 1)
            archive = ArchiveFactory.OpenArchive(volumes, options);
        else
            archive = ArchiveFactory.OpenArchive(archivePath, options);
        using var _ = archive;

        var entries = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
        if (entries.Count == 0) return false;
        var safeDir = Path.GetFullPath(outputDir);
        if (!safeDir.EndsWith(Path.DirectorySeparatorChar)) safeDir += Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            var outPath = Path.GetFullPath(Path.Combine(safeDir, entry.Key!));
            if (!outPath.StartsWith(safeDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(outPath))
                return false;
            if (entry.Size >= 0 && new FileInfo(outPath).Length != entry.Size)
                return false;
        }
        return true;
    }
}
