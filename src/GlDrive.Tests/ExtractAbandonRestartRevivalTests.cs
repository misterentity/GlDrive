using System.IO;
using System.Reflection;
using GlDrive.Downloads;
using GlDrive.Downloads.ArchiveExtraction;
using Xunit;

namespace GlDrive.Tests;

public sealed class ExtractAbandonRestartRevivalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-extract-revival-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveExtractionEngine _engine;
    private readonly ExtractAbandonStore _persisted;
    private readonly TransientAbandonLedger _live;
    private readonly Dictionary<string, int> _retries;
    private readonly HashSet<string> _processed;
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

        // Exercise the production startup gate and watcher handler on a real engine. Demo mode
        // keeps it off the user's saved settings and watchers; the persisted store and the
        // download guard are injected. Disposing stops only the later readiness wait, after
        // the real requeue/duplicate gates have run.
        _engine = new ArchiveExtractionEngine(null, new ExtractorEngineOptions
        {
            DemoMode = true,
            SettingsDirectory = _root,
            AbandonStore = _persisted,
            DownloadGuard = new DownloadExtractionGuard(_root, () => []),
        });
        _processed = Get<HashSet<string>>("_watchProcessed");
        _retries = Get<Dictionary<string, int>>("_watchRetryCounts");
        _live = Get<TransientAbandonLedger>("_watchAbandoned");
        _engine.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persisted_startup_skip_revives_through_watcher_when_a_part_arrives_or_grows(bool newPart)
    {
        Assert.True(_engine.SkipIfAbandoned(_archive));
        Assert.Contains(_archive, _processed);
        _retries[_archive] = 5;
        var eventPath = newPart ? Path.ChangeExtension(_archive, ".r00") : _archive;
        File.WriteAllBytes(eventPath, new byte[200]);

        await _engine.HandleWatchedFileAsync(eventPath);

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
        Assert.True(_engine.SkipIfAbandoned(_archive));
        Assert.Contains(_archive, _live.AbandonedPaths());
        Assert.Equal(TransientAbandonLedger.AbandonState.Unknown, _live.Evaluate(_archive, -1, -1));
        Assert.Contains(_archive, _live.AbandonedPaths());
        Assert.Equal(TransientAbandonLedger.AbandonState.StillAbandoned, _live.Evaluate(_archive, 1, 100));
        _retries[_archive] = 5;

        await _engine.HandleWatchedFileAsync(_archive);

        Assert.Equal(5, _retries[_archive]);
        Assert.True(_persisted.ShouldSkip(_archive, 1, 100));
        Assert.Contains(_archive, _live.AbandonedPaths());
    }

    private T Get<T>(string field) => (T)typeof(ArchiveExtractionEngine)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_engine)!;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
