using System.IO;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class SnapshotStore
{
    private readonly string _dir;
    private readonly int _retain;

    public SnapshotStore(string aiDataRoot, int retentionCount)
    {
        _dir = Path.Combine(aiDataRoot, "ai-snapshots");
        _retain = Math.Max(1, retentionCount);
        Directory.CreateDirectory(_dir);
    }

    public string Save(string appsettingsPath, string runId)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var shortId = runId.Length > 8 ? runId[..8] : runId;
        var dest = Path.Combine(_dir, $"{stamp}-{shortId}.json");
        File.Copy(appsettingsPath, dest, overwrite: true);
        Prune();
        return dest;
    }

    public IEnumerable<string> List() => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f)
        : Enumerable.Empty<string>();

    /// <summary>Copies snapshot over appsettings.json. Saves a pre-restore snapshot first for safety.</summary>
    public void Restore(string snapshotPath, string appsettingsPath)
    {
        var preRestore = Path.Combine(_dir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try { File.Copy(appsettingsPath, preRestore, overwrite: true); }
        catch (Exception ex) { Log.Warning(ex, "pre-restore snapshot save failed"); }
        File.Copy(snapshotPath, appsettingsPath, overwrite: true);
    }

    private void Prune()
    {
        try
        {
            var files = Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f).ToList();
            foreach (var old in files.Skip(_retain))
            {
                try { File.Delete(old); }
                catch (Exception ex) { Log.Debug(ex, "snapshot prune delete failed {Path}", old); }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "SnapshotStore prune failed"); }
    }
}
