using System.IO;
using GlDrive.Util;

namespace GlDrive.Services;

/// <summary>Uses the failure-ledger format understood by previous releases.</summary>
internal static class UpdateRollbackGuard
{
    internal static bool Prepare(string appDataRoot, Version rejected, out string message,
        Action<string, string>? write = null)
    {
        try
        {
            Directory.CreateDirectory(appDataRoot);
            var path = Path.Combine(appDataRoot, ".update-failures");
            var aliases = Aliases(rejected);
            var attempt = Path.Combine(appDataRoot, ".update-attempt");
            if (File.Exists(attempt))
            {
                if (new FileInfo(attempt).Length > 4096) throw new IOException("Update attempt marker exceeds its safe read bound");
                var tag = File.ReadAllText(attempt).Trim();
                if (Version.TryParse(tag.TrimStart('v', 'V'), out var version) &&
                    UpdateChecker.Normalize(version) == UpdateChecker.Normalize(rejected)) aliases.Add(tag);
            }
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                throw new IOException("Update failure ledger exceeds its safe read bound");
            var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            var preserved = lines.Where(line => !aliases.Contains(line.Split('|', 2)[0])).ToList();
            preserved.AddRange(aliases.Select(tag => tag + "|3"));
            (write ?? SecureFile.WriteAllTextRestricted)(path, string.Join(Environment.NewLine, preserved) + Environment.NewLine);
            if (!ContainsBlocks(path, rejected)) throw new IOException("Rejected-version marker did not verify");
            // Older builds clear the whole ledger when a satisfied attempt remains. Consume
            // it before shutdown, and have the elevated child read back both conditions.
            File.Delete(attempt);
            if (!IsPrepared(appDataRoot, rejected)) throw new IOException("Update attempt marker could not be consumed");
            message = "The rejected version is blocked from automatic reinstall.";
            return true;
        }
        catch (Exception error)
        {
            message = "Rollback was not started because its automatic-reinstall guard could not be saved: " + error.Message;
            return false;
        }
    }

    internal static bool IsPrepared(string appDataRoot, Version rejected)
    {
        try
        {
            var attempt = Path.Combine(appDataRoot, ".update-attempt");
            return !File.Exists(attempt) && !Directory.Exists(attempt) &&
                ContainsBlocks(Path.Combine(appDataRoot, ".update-failures"), rejected);
        }
        catch { return false; }
    }

    private static bool ContainsBlocks(string path, Version rejected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return false;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var pieces = line.Split('|', 2);
            if (pieces.Length == 2 && int.TryParse(pieces[1], out var count)) counts[pieces[0]] = count;
        }
        return Aliases(rejected).All(tag => counts.GetValueOrDefault(tag) >= 3);
    }

    private static HashSet<string> Aliases(Version version)
    {
        var normalized = UpdateChecker.Normalize(version);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in new[] { normalized.ToString(), new Version(normalized.Major, normalized.Minor, normalized.Build, 0).ToString() })
        {
            result.Add(text);
            result.Add("v" + text);
        }
        if (normalized.Build == 0)
        {
            result.Add($"{normalized.Major}.{normalized.Minor}");
            result.Add($"v{normalized.Major}.{normalized.Minor}");
        }
        return result;
    }
}
