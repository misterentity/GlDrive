using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.Player;

public class PlayerResumeStore
{
    private readonly string _filePath;
    private Dictionary<string, double> _positions = new();
    private readonly object _gate = new();
    private bool _loadFailed;

    public PlayerResumeStore(string libraryPath)
    {
        _filePath = Path.Combine(libraryPath, "resume.json");
        Load();
    }

    public double GetPosition(string releaseName)
    {
        lock (_gate) return _positions.GetValueOrDefault(releaseName, 0);
    }

    public void SavePosition(string releaseName, double positionPercent)
    {
        if (!double.IsFinite(positionPercent)) return;
        lock (_gate)
        {
            if (_loadFailed) return;
            var next = new Dictionary<string, double>(_positions);
            if (positionPercent < 2 || positionPercent > 98) next.Remove(releaseName);
            else next[releaseName] = positionPercent;
            if (next.Count == _positions.Count && next.GetValueOrDefault(releaseName) == _positions.GetValueOrDefault(releaseName)) return;
            Persist(next);
        }
    }

    public void ClearPosition(string releaseName) => SavePosition(releaseName, 0);

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _positions = JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? throw new JsonException("Missing positions");
            }
        }
        catch (Exception ex) { _loadFailed = true; Log.Warning(ex, "Failed to load resume store; preserving unreadable file"); }
    }

    private void Persist(Dictionary<string, double> next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            GlDrive.Util.SecureFile.WriteAllTextRestricted(_filePath, JsonSerializer.Serialize(next));
            _positions = next;
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to save resume store"); }
    }
}
