using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Logging;
using GlDrive.Services.Operations;
using Xunit;

namespace GlDrive.Tests;

public sealed class OpsJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-journal-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly List<OpsJournal> _journals = [];
    private string JournalPath => Path.Combine(_root, "ops-journal.jsonl");
    public OpsJournalTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var journal in _journals) journal.Dispose();
        Directory.Delete(_root, true);
    }
    private OpsJournal Create(Func<Func<string, string>>? redactor = null, OpsJournalLimits? limits = null,
        Action<string>? beforeWrite = null, Func<DateTimeOffset>? now = null)
    {
        var journal = new OpsJournal(_root, redactor ?? (() => text => text), now ?? (() => _now),
            limits ?? new(), beforeWrite);
        _journals.Add(journal);
        return journal;
    }
    private OpsEvent Row(int number = 0, string server = "server-a", string severity = "Information", string subject = "Synthetic.Release") =>
        new(_now.AddSeconds(-100 + number), "observation", severity, server, subject, "fact-" + number, "application", "correlation");
    private static async Task Flush(OpsJournal journal) => await journal.FlushAsync().WaitAsync(TimeSpan.FromSeconds(8));
    private static async Task Stop(OpsJournal journal)
    { await journal.DisposeAsync(); await journal.Completion.WaitAsync(TimeSpan.FromSeconds(8)); }

    [Fact]
    public async Task Per_release_noise_is_excluded_and_repeated_states_are_coalesced_until_a_transition()
    {
        var journal = Create();
        foreach (var kind in new[] { "auto-race-verdict", "auto-race-site", "auto-race-destination", "race-admission", "race-ended" })
            for (var i = 0; i < 2000; i++) Assert.False(journal.TryRecord(Row(i) with { Kind = kind }));
        for (var i = 0; i < 1000; i++) journal.TryRecord(Row() with { Kind = "irc-state", Detail = "Reconnecting", Severity = "Warning" });
        journal.TryRecord(Row(1) with { Kind = "config-remount", Detail = "connection.host" });
        journal.TryRecord(Row(2) with { Kind = "irc-state", Detail = "Connected" });
        journal.TryRecord(Row(3) with { Kind = "irc-state", Detail = "Reconnecting", Severity = "Warning" });
        await Flush(journal);
        Assert.Equal(4, (await journal.ReadTimelineAsync()).Count);
        Assert.Equal(0, journal.Dropped);
    }

    [Fact]
    public async Task Default_capacity_retains_days_of_operational_facts_without_memory_multiplier_eviction()
    {
        var journal = Create();
        for (var i = 0; i < 7200; i++)
        {
            journal.TryRecord(Row() with { Time = _now.AddDays(-89).AddMinutes(i * 17), Detail = "transition-" + i,
                Subject = i == 0 ? "Oldest.Operation" : "Later.Operation" });
            if (i % 200 == 199) await Flush(journal);
        }
        await Flush(journal);
        Assert.Single(journal.SnapshotForSubject("Oldest.Operation"));
        Assert.InRange(new FileInfo(JournalPath).Length, 1, 8 * 1024 * 1024);
        Assert.Equal(7200, File.ReadLines(JournalPath).Count());
    }

    [Fact]
    public async Task Change_inside_an_incident_group_is_context_for_the_first_issue()
    {
        var journal = Create();
        journal.TryRecord(Row() with { Kind = "mount-state", Time = _now.AddMinutes(-6), Detail = "Connected" });
        journal.TryRecord(Row(1) with { Kind = "config-remount", Time = _now.AddMinutes(-4), Detail = "connection.host" });
        journal.TryRecord(Row(2, severity: "Warning") with { Kind = "irc-state", Time = _now.AddMinutes(-2), Detail = "Reconnecting" });
        await Flush(journal);
        Assert.Contains(Assert.Single(await journal.ReadIncidentsAsync()).PrecedingChanges, row => row.Kind == "config-remount");
    }

    [Fact]
    public async Task Torn_and_malformed_rows_do_not_discard_valid_neighbors()
    {
        var journal = Create(); journal.TryRecord(Row()); journal.TryRecord(Row(1)); await Flush(journal); await Stop(journal);
        var lines = File.ReadAllLines(JournalPath);
        File.WriteAllText(JournalPath, lines[0] + "\n{bad}\n{\"event\":{\"time\":17}}\n" + lines[1] + "\n{\"event\":\u0000");
        var restarted = Create(); await Flush(restarted);
        var facts = await restarted.ReadTimelineAsync();
        Assert.Equal(new[] { "fact-1", "fact-0" }, facts.Select(row => row.Detail));
        Assert.Contains("invalid journal rows skipped: 3", Assert.IsAssignableFrom<IOpsReadCoverage>(facts).Limits);
        Assert.Equal(2, File.ReadAllLines(JournalPath).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_invalidated_inventory_preserves_stored_history_until_recovery(bool invalidateDuringLoad)
    {
        var journal = Create(); journal.TryRecord(Row()); journal.TryRecord(Row(1)); await Flush(journal); await Stop(journal);
        var stored = File.ReadAllText(JournalPath); var unavailable = 1; var calls = 0;
        var restarted = Create(() =>
        {
            if (!invalidateDuringLoad && Volatile.Read(ref unavailable) != 0) throw new IOException("inventory unavailable");
            return value => Volatile.Read(ref unavailable) != 0 && Interlocked.Increment(ref calls) > 7 ? "[redaction unavailable]" : value;
        });
        await Flush(restarted);
        Assert.Equal(stored, File.ReadAllText(JournalPath));
        Volatile.Write(ref unavailable, 0);
        await Flush(restarted);
        Assert.Equal(new[] { "fact-1", "fact-0" }, (await restarted.ReadTimelineAsync()).Select(row => row.Detail));
        Assert.DoesNotContain("[redaction unavailable]", File.ReadAllText(JournalPath));
    }

    [Fact]
    public async Task ProducerDoesNotWaitForRedactorAndQueueEvictsOldest()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var journal = Create(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(8)));
            return text => text;
        }, new(MaxEvents: 10, QueueCapacity: 4));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(4)));
            var elapsed = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++) Assert.True(journal.TryRecord(Row(i)));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(996, journal.Dropped);
            Assert.False(journal.Ready.IsCompleted);
        }
        finally { release.Set(); }
        await Flush(journal);
        // Query clock excludes future timestamps, so query through an equivalent current clock below.
        await Stop(journal);
        var restarted = Create(now: () => _now.AddHours(1));
        await restarted.Ready;
        Assert.Equal(new[] { "fact-999", "fact-998", "fact-997", "fact-996" },
            (await restarted.ReadTimelineAsync()).Select(row => row.Detail));
    }

    [Fact]
    public async Task WorkerAndQueryDoNotInheritConfigWriteAttribution()
    {
        var actors = new ConcurrentBag<string>();
        OpsJournal journal;
        using (ConfigWriteScope.Begin("synthetic-request"))
            journal = Create(() => { actors.Add(ConfigWriteScope.Current.Actor); return text => text; });
        journal.TryRecord(Row());
        await Flush(journal);
        using (ConfigWriteScope.Begin("other-request")) await journal.ReadTimelineAsync();
        Assert.NotEmpty(actors);
        Assert.All(actors, actor => Assert.Equal("application", actor));
    }

    [Fact]
    public async Task RedactsEveryFieldBeforePublishedMemoryAndDiskAndAfterInventoryChanges()
    {
        var secrets = new List<string> { "synthetic-fish-key", "synthetic-control-token", "synthetic-password" };
        var journal = Create(() => DiagnosticTextRedactor.Create(secrets.ToArray()));
        var fact = Row() with { Kind = secrets[0], Severity = secrets[1], ServerId = secrets[2],
            Subject = secrets[0], Detail = secrets[1] + " later-credential", Actor = secrets[2], CorrelationId = secrets[0] };
        journal.TryRecord(fact);
        await Flush(journal);
        var stored = File.ReadAllText(JournalPath);
        var visible = JsonSerializer.Serialize(await journal.ReadTimelineAsync());
        foreach (var secret in secrets) { Assert.DoesNotContain(secret, stored); Assert.DoesNotContain(secret, visible); }
        secrets.Add("later-credential");
        Assert.DoesNotContain("later-credential", JsonSerializer.Serialize(await journal.ReadTimelineAsync()));
        await Stop(journal);
        var restarted = Create(() => DiagnosticTextRedactor.Create(secrets.ToArray()));
        await Flush(restarted);
        Assert.DoesNotContain("later-credential", File.ReadAllText(JournalPath));
    }

    [Fact]
    public async Task SubjectJoinIsExactCaseInsensitiveAcrossRedactionAndRestart()
    {
        var journal = Create(() => DiagnosticTextRedactor.Create(["SecretPart"]));
        journal.TryRecord(Row(subject: "Synthetic.SecretPart.Release"));
        journal.TryRecord(Row(1, subject: "Synthetic.SecretPart.Release.Extra"));
        await Flush(journal);
        var matching = journal.SnapshotForSubject("synthetic.secretpart.release");
        Assert.Single(matching);
        Assert.DoesNotContain("SecretPart", matching[0].Subject);
        await Stop(journal);
        var restarted = Create(() => DiagnosticTextRedactor.Create(["SecretPart"]));
        await restarted.Ready;
        Assert.Single(restarted.SnapshotForSubject("SYNTHETIC.SECRETPART.RELEASE"));
    }

    [Fact]
    public async Task RedactorFailureOmitsWholeFieldsWithoutDroppingOperationalFacts()
    {
        var journal = Create(() => throw new IOException("synthetic inventory failure"));
        Assert.True(journal.TryRecord(Row() with { Detail = "synthetic-secret" }));
        await Flush(journal);
        var row = Assert.Single(await journal.ReadTimelineAsync());
        Assert.Equal("", row.Detail);
        Assert.DoesNotContain("synthetic-secret", File.ReadAllText(JournalPath));
        Assert.True(journal.PersistenceHealthy);
    }

    [Fact]
    public async Task OversizedFieldsAndExpandedRedactionAreOmittedWhole()
    {
        var journal = Create(() => value => value == "expand" ? new string('x', 5000) : value);
        journal.TryRecord(Row() with { Detail = new string('a', 4090) + "partial-sensitive-value", Subject = "expand" });
        await Flush(journal);
        var row = Assert.Single(await journal.ReadTimelineAsync());
        Assert.Equal("", row.Detail);
        Assert.Equal("", row.Subject);
        Assert.DoesNotContain("partial", File.ReadAllText(JournalPath));
    }

    [Fact]
    public async Task SerializationEscapingCannotExceedEventOrDiskBounds()
    {
        var journal = Create(limits: new(MaxBytes: 8192, MaxEventBytes: 1024));
        journal.TryRecord(Row() with { Detail = new string('\u0001', 1000), Subject = "kept-unless-whole-event-too-large" });
        await Flush(journal);
        var row = Assert.Single(await journal.ReadTimelineAsync());
        Assert.Equal("", row.Detail);
        Assert.InRange(new FileInfo(JournalPath).Length, 1, 8192);
        Assert.All(File.ReadAllLines(JournalPath), line => Assert.InRange(Encoding.UTF8.GetByteCount(line) + 1, 1, 1024));
    }

    [Fact]
    public async Task DurableRestartRetainsFactsAndEvictsOlderThanNinetyDays()
    {
        var journal = Create();
        journal.TryRecord(Row() with { Time = _now.AddDays(-91), Detail = "expired" });
        journal.TryRecord(Row(1) with { Time = _now.AddDays(-90), Detail = "boundary" });
        journal.TryRecord(Row(2) with { Detail = "recent" });
        await Flush(journal);
        await Stop(journal);
        var restarted = Create();
        Assert.Equal(new[] { "recent", "boundary" }, (await restarted.ReadTimelineAsync(days: 90)).Select(row => row.Detail));
        await Flush(restarted);
        Assert.DoesNotContain("expired", File.ReadAllText(JournalPath));
    }

    [Fact]
    public async Task CapacityEvictsOldestAndCompactionLeavesAppendHeadroom()
    {
        var journal = Create(limits: new(MaxEvents: 8));
        for (var i = 0; i < 9; i++) journal.TryRecord(Row(i));
        await Flush(journal);
        var first = await journal.ReadTimelineAsync();
        Assert.Equal(6, first.Count);
        Assert.Equal("fact-8", first[0].Detail);
        Assert.DoesNotContain(first, row => row.Detail == "fact-0");
        var compactedLength = new FileInfo(JournalPath).Length;
        journal.TryRecord(Row(9));
        await Flush(journal);
        Assert.Equal(7, (await journal.ReadTimelineAsync()).Count);
        Assert.True(new FileInfo(JournalPath).Length > compactedLength);
    }

    [Theory]
    [InlineData(3, 1048576)]
    [InlineData(100, 4096)]
    public async Task ReducedLimitsOnRestartKeepNewestValidFactsInsteadOfClearingHistory(int eventLimit, int byteLimit)
    {
        var journal = Create();
        for (var i = 0; i < 20; i++) journal.TryRecord(Row(i));
        await Flush(journal);
        await Stop(journal);
        var restarted = Create(limits: new(MaxEvents: eventLimit, MaxBytes: byteLimit, MaxEventBytes: 1024));
        var retained = await restarted.ReadTimelineAsync();
        Assert.InRange(retained.Count, 1, eventLimit);
        Assert.Equal("fact-19", retained[0].Detail);
        Assert.True(Assert.IsAssignableFrom<IOpsReadCoverage>(retained).Partial);
        Assert.False(File.Exists(JournalPath + ".corrupt"));
        Assert.True(restarted.TryRecord(Row(20)));
        await Flush(restarted);
        Assert.Equal("fact-20", (await restarted.ReadTimelineAsync())[0].Detail);
        Assert.InRange(new FileInfo(JournalPath).Length, 1, byteLimit);
    }

    [Fact]
    public async Task FailedWritesKeepBoundedRecentMemoryAndRecoverOnNextFlush()
    {
        var fail = 1;
        var journal = Create(limits: new(MaxEvents: 8), beforeWrite: _ =>
        { if (Volatile.Read(ref fail) != 0) throw new IOException("synthetic write failure"); });
        for (var i = 0; i < 15; i++) journal.TryRecord(Row(i));
        await Flush(journal);
        Assert.False(journal.PersistenceHealthy);
        var recent = await journal.ReadTimelineAsync();
        Assert.InRange(recent.Count, 1, 8);
        Assert.Equal("fact-14", recent[0].Detail);
        Volatile.Write(ref fail, 0);
        await Flush(journal);
        Assert.True(journal.PersistenceHealthy);
        await Stop(journal);
        var restarted = Create(limits: new(MaxEvents: 8));
        Assert.Equal("fact-14", (await restarted.ReadTimelineAsync())[0].Detail);
    }

    [Fact]
    public async Task CorruptReadMakesOneRedactedBackupAndStartsEmptyThenPersistsNewFacts()
    {
        var corrupt = "{\"credential\":\"synthetic-private-token\", broken";
        File.WriteAllText(JournalPath, corrupt);
        var journal = Create(() => DiagnosticTextRedactor.Create(["synthetic-private-token"]));
        await journal.Ready;
        Assert.Empty(await journal.ReadTimelineAsync());
        Assert.True(File.Exists(JournalPath + ".corrupt"));
        Assert.DoesNotContain("synthetic-private-token", File.ReadAllText(JournalPath + ".corrupt"));
        Assert.True(journal.TryRecord(Row()));
        await Flush(journal);
        Assert.Single(await journal.ReadTimelineAsync());
        Assert.Single(Directory.GetFiles(_root, "*.corrupt"));
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }

    [Fact]
    public async Task OversizedCorruptInputAndAuxiliariesDoNotPreventRecoveryOrGrowBackups()
    {
        File.WriteAllText(JournalPath, new string('x', 20_000));
        File.WriteAllText(JournalPath + ".corrupt", new string('x', 20_000));
        File.WriteAllText(JournalPath + ".tmp", new string('x', 20_000));
        var journal = Create(limits: new(MaxBytes: 8192, MaxEventBytes: 1024));
        await journal.Ready;
        Assert.Empty(await journal.ReadTimelineAsync());
        journal.TryRecord(Row());
        await Flush(journal);
        Assert.Single(await journal.ReadTimelineAsync());
        Assert.All(Directory.GetFiles(_root), path => Assert.InRange(new FileInfo(path).Length, 0, 8192));
        Assert.InRange(Directory.GetFiles(_root).Sum(path => new FileInfo(path).Length), 0, 3 * 8192);
    }

    [Fact]
    public async Task LockedCorruptFileLeavesMemoryUsableAndRecoversAfterUnlock()
    {
        File.WriteAllText(JournalPath, "broken");
        OpsJournal journal;
        using (var locked = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal = Create();
            await journal.Ready;
            journal.TryRecord(Row());
            await Flush(journal);
            Assert.Single(await journal.ReadTimelineAsync());
            Assert.False(journal.PersistenceHealthy);
        }
        await Flush(journal);
        Assert.True(journal.PersistenceHealthy);
        await Stop(journal);
        var restarted = Create();
        Assert.Single(await restarted.ReadTimelineAsync());
    }

    [Fact]
    public async Task OptionalWrongJsonTypesDoNotVoidValidEvents()
    {
        var stored = JsonSerializer.Serialize(new { @event = new { time = _now, kind = "observation", severity = "Warning",
            serverId = "server-a", subject = "Synthetic.Release", detail = new { future = true }, actor = 7, correlationId = (object?)null } });
        File.WriteAllText(JournalPath, stored + "\n");
        var journal = Create();
        var row = Assert.Single(await journal.ReadTimelineAsync());
        Assert.Equal("observation", row.Kind);
        Assert.Equal("", row.Detail);
        Assert.Equal("", row.Actor);
        Assert.False(File.Exists(JournalPath + ".corrupt"));
        Assert.Single(journal.SnapshotForSubject("synthetic.release"));
    }

    [Fact]
    public async Task ExactServerIdentitySurvivesRedactedLabelCollisions()
    {
        var journal = Create(() => DiagnosticTextRedactor.Create(["server-a", "server-b"]));
        journal.TryRecord(Row(server: "server-a", severity: "Warning"));
        journal.TryRecord(Row(1, server: "server-b", severity: "Error"));
        await Flush(journal);
        var a = Assert.Single(await journal.ReadTimelineAsync("server-a"));
        var b = Assert.Single(await journal.ReadTimelineAsync("server-b"));
        Assert.Equal(a.ServerId, b.ServerId);
        Assert.NotEqual(a.Detail, b.Detail);
        Assert.Equal(2, (await journal.ReadIncidentsAsync()).Count);
    }

    [Fact]
    public async Task IncidentsUseExactServerGapAndBoundedContextContainingTheLatestIssue()
    {
        var journal = Create();
        for (var i = 0; i < 40; i++) journal.TryRecord(Row(i, severity: i == 25 ? "Warning" : "Information"));
        journal.TryRecord(Row(41, "server-b", "Error"));
        journal.TryRecord(Row(42, severity: "Error") with { Time = _now.AddMinutes(-30) });
        journal.TryRecord(Row(43, "server-c")); // Informational groups alone are not incidents.
        await Flush(journal);
        var incidents = await journal.ReadIncidentsAsync();
        Assert.Equal(3, incidents.Count);
        var primary = Assert.Single(incidents, incident => incident.EventCount == 40);
        Assert.Equal(20, primary.Events.Count);
        Assert.Contains(primary.Events, row => row.Detail == "fact-25" && row.Severity == "Warning");
        Assert.Equal(2, (await journal.ReadIncidentsAsync("server-a")).Count);
    }

    [Fact]
    public async Task IncidentClassificationSurvivesSecretOverlapAndRestart()
    {
        var journal = Create(() => DiagnosticTextRedactor.Create(["a"]));
        journal.TryRecord(Row(severity: "Warning"));
        await Flush(journal);
        var incident = Assert.Single(await journal.ReadIncidentsAsync());
        Assert.DoesNotContain("a", incident.Events[0].Severity);
        await Stop(journal);
        var restarted = Create(() => DiagnosticTextRedactor.Create(["a"]));
        Assert.Single(await restarted.ReadIncidentsAsync());
    }

    [Fact]
    public async Task PrecedingChangesAreLimitedToTwoHoursSameServerOrGlobalAndBoundedTen()
    {
        var journal = Create(() => DiagnosticTextRedactor.Create(["config"]));
        for (var i = 0; i < 15; i++)
            journal.TryRecord(Row(i, server: i % 2 == 0 ? "" : "server-a") with
                { Kind = i % 2 == 0 ? "config-change-set" : "config-saved", Time = _now.AddMinutes(-100 + i), Detail = "change-" + i });
        journal.TryRecord(Row(server: "server-b") with { Kind = "agent-run", Time = _now.AddMinutes(-20), Detail = "unrelated-server" });
        journal.TryRecord(Row(server: "") with { Kind = "update-available", Time = _now.AddHours(-3), Detail = "too-old" });
        journal.TryRecord(Row(severity: "Error"));
        await Flush(journal);
        var incident = Assert.Single(await journal.ReadIncidentsAsync("server-a"));
        Assert.Equal(10, incident.PrecedingChanges.Count);
        Assert.DoesNotContain(incident.PrecedingChanges, row => row.Detail is "unrelated-server" or "too-old");
        Assert.Contains(incident.PrecedingChanges, row => row.Detail == "change-14");
        Assert.All(incident.PrecedingChanges, row => Assert.DoesNotContain("config", row.Kind));
    }

    [Fact]
    public async Task RowLimitIsExplicitAndInvalidQueriesAreRejected()
    {
        var journal = Create();
        journal.TryRecord(Row()); journal.TryRecord(Row(1));
        await Flush(journal);
        var rows = await journal.ReadTimelineAsync(limit: 1);
        var coverage = Assert.IsAssignableFrom<IOpsReadCoverage>(rows);
        Assert.True(coverage.Partial);
        Assert.Contains("row limit", coverage.Limits);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.ReadTimelineAsync(days: 91));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.ReadTimelineAsync(limit: 501));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.ReadIncidentsAsync(limit: 101));
    }

    [Fact]
    public async Task QueryDeadlineReturnsExplicitPartialCoverageBetweenFields()
    {
        var slow = 0;
        var journal = Create(() => value =>
        {
            if (Volatile.Read(ref slow) != 0) Thread.Sleep(110);
            return value;
        });
        for (var i = 0; i < 10; i++) journal.TryRecord(Row(i));
        await Flush(journal);
        Volatile.Write(ref slow, 1);
        var elapsed = Stopwatch.StartNew();
        var rows = await journal.ReadTimelineAsync();
        Assert.InRange(rows.Count, 1, 9);
        Assert.Contains("read time limit", Assert.IsAssignableFrom<IOpsReadCoverage>(rows).Limits);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Volatile.Write(ref slow, 0);
    }

    [Fact]
    public async Task QueryCancellationIsObservedBetweenRedactedFields()
    {
        using var cancelled = new CancellationTokenSource();
        var cancelOnRead = 0;
        var journal = Create(() => value =>
        {
            if (Volatile.Read(ref cancelOnRead) != 0) cancelled.Cancel();
            return value;
        });
        journal.TryRecord(Row());
        await Flush(journal);
        Volatile.Write(ref cancelOnRead, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ReadTimelineAsync(cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.FlushAsync(cancelled.Token));
    }

    [Fact]
    public async Task ExplicitFlushAndDisposeBypassBatchDelayAndDrainAcceptedFacts()
    {
        var journal = Create();
        journal.TryRecord(Row());
        await Flush(journal);
        journal.TryRecord(Row(1));
        await Stop(journal);
        Assert.False(journal.TryRecord(Row(2)));
        var restarted = Create();
        Assert.Equal(2, (await restarted.ReadTimelineAsync()).Count);
    }

    [Fact]
    public async Task OrdinaryWritesAreBatchedInsteadOfFlushingEveryFact()
    {
        var writes = new ConcurrentQueue<long>();
        var journal = Create(beforeWrite: _ => writes.Enqueue(Stopwatch.GetTimestamp()));
        await journal.Ready;
        for (var i = 0; i < 12; i++)
        {
            journal.TryRecord(Row(i));
            await Task.Delay(100);
        }
        var automatic = writes.ToArray();
        foreach (var pair in automatic.Zip(automatic.Skip(1)))
            Assert.True(Stopwatch.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromMilliseconds(950));
        await Flush(journal);
        Assert.Equal(12, (await journal.ReadTimelineAsync()).Count);
    }
}
