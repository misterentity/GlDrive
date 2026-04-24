using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed record FreezeEntry(string Path, string FrozenAt, string? Note);

public sealed class FreezeStore
{
    private readonly string _path;
    private List<FreezeEntry> _entries = new();
    private readonly object _lock = new();
    public event Action? Changed;

    public FreezeStore(string aiDataRoot)
    {
        _path = System.IO.Path.Combine(aiDataRoot, "frozen.json");
        Load();
    }

    public IReadOnlyList<FreezeEntry> All
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public bool IsFrozen(string pointer)
    {
        lock (_lock)
            return _entries.Any(e => JsonPointer.IsAncestorOrSelf(e.Path, pointer));
    }

    public void Freeze(string pointer, string? note = null)
    {
        bool changed;
        lock (_lock)
        {
            if (_entries.Any(e => e.Path == pointer)) return;
            _entries.Add(new FreezeEntry(pointer, DateTime.UtcNow.ToString("O"), note));
            Save();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void Unfreeze(string pointer)
    {
        bool changed;
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => e.Path == pointer);
            changed = removed > 0;
            if (changed) Save();
        }
        if (changed) Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<FreezeEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FreezeStore load failed; treating as empty");
            _entries = new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FreezeStore save failed");
        }
    }
}
