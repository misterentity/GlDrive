using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class GuardedJsonFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-guard-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_root, "state.json");
    public GuardedJsonFileTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static int Count(string json) => JsonSerializer.Deserialize<int[]>(json)?.Length ?? throw new JsonException();
    private GuardedJsonFile Create(Func<bool>? enabled = null, Action<string>? warn = null, Func<DateTime>? clock = null) =>
        new(StatePath, Count, 4096, enabled, warn, clock);

    [Fact]
    public async Task FirstSuccessfulSaveAlreadyHasARecoveryGeneration()
    {
        var file = Create(() => true);
        file.Write("[1]");
        await file.FlushPendingWritesAsync();
        Assert.Equal("[1]", File.ReadAllText(StatePath + ".last-good.1"));
    }

    [Fact]
    public void DefaultPreservesLegacySavesWithoutGenerationsOrShrinkGate()
    {
        File.WriteAllText(StatePath, "[1,2,3,4]");
        var file = Create();
        file.Write("[]");
        Assert.Equal("[]", File.ReadAllText(StatePath));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task GenerationsKeepTheExistingPayloadAndAreBoundedAndTimeSpaced()
    {
        var now = DateTime.UtcNow;
        File.WriteAllText(StatePath, "[0]");
        var file = Create(() => true, clock: () => now);
        for (var i = 1; i <= 5; i++)
        {
            file.Write("[" + i + "]");
            await file.FlushPendingWritesAsync();
            if (i == 1)
            {
                file.Write("[10]");
                await file.FlushPendingWritesAsync();
                Assert.Equal("[0]", File.ReadAllText(StatePath + ".last-good.1"));
                Assert.False(File.Exists(StatePath + ".last-good.2"));
            }
            now = now.AddMinutes(1);
        }
        Assert.Equal("[5]", File.ReadAllText(StatePath));
        Assert.Equal("[4]", File.ReadAllText(StatePath + ".last-good.1"));
        Assert.Equal("[3]", File.ReadAllText(StatePath + ".last-good.2"));
        Assert.Equal("[2]", File.ReadAllText(StatePath + ".last-good.3"));
        Assert.Equal(4, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task CorruptPrimaryStartsEmptyAndKeepsEvidenceWithoutResurrectingOlderState()
    {
        File.WriteAllText(StatePath, "broken");
        File.WriteAllText(StatePath + ".last-good.1", "[1,2]");
        var file = Create(() => true);
        Assert.Null(file.Read());
        Assert.Equal("broken", File.ReadAllText(StatePath + ".corrupt"));
        file.Write("[3]");
        await file.FlushPendingWritesAsync();
        Assert.Equal("[3]", File.ReadAllText(StatePath));
        Assert.Equal("[1,2]", File.ReadAllText(StatePath + ".last-good.1"));
    }

    [Fact]
    public async Task UnexpectedShrinkKeepsPrimaryAndCandidateButExplicitRemovalCanSaveEmpty()
    {
        File.WriteAllText(StatePath, "[1,2,3,4]");
        var warnings = 0;
        var file = Create(() => true, _ => warnings++);
        file.Write("[1]");
        await file.FlushPendingWritesAsync();
        file.Write("[]");
        await file.FlushPendingWritesAsync();
        Assert.Equal("[1,2,3,4]", File.ReadAllText(StatePath));
        Assert.Equal("[]", File.ReadAllText(StatePath + ".shrink-rejected"));
        Assert.Equal(1, warnings);
        file.Write("[]", allowShrink: true);
        await file.FlushPendingWritesAsync();
        Assert.Equal("[]", File.ReadAllText(StatePath));
        Assert.Equal("[1,2,3,4]", File.ReadAllText(StatePath + ".last-good.1"));
    }

    [Fact]
    public async Task PrimaryCommitsBeforeReturnWhileGenerationsRunWithoutAttributionAndDoNotHoldLaterSaves()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var attribution = new AsyncLocal<string?> { Value = "settings" };
        var enabled = true;
        var file = new GuardedJsonFile(StatePath, json =>
        {
            if (caller != Environment.CurrentManagedThreadId)
            {
                Assert.Null(attribution.Value);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return Count(json);
        }, 4096, () => enabled);
        file.Write("[1]");
        Assert.Equal("[1]", File.ReadAllText(StatePath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        file.Write("[]", allowShrink: true);
        enabled = false;
        file.Write("[2]");
        Assert.Equal("[2]", File.ReadAllText(StatePath));
        release.Set();
        await file.FlushPendingWritesAsync();
        Assert.Equal("[2]", File.ReadAllText(StatePath));
    }

    [Fact]
    public async Task SaveFailureWarnsOnceAndLaterSaveRetries()
    {
        Directory.CreateDirectory(StatePath);
        var warnings = 0;
        var file = Create(() => true, _ => warnings++);
        file.Write("[1]");
        await file.FlushPendingWritesAsync();
        file.Write("[2]");
        await file.FlushPendingWritesAsync();
        Assert.Equal(1, warnings);
        Directory.Delete(StatePath);
        file.Write("[3]");
        await file.FlushPendingWritesAsync();
        Assert.Equal("[3]", File.ReadAllText(StatePath));
    }

    [Fact]
    public async Task InvalidOrOversizedCandidatesCannotReplaceGoodState()
    {
        File.WriteAllText(StatePath, "[1]");
        var file = Create(() => true);
        foreach (var candidate in new[] { "null", "broken", new string(' ', 4097) })
        {
            file.Write(candidate, allowShrink: true);
            await file.FlushPendingWritesAsync();
            Assert.Equal("[1]", File.ReadAllText(StatePath));
        }
    }

    [Fact]
    public async Task GuardedFeedRemovalAndUnmuteApplyImmediatelyAndPersistEmpty()
    {
        var store = FeedViewStore.ForFile(StatePath, () => true);
        var view = store.Save(null, "Everything");
        store.SetMuted("site", "movies", true);
        await store.FlushPendingWritesAsync();
        Assert.True(store.Remove(view.Id));
        Assert.Empty(store.Views);
        store.ClearMutes();
        Assert.Empty(store.MutedCategories);
        await store.FlushPendingWritesAsync();
        var reloaded = FeedViewStore.ForFile(StatePath, () => true);
        Assert.Empty(reloaded.Views);
        Assert.Empty(reloaded.MutedCategories);
        Assert.False(File.Exists(StatePath + ".shrink-rejected"));
    }

    [Fact]
    public async Task GuardedFeedPersistenceFailureDoesNotUndoVisibleState()
    {
        Directory.CreateDirectory(StatePath);
        var store = FeedViewStore.ForFile(StatePath, () => true);
        var view = store.Save(null, "Visible immediately");
        Assert.Equal(view.Id, Assert.Single(store.Views).Id);
        await store.FlushPendingWritesAsync();
        Assert.Equal(view.Id, Assert.Single(store.Views).Id);
        Directory.Delete(StatePath);
        store.SetMuted("site", "movies", true);
        await store.FlushPendingWritesAsync();
        Assert.Equal(view.Id, Assert.Single(FeedViewStore.ForFile(StatePath).Views).Id);
    }

    [Fact]
    public async Task EstablishedFeedCapacityTrimmingIsAnIntentionalShrink()
    {
        var views = Enumerable.Range(0, FeedViewStore.MaximumViews * 3)
            .Select(i => new FeedView("v" + i, "View " + i, "", "All", "All", DateTime.UtcNow.AddSeconds(i), []));
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new { views, mutedCategories = Array.Empty<FeedCategoryMute>() },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var store = FeedViewStore.ForFile(StatePath, () => true);
        var latest = store.Save(null, "Newest");
        await store.FlushPendingWritesAsync();
        Assert.Equal(FeedViewStore.MaximumViews, store.Views.Count);
        Assert.Contains(FeedViewStore.ForFile(StatePath).Views, view => view.Id == latest.Id);
        Assert.False(File.Exists(StatePath + ".shrink-rejected"));
    }
}
