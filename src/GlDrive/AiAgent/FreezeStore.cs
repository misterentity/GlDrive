using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed record FreezeEntry(string Path, string FrozenAt, string? Note);

public sealed class FreezeStore
{
    private readonly string _path;
    private List<FreezeEntry> _entries = new();
    private readonly object _lock = new();
    private bool _loadFailed;
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
            return _loadFailed || _entries.Any(e => JsonPointer.IsAncestorOrSelf(e.Path, pointer));
    }

    public void Freeze(string pointer, string? note = null)
    {
        bool changed;
        lock (_lock)
        {
            if (_loadFailed) throw new IOException("Freeze settings could not be read; restore frozen.json before editing restrictions.");
            if (_entries.Any(e => e.Path == pointer)) return;
            var next = _entries.Append(new FreezeEntry(pointer, DateTime.UtcNow.ToString("O"), note)).ToList();
            Save(next);
            _entries = next;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void Unfreeze(string pointer)
    {
        bool changed;
        lock (_lock)
        {
            if (_loadFailed) throw new IOException("Freeze settings could not be read; restore frozen.json before editing restrictions.");
            var next = _entries.Where(e => e.Path != pointer).ToList();
            changed = next.Count != _entries.Count;
            if (changed) { Save(next); _entries = next; }
        }
        if (changed) Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            _entries = JsonSerializer.Deserialize<List<FreezeEntry>>(File.ReadAllText(_path))
                ?? throw new JsonException("Missing freeze entries");
            if (_entries.Any(e => e == null || e.Path == null)) throw new JsonException("Invalid freeze entry");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FreezeStore load failed; freezing all AI changes until restored");
            _loadFailed = true;
            _entries = new();
        }
    }

    private void Save(List<FreezeEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        GlDrive.Util.SecureFile.WriteAllTextRestricted(_path,
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
