using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class ExtractAbandonRestartRevivalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-extract-revival-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ExtractorWindow _window;
    private readonly ExtractAbandonStore _persisted;
    private readonly TransientAbandonLedger _live = new();
    private readonly Dictionary<string, int> _retries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _archive;

    public ExtractAbandonRestartRevivalTests()
    {
        Directory.CreateDirectory(_root);
        _archive = Path.Combine(_root, "release.rar");
        File.WriteAllBytes(_archive, new byte[100]);
        var storePath = Path.Combine(_root, "abandoned.json");
        new ExtractAbandonStore(storePath).Record(_archive, 1, 100, "missing volume");
        _persisted = new ExtractAbandonStore(storePath);
        _persisted.Load(); // Model the next process, with an empty in-memory ledger.

        // Exercise the production startup gate and watcher handler without constructing a
        // real window (which loads user settings and starts watchers). Cancellation stops
        // only the later readiness wait, after the real requeue/duplicate gates have run.
        _window = (ExtractorWindow)RuntimeHelpers.GetUninitializedObject(typeof(ExtractorWindow));
        Set("_watchLock", new object());
        Set("_watchProcessed", _processed);
        Set("_watchRetryCounts", _retries);
        Set("_watchAbandoned", _live);
        Set("_abandonStore", _persisted);
        Set("_managedDownloadParked", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Set("_downloadGuard", new DownloadExtractionGuard(_root, () => []));
        Set("_lifetimeCts", _lifetime);
        _lifetime.Cancel();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persisted_startup_skip_revives_through_watcher_when_a_part_arrives_or_grows(bool newPart)
    {
        Assert.True(SkipPersisted());
        Assert.Contains(_archive, _processed);
        _retries[_archive] = 5;
        var eventPath = newPart ? Path.ChangeExtension(_archive, ".r00") : _archive;
        File.WriteAllBytes(eventPath, new byte[200]);

        await HandleWatched(eventPath);

        // The broken path removed its durable record but returned at _watchProcessed.Add,
        // leaving its old retry state and never entering readiness for the changed set.
        Assert.False(_retries.ContainsKey(_archive), "A changed persisted archive must clear the old watch gate and retry count.");
        Assert.DoesNotContain(_archive, _live.AbandonedPaths());
        Assert.False(_persisted.ShouldSkip(_archive, newPart ? 2 : 1, newPart ? 300 : 200));
        Assert.Contains(_archive, _processed); // Exactly one fresh attempt now owns the path.
    }

    [Fact]
    public async Task Persisted_startup_skip_is_registered_for_sweep_but_unchanged_or_unreadable_sets_stay_parked()
    {
        Assert.True(SkipPersisted());
        Assert.Contains(_archive, _live.AbandonedPaths());
        Assert.Equal(TransientAbandonLedger.AbandonState.Unknown, _live.Evaluate(_archive, -1, -1));
        Assert.Contains(_archive, _live.AbandonedPaths());
        Assert.Equal(TransientAbandonLedger.AbandonState.StillAbandoned, _live.Evaluate(_archive, 1, 100));
        _retries[_archive] = 5;

        await HandleWatched(_archive);

        Assert.Equal(5, _retries[_archive]);
        Assert.True(_persisted.ShouldSkip(_archive, 1, 100));
        Assert.Contains(_archive, _live.AbandonedPaths());
    }

    private bool SkipPersisted() => (bool)typeof(ExtractorWindow)
        .GetMethod("SkipIfAbandoned", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, [_archive])!;
    private Task HandleWatched(string path) => (Task)typeof(ExtractorWindow)
        .GetMethod("HandleWatchedFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, [path])!;
    private void Set(string name, object value) => typeof(ExtractorWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

    public void Dispose()
    {
        _lifetime.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
