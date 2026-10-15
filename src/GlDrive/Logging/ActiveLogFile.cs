using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GlDrive.Logging;

/// <summary>
/// Locates the file Serilog is writing for a given day.
///
/// v3.11.9. The crash dumper read <c>gldrive-{date}.log</c> by name with
/// <c>File.ReadAllLines</c>. Two faults: that call demands exclusive write access while the
/// live sink holds the file open, so 26 of 30 dumps on disk say "failed to read today's
/// log: ... being used by another process"; and on a busy day <c>rollOnFileSizeLimit</c>
/// moves the sink to <c>gldrive-{date}_001.log</c>, <c>_002</c>, ..., so even a readable base
/// file is hours stale. The live file is the highest roll sequence — not the newest write
/// time, because a rejected second launch appends its audit line to the base name.
/// </summary>
internal static class ActiveLogFile
{
    internal static string? Find(string logsDir, DateTime day)
    {
        if (!Directory.Exists(logsDir)) return null;
        var stem = $"gldrive-{day:yyyyMMdd}";
        return Directory.EnumerateFiles(logsDir, stem + "*.log")
            .Select(path => (path, seq: Sequence(Path.GetFileNameWithoutExtension(path), stem)))
            .Where(f => f.seq >= 0)
            .OrderByDescending(f => f.seq)
            .Select(f => f.path)
            .FirstOrDefault();
    }

    /// <summary>The live file, or the base name when the day has no log yet.</summary>
    internal static string PathFor(string logsDir, DateTime day)
        => Find(logsDir, day) ?? Path.Combine(logsDir, $"gldrive-{day:yyyyMMdd}.log");

    /// <summary>
    /// Appends one pre-formatted line beside the live sink. The watchdog and the pre-Serilog
    /// startup path wrote to the BASE name resolved once: after a size roll their lines went
    /// into a stale file out of time order (2026-10-13: WATCHDOG relaunches at 20:32-22:06
    /// appended to a file whose sink had moved to _002 at 19:54), and the watchdog's path
    /// never advanced past the day it started. Resolve per write, and share like the sink.
    /// </summary>
    internal static void AppendLine(string logsDir, string line)
    {
        using var stream = new FileStream(PathFor(logsDir, DateTime.Now), FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }

    private static int Sequence(string name, string stem)
    {
        if (name == stem) return 0;
        if (name.Length > stem.Length + 1 && name.StartsWith(stem + "_", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(stem.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            return seq;
        return -1;
    }
}
