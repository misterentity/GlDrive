using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GlDrive.Irc;

// These values stay inside the local search window; they are not automation or prompt data.
public sealed record IrcSearchMatch(DateTime Timestamp, string Target, string Nick, string Text, bool IsPrivate);
public sealed record IrcSearchResult(IReadOnlyList<IrcSearchMatch> Matches, string Status, int FilesRead);

/// <summary>Explicit local history search. Never connects, sends, or writes chat content.</summary>
public static class IrcLogSearch
{
    public const int MaximumResults = 200;
    public const int MaximumPatternLength = 512;
    private const int MaximumLineLength = 8192;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(2);
    // The PM store can be flushing while a snapshot is requested. One outstanding
    // snapshot is enough; a slow disk must not leave a worker behind per search.
    private static readonly SemaphoreSlim PrivateSnapshots = new(1, 1);

    public static Task<IrcSearchResult> SearchAsync(string logDirectory, string serverId, string pattern,
        bool includePrivate,
        Func<IReadOnlyDictionary<string, IReadOnlyList<PmHistoryEntry>>>? privateSnapshot = null,
        CancellationToken cancellationToken = default) => Task.Run(() => SearchCoreAsync(
            logDirectory, serverId, pattern, includePrivate, privateSnapshot, cancellationToken), cancellationToken);

    private static async Task<IrcSearchResult> SearchCoreAsync(string directory, string serverId, string pattern,
        bool includePrivate, Func<IReadOnlyDictionary<string, IReadOnlyList<PmHistoryEntry>>>? privateSnapshot,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SearchBudget);
        var token = deadline.Token;
        var results = new PriorityQueue<IrcSearchMatch, (DateTime Timestamp, long Sequence)>();
        long sequence = 0;
        var limited = false;
        var files = 0;
        var partial = false;
        IrcSearchResult Result(string status) => new(results.UnorderedItems.OrderByDescending(e => e.Priority)
            .Select(e => e.Element).ToArray(), status, files);
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= SearchBudget) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > 128 || serverId is "." or ".." ||
            serverId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || serverId.Contains('/') || serverId.Contains('\\'))
            return Result("invalid-server");
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaximumPatternLength)
            return Result("invalid-pattern");
        Regex matcher;
        try { matcher = new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout); }
        catch (ArgumentException) { return Result("invalid-pattern"); }

        void Add(DateTime timestamp, string target, string nick, string text, bool isPrivate)
        {
            CheckBudget();
            if (text.Length > MaximumLineLength) { partial = true; return; }
            if (!matcher.IsMatch(text)) return;
            results.Enqueue(new(timestamp, target, nick, text, isPrivate), (timestamp, sequence++));
            if (results.Count > MaximumResults) { results.Dequeue(); limited = true; }
        }

        try
        {
            // Decrypted snapshots remain in memory. PM search never creates a plaintext cache.
            if (includePrivate && privateSnapshot != null)
            {
                CheckBudget();
                await PrivateSnapshots.WaitAsync(token).ConfigureAwait(false);
                var snapshotTask = Task.Run(() =>
                {
                    try { return privateSnapshot(); }
                    finally { PrivateSnapshots.Release(); }
                });
                _ = snapshotTask.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var snapshot = await snapshotTask.WaitAsync(token).ConfigureAwait(false);
                CheckBudget();
                if (snapshot.Count > 50) partial = true;
                foreach (var pair in snapshot.OrderByDescending(p => p.Value.LastOrDefault()?.Timestamp).Take(50))
                {
                    if (pair.Value.Count > 200) partial = true;
                    foreach (var entry in pair.Value.TakeLast(200).Reverse())
                        Add(entry.Timestamp, pair.Key, entry.Nick, entry.Text, true);
                }
            }
            var today = DateTime.Today;
            for (var day = 0; day < 30; day++)
            {
                CheckBudget();
                var date = today.AddDays(-day);
                var path = Path.Combine(directory, $"{serverId}-{date:yyyy-MM-dd}.log");
                try
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, leaveOpen: true);
                    files++;
                    var buffer = new char[2048];
                    var line = new StringBuilder();
                    var oversized = false;
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                    {
                        CheckBudget();
                        for (var i = 0; i < read; i++)
                        {
                            var value = buffer[i];
                            if (value == '\n')
                            {
                                if (!oversized) MatchLine(line.ToString(), date);
                                line.Clear();
                                oversized = false;
                            }
                            else if (!oversized && value != '\r')
                            {
                                if (line.Length == MaximumLineLength) { oversized = true; partial = true; line.Clear(); }
                                else line.Append(value);
                            }
                        }
                    }
                    if (!oversized && line.Length > 0) MatchLine(line.ToString(), date);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { partial = true; }
                catch (UnauthorizedAccessException) { partial = true; }
                // Finish the whole day before applying the cap: files append oldest
                // first, so stopping at the first 200 would omit recent matches.
                if (results.Count >= MaximumResults && results.Peek().Timestamp.Date >= date)
                    return Result("result-limit");
            }
            CheckBudget();
            return Result(limited ? "result-limit" : partial ? "partial" : "complete");
        }
        catch (RegexMatchTimeoutException) { return Result("pattern-timeout"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Result("time-budget"); }

        void MatchLine(string line, DateTime date)
        {
            CheckBudget();
            if (IrcLogStore.TryParseLine(line, date, out var entry))
                Add(entry.Timestamp, entry.Channel, entry.Nick, entry.Text, false);
        }
    }
}
