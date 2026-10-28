using System.Diagnostics;
using System.IO;
using System.Text;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public sealed class IrcLogSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-search-" + Guid.NewGuid().ToString("N"));
    private string LogPath(int daysAgo = 0) => Path.Combine(_root, $"fixture-{DateTime.Today.AddDays(-daysAgo):yyyy-MM-dd}.log");
    private void Write(string text, int daysAgo = 0)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(LogPath(daysAgo), text);
    }
    private Task<IrcSearchResult> Search(string pattern = "example", bool includePrivate = false,
        Func<IReadOnlyDictionary<string, IReadOnlyList<PmHistoryEntry>>>? snapshot = null,
        CancellationToken ct = default) => IrcLogSearch.SearchAsync(_root, "fixture", pattern, includePrivate, snapshot, ct);

    [Fact]
    public async Task SearchesSharedLiveLogsAndParsesDatesWithoutWriting()
    {
        Write("12:34:56\t#channel\tnick\tEXAMPLE.2026 release\nmalformed\n", 1);
        using var writer = new FileStream(LogPath(1), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var result = await Search(@"example\.\d{4}");
        var match = Assert.Single(result.Matches);
        Assert.Equal(DateTime.Today.AddDays(-1).AddHours(12).AddMinutes(34).AddSeconds(56), match.Timestamp);
        Assert.False(match.IsPrivate);
        Assert.Equal("complete", result.Status);
        Assert.Equal(1, result.FilesRead);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidPatternsHaveFixedLocalErrors(string pattern)
    {
        var result = await Search(pattern);
        Assert.Equal("invalid-pattern", result.Status);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task PatternLengthIsBoundedBeforeParsing()
        => Assert.Equal("invalid-pattern", (await Search(new string('a', 513))).Status);

    [Theory]
    [InlineData("../fixture")]
    [InlineData("..\\fixture")]
    [InlineData("fixture:stream")]
    public async Task ConfiguredIdentityCannotBecomeAPath(string id)
        => Assert.Equal("invalid-server", (await IrcLogSearch.SearchAsync(_root, id, "example", false)).Status);

    [Fact]
    public async Task CatastrophicUserPatternStopsAtMatchTimeout()
    {
        Write("12:00:00\t#channel\tnick\t" + new string('a', 4000) + "!\n");
        var clock = Stopwatch.StartNew();
        var result = await Search("(a+)+$");
        Assert.Equal("pattern-timeout", result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResultsAreBoundedAndOversizedLinesDoNotHideLaterLines()
    {
        Write(new string('x', 100_000) + "\n" + string.Concat(Enumerable.Repeat("12:00:00\t#channel\tnick\texample\n", 201)));
        var result = await Search();
        Assert.Equal(IrcLogSearch.MaximumResults, result.Matches.Count);
        Assert.Equal("result-limit", result.Status);
    }

    [Fact]
    public async Task Result_cap_keeps_latest_channel_matches_in_descending_order()
    {
        Write(string.Concat(Enumerable.Range(0, 250).Select(i => $"{DateTime.Today.AddMinutes(i):HH:mm:ss}\t#channel\tnick\texample {i}\n")));
        var result = await Search();
        Assert.Equal("result-limit", result.Status);
        Assert.Equal(Enumerable.Range(50, 200).Reverse().Select(i => "example " + i), result.Matches.Select(m => m.Text));
    }

    [Fact]
    public async Task Recent_pm_and_channel_matches_share_cap_by_time_not_target_insertion_order()
    {
        Write("23:00:00\t#channel\tnick\texample newest channel\n");
        var snapshot = new Dictionary<string, IReadOnlyList<PmHistoryEntry>>
        {
            ["old"] = Enumerable.Range(0, 200).Select(i => new PmHistoryEntry { Timestamp = DateTime.Today.AddDays(-1).AddMinutes(i), Text = "example old" }).ToArray(),
            ["recent"] = Enumerable.Range(0, 200).Select(i => new PmHistoryEntry { Timestamp = DateTime.Today.AddMinutes(i), Text = "example recent " + i }).ToArray()
        };
        var result = await Search(includePrivate: true, snapshot: () => snapshot);
        Assert.Equal(200, result.Matches.Count);
        Assert.Equal("example newest channel", result.Matches[0].Text);
        Assert.DoesNotContain(result.Matches, m => m.Target == "old");
        Assert.Equal("example recent 1", result.Matches[^1].Text);
    }

    [Fact]
    public async Task OversizedAndUnavailableFilesReportPartialInsteadOfWholeSearchFailure()
    {
        Write(new string('x', 9000) + "\n12:00:00\t#channel\tnick\texample\n");
        Write("12:00:00\t#channel\tnick\texample\n", 1);
        using var blocked = new FileStream(LogPath(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Search();
        Assert.Single(result.Matches);
        Assert.Equal("partial", result.Status);
    }

    [Fact]
    public async Task WholeBudgetIncludesPrivateSnapshotAndRunsAwayFromCaller()
    {
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pending = Search(includePrivate: true, snapshot: () =>
        {
            Interlocked.Increment(ref calls);
            try
            {
                release.Wait();
                return new Dictionary<string, IReadOnlyList<PmHistoryEntry>>();
            }
            finally { exited.SetResult(); }
        });
        try
        {
            Assert.False(pending.IsCompleted);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("time-budget", result.Status);
            var next = await Search(includePrivate: true, snapshot: () =>
            {
                Interlocked.Increment(ref calls);
                return new Dictionary<string, IReadOnlyList<PmHistoryEntry>>();
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("time-budget", next.Status);
            Assert.Equal(1, calls);
        }
        finally { release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CancellationDuringSnapshotWaitReturnsWithoutWaitingForDisk()
    {
        using var release = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Search(includePrivate: true, snapshot: () =>
        {
            entered.SetResult();
            try { release.Wait(); return new Dictionary<string, IReadOnlyList<PmHistoryEntry>>(); }
            finally { exited.SetResult(); }
        }, ct: cancelled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task PrivateSearchIsExplicitAndDoesNotWriteDecryptedHistory()
    {
        Directory.CreateDirectory(_root);
        var encryptedPath = Path.Combine(_root, "private.json");
        using var history = new PmHistoryStore(encryptedPath, loadNow: false);
        history.Append("peer", new IrcMessageItem { Nick = "peer", Text = "unique example private text" });
        history.Flush();
        var before = File.ReadAllBytes(encryptedPath);
        Assert.DoesNotContain("unique example private text", Encoding.UTF8.GetString(before));
        var excluded = await Search(snapshot: () => throw new InvalidOperationException("PMs must not be read"));
        Assert.Empty(excluded.Matches);
        var included = await Search(includePrivate: true, snapshot: history.GetAll);
        Assert.True(Assert.Single(included.Matches).IsPrivate);
        Assert.Equal(before, File.ReadAllBytes(encryptedPath));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task CancellationPropagatesAndOldLogsAreNotScanned()
    {
        Write("12:00:00\t#channel\tnick\texample\n", 30);
        Assert.Empty((await Search()).Matches);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search(ct: cancelled.Token));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
