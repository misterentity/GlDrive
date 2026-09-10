using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed class NukeCursorStore
{
    private readonly string _path;
    private Dictionary<string, DateTime> _cursors = new();
    private readonly object _lock = new();
    private bool _loadFailed;

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
            if (_loadFailed) return; // Preserve unreadable evidence instead of overwriting it.
            var next = new Dictionary<string, DateTime>(_cursors) { [serverId] = cursor };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                GlDrive.Util.SecureFile.WriteAllTextRestricted(_path, JsonSerializer.Serialize(next));
                _cursors = next;
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "NukeCursorStore save failed; cursor not advanced"); }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _cursors = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path))
                           ?? throw new JsonException("Missing cursor entries");
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "NukeCursorStore load failed; preserving unreadable file"); _loadFailed = true; }
    }
}
