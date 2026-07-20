using System.Collections.Concurrent;
using System.IO;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Irc;

/// <summary>
/// Persistent per-server IRC channel log. Writes plain text files at
/// %AppData%\GlDrive\irc-logs\{serverId}-{yyyy-MM-dd}.log with one line per
/// message. Used by AI Setup and IrcPatternDetector to survive restarts.
///
/// Line format: HH:mm:ss\t{channel}\t{nick}\t{text}
///
/// Only real channel messages (#channel target, Normal/Notice type) are logged.
/// PMs and system messages are skipped to keep the log focused on announces
/// and chat that matters for site config detection.
/// </summary>
public static class IrcLogStore
{
    private static readonly ConcurrentDictionary<string, object> _writeLocks = new();
    private const int RetentionDays = 30;
    private const int MaxLineLength = 2000;

    private static string LogFolder => Path.Combine(ConfigManager.AppDataPath, "irc-logs");

    private static string GetLogPath(string serverId, DateTime date) =>
        Path.Combine(LogFolder, $"{Sanitize(serverId)}-{date:yyyy-MM-dd}.log");

    /// <summary>
    /// Append a single IRC channel message to the server's log file. Safe to
    /// call from any thread. Silently drops lines if writing fails — must not
    /// break the IRC pipeline.
    /// </summary>
    public static void Append(string serverId, string channel, string nick, string text)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(channel)) return;
        if (!IsChannelName(channel)) return; // channel only, no PMs
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = GetLogPath(serverId, DateTime.Now);

            // Clean any control chars and clamp length
            var safeText = Sanitize(text);
            if (safeText.Length > MaxLineLength) safeText = safeText[..MaxLineLength];

            var line = $"{DateTime.Now:HH:mm:ss}\t{channel}\t{nick}\t{safeText}";

            var gate = _writeLocks.GetOrAdd(serverId, _ => new object());
            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IrcLogStore: append failed for {Server} on {Channel}", serverId, channel);
        }
    }

    /// <summary>
    /// Read the most recent <paramref name="maxLines"/> channel messages for a
    /// server, pulled from today and yesterday's files if needed. Returns them
    /// in chronological order (oldest first).
    /// </summary>
    public static List<string> ReadRecent(string serverId, int maxLines = 100)
    {
        var results = new List<string>();
        try
        {
            var today = GetLogPath(serverId, DateTime.Now);
            var yesterday = GetLogPath(serverId, DateTime.Now.AddDays(-1));

            if (File.Exists(yesterday))
                results.AddRange(SafeReadAllLines(yesterday));
            if (File.Exists(today))
                results.AddRange(SafeReadAllLines(today));

            if (results.Count > maxLines)
                results = results.GetRange(results.Count - maxLines, maxLines);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IrcLogStore: read failed for {Server}", serverId);
        }
        return results;
    }

    /// <summary>
    /// True for any IRC channel prefix (#, &amp;, !, +) — matches IrcService.IsChannel,
    /// not just '#', so e.g. &amp;-channels get logged too.
    /// </summary>
    public static bool IsChannelName(string target) =>
        target.Length > 0 && (target[0] == '#' || target[0] == '&' || target[0] == '!' || target[0] == '+');

    /// <summary>
    /// Like <see cref="ReadRecent"/> but parsed, with full timestamps reconstructed
    /// from each file's date. Malformed lines are skipped. Chronological order.
    /// Used to seed the in-memory scrollback at IrcService startup.
    /// </summary>
    public static List<(DateTime Timestamp, string Channel, string Nick, string Text)> ReadRecentEntries(
        string serverId, int maxLines = 100)
    {
        var results = new List<(DateTime, string, string, string)>();
        try
        {
            foreach (var date in new[] { DateTime.Now.AddDays(-1), DateTime.Now })
            {
                var path = GetLogPath(serverId, date);
                if (!File.Exists(path)) continue;
                foreach (var line in SafeReadAllLines(path))
                {
                    if (!TryParseLine(line, date.Date, out var entry)) continue;
                    results.Add(entry);
                }
            }
            if (results.Count > maxLines)
                results.RemoveRange(0, results.Count - maxLines);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IrcLogStore: parsed read failed for {Server}", serverId);
        }
        return results;
    }

    /// <summary>
    /// Parses one "HH:mm:ss\t#channel\tnick\ttext" log line against the file's date.
    /// </summary>
    public static bool TryParseLine(string line, DateTime fileDate,
        out (DateTime Timestamp, string Channel, string Nick, string Text) entry)
    {
        entry = default;
        var parts = line.Split('\t', 4);
        if (parts.Length < 4) return false;
        if (!TimeSpan.TryParseExact(parts[0], @"hh\:mm\:ss", null, out var time)) return false;
        var channel = parts[1];
        if (!IsChannelName(channel)) return false;
        entry = (fileDate.Date + time, channel, parts[2], parts[3]);
        return true;
    }

    /// <summary>
    /// Delete log files older than <see cref="RetentionDays"/> days. Called on
    /// app startup or lazily; must not throw on failure.
    /// </summary>
    public static void PruneOld()
    {
        try
        {
            if (!Directory.Exists(LogFolder)) return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogFolder, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IrcLogStore: prune failed");
        }
    }

    private static IEnumerable<string> SafeReadAllLines(string path)
    {
        // Use ReadAllLines with a retry on transient IO errors; we don't need
        // streaming because a day of IRC traffic is well under 10MB.
        try { return File.ReadAllLines(path); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '\t' || c == '\n' || c == '\r') sb.Append(' ');
            else if (c >= 32 || c == 0x03 /* mIRC color */) sb.Append(c);
        }
        return sb.ToString();
    }
}
