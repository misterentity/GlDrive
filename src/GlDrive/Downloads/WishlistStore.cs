using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Downloads;

public class WishlistStore
{
    // Production instances share one list and ordered persistence gate. Isolated stores
    // use the same implementation without initializing AppData or touching user state.
    private static readonly Lazy<State> Shared = new(() =>
    {
        var path = Path.Combine(ConfigManager.AppDataPath, "wishlist.json");
        return new State(() => File.Exists(path) ? File.ReadAllText(path) : null, json =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SecureFile.WriteAllTextRestricted(path, json);
        });
    });
    private readonly State _state;

    public WishlistStore() => _state = Shared.Value;
    internal WishlistStore(Func<string?> read, Action<string> write) => _state = new State(read, write);

    private sealed class State(Func<string?> read, Action<string> write)
    {
        internal readonly Lock Sync = new();
        internal readonly Func<string?> Read = read;
        internal readonly Action<string> Write = write;
        internal List<WishlistItem> Items = [];
        internal readonly WishlistReleaseDeduplicator ReleaseEvaluations = new();
        internal bool Loaded;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IReadOnlyList<WishlistItem> Items
    {
        get { lock (_state.Sync) { EnsureLoaded(); return _state.Items.ToList(); } }
    }

    public void Load()
    {
        lock (_state.Sync)
        {
            if (_state.Loaded) return;

            try
            {
                var json = _state.Read();
                _state.Items = json == null ? [] : JsonSerializer.Deserialize<List<WishlistItem>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load wishlist, starting empty");
                _state.Items = [];
            }
            _state.Loaded = true;
        }
    }

    public void Save()
    {
        try
        {
            lock (_state.Sync)
            {
                EnsureLoaded();
                _state.Write(JsonSerializer.Serialize(_state.Items, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wishlist");
        }
    }

    public void Add(WishlistItem item)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            _state.Items.Add(item);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            _state.Items.RemoveAll(i => i.Id == id);
            _state.ReleaseEvaluations.ForgetItem(id);
            Save();
        }
    }

    public void Update(WishlistItem item)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var idx = _state.Items.FindIndex(i => i.Id == item.Id);
            if (idx >= 0)
                _state.Items[idx] = item;
            else
                return;
            _state.ReleaseEvaluations.ForgetItem(item.Id);
            Save();
        }
    }

    public WishlistItem? GetById(string id)
    {
        lock (_state.Sync) { EnsureLoaded(); return _state.Items.FirstOrDefault(i => i.Id == id); }
    }

    internal bool TryBeginReleaseEvaluation(string itemId, string release)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            return item is { Dead: false, Status: WishlistStatus.Watching }
                && !item.GrabbedReleases.Contains(release, StringComparer.OrdinalIgnoreCase)
                && _state.ReleaseEvaluations.TryBegin(itemId, release);
        }
    }

    internal void ForgetReleaseEvaluation(string itemId, string release)
    {
        lock (_state.Sync) _state.ReleaseEvaluations.Forget(itemId, release);
    }

    internal void CompleteReleaseEvaluation(string itemId, string release)
    {
        lock (_state.Sync) _state.ReleaseEvaluations.Complete(itemId, release);
    }

    internal void RecordGrab(string itemId, string release)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            _state.ReleaseEvaluations.Complete(itemId, release);
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || item.GrabbedReleases.Contains(release, StringComparer.OrdinalIgnoreCase)) return;
            item.GrabbedReleases.Add(release);
            Save();
        }
    }

    /// <summary>
    /// Imports unique entries with fresh IDs and one ordered secure save. The batch is
    /// published only after persistence succeeds; failures propagate to the import UI.
    /// </summary>
    public int AddImported(IEnumerable<WishlistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_state.Sync)
        {
            EnsureLoaded();
            var next = new List<WishlistItem>(_state.Items);
            var imdb = next.Where(i => !string.IsNullOrEmpty(i.ImdbId)).Select(i => i.ImdbId!).ToHashSet(StringComparer.Ordinal);
            var tvMaze = next.Where(i => i.TvMazeId.HasValue).Select(i => i.TvMazeId!.Value).ToHashSet();
            var titles = next.Select(i => (i.Title, i.Type)).ToHashSet();
            foreach (var item in items)
            {
                if (item == null) throw new InvalidDataException("Wishlist import contains an empty entry.");
                if ((!string.IsNullOrEmpty(item.ImdbId) && imdb.Contains(item.ImdbId)) ||
                    (item.TvMazeId.HasValue && tvMaze.Contains(item.TvMazeId.Value)) ||
                    titles.Contains((item.Title, item.Type))) continue;

                // Do not mutate imported objects (or their IDs) before a successful save.
                next.Add(new WishlistItem
                {
                    Id = Guid.NewGuid().ToString("N"), Type = item.Type, Title = item.Title,
                    Year = item.Year, ImdbId = item.ImdbId, TvMazeId = item.TvMazeId,
                    Quality = item.Quality, Status = item.Status, AddedAt = item.AddedAt,
                    GrabbedReleases = item.GrabbedReleases == null ? null! : new List<string>(item.GrabbedReleases),
                    PosterUrl = item.PosterUrl, Plot = item.Plot, Rating = item.Rating,
                    Genres = item.Genres, Dead = item.Dead
                });
                if (!string.IsNullOrEmpty(item.ImdbId)) imdb.Add(item.ImdbId);
                if (item.TvMazeId.HasValue) tvMaze.Add(item.TvMazeId.Value);
                titles.Add((item.Title, item.Type));
            }
            var added = next.Count - _state.Items.Count;
            if (added == 0) return 0;
            _state.Write(JsonSerializer.Serialize(next, JsonOptions));
            _state.Items = next;
            return added;
        }
    }

    private void EnsureLoaded()
    {
        if (!_state.Loaded) Load();
    }
}
