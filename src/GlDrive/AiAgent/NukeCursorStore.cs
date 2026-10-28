using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace GlDrive.AiAgent;

public sealed class NukeCursorStore
{
    private readonly string _path;
    private Dictionary<string, DateTime> _cursors = new();
    private readonly object _lock = new();
    private bool _loadFailed;
    private readonly string _relativePath;
    // Insertion order is kept so the oldest rows can be dropped: they have long left every
    // site's recent-nukes table. Refusing new rows at the cap stopped nuke telemetry for good.
    private readonly int _maxRelativeRows;
    private List<string> _relativeOrder = [];
    private HashSet<string> _relativeRows = new(StringComparer.Ordinal);

    public NukeCursorStore(string aiDataRoot) : this(aiDataRoot, 10000) { }

    internal NukeCursorStore(string aiDataRoot, int maxRelativeRows)
    {
        _maxRelativeRows = Math.Max(10, maxRelativeRows);
        _path = Path.Combine(aiDataRoot, "nuke-cursors.json");
        _relativePath = Path.Combine(aiDataRoot, "nuke-relative-rows.json");
        Load();
        try
        {
            if (File.Exists(_relativePath))
            {
                var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_relativePath));
                if (values == null || values.Any(value => value is not { Length: 64 } || !value.All(Uri.IsHexDigit)))
                    throw new JsonException("Invalid relative nuke cursor entries");
                _relativeOrder = values.Distinct(StringComparer.Ordinal).TakeLast(_maxRelativeRows).ToList();
                _relativeRows = _relativeOrder.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            // De-duplication only: starting empty may record a few rows twice, never none.
            Serilog.Log.Warning(ex, "Relative nuke cursors unreadable; quarantined them and starting empty");
            try { File.Copy(_relativePath, _relativePath + ".corrupt", true); } catch { }
        }
    }

    /// <summary>Age-only rows have no stable timestamp. Their identity deliberately excludes the changing age.</summary>
    public bool TryRecordRelative(string serverId, ParsedNuke nuke)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            serverId, nuke.Release, nuke.Nuker, nuke.Nukee ?? "", nuke.Multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture), nuke.Reason
        }))));
        lock (_lock)
        {
            if (_relativeRows.Contains(key)) return false;
            var order = new List<string>(_relativeOrder) { key };
            if (order.Count > _maxRelativeRows) order.RemoveRange(0, order.Count - _maxRelativeRows + _maxRelativeRows / 10);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_relativePath)!);
                GlDrive.Util.SecureFile.WriteAllTextRestricted(_relativePath, JsonSerializer.Serialize(order));
                _relativeOrder = order;
                _relativeRows = order.ToHashSet(StringComparer.Ordinal);
                return true;
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Relative nuke cursor save failed; cursor not advanced"); return false; }
        }
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
