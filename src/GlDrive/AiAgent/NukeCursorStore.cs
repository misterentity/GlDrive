using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed class NukeCursorStore
{
    private readonly string _path;
    private Dictionary<string, DateTime> _cursors = new();
    private readonly object _lock = new();

    public NukeCursorStore(string aiDataRoot)
    {
        _path = Path.Combine(aiDataRoot, "nuke-cursors.json");
        Load();
    }

    public DateTime Get(string serverId)
    {
        lock (_lock)
            return _cursors.TryGetValue(serverId, out var v) ? v : DateTime.MinValue;
    }

    public void Set(string serverId, DateTime cursor)
    {
        lock (_lock)
        {
            _cursors[serverId] = cursor;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _cursors = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path))
                           ?? new();
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "NukeCursorStore load failed; starting fresh"); _cursors = new(); }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_cursors)); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "NukeCursorStore save failed"); }
    }
}
