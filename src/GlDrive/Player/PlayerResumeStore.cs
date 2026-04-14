using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.Player;

public class PlayerResumeStore
{
    private readonly string _filePath;
    private Dictionary<string, double> _positions = new();

    public PlayerResumeStore(string libraryPath)
    {
        _filePath = Path.Combine(libraryPath, "resume.json");
        Load();
    }

    public double GetPosition(string releaseName)
    {
        return _positions.GetValueOrDefault(releaseName, 0);
    }

    public void SavePosition(string releaseName, double positionPercent)
    {
        if (positionPercent < 2 || positionPercent > 98)
        {
            // Remove if at start or finished
            _positions.Remove(releaseName);
        }
        else
        {
            _positions[releaseName] = positionPercent;
        }
        Persist();
    }

    public void ClearPosition(string releaseName)
    {
        _positions.Remove(releaseName);
        Persist();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _positions = JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load resume store"); }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_positions));
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to save resume store"); }
    }
}
