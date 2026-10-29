using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationCallLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-ledger-" + Guid.NewGuid().ToString("N"));
    private string LedgerRoot => Path.Combine(_root, "automation-ledger");
    private static readonly AutomationLedgerLimits Small = new(64, 1024, 4096, 2, 1);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private AutomationCallLedger Create(Func<bool>? enabled = null, Func<int>? capacity = null,
        AutomationLedgerLimits? limits = null, Action<string>? warning = null, Action? beforeWrite = null) =>
        new(_root, enabled ?? (() => true), capacity ?? (() => 20), tool => tool == "servers.list",
            limits ?? Small, warning, beforeWrite);
    private static AutomationCallAudit Call(string? callId = null, string outcome = "completed", JsonElement arguments = default) =>
        new(callId ?? Guid.NewGuid().ToString("N"), "legacy", "Owner", "servers.list", arguments, outcome, 17);
    private string[] Segments() => Directory.GetFiles(LedgerRoot, "*.jsonl").Order().ToArray();

    [Fact]
    public async Task OnlyArgumentShapeAndStableClientIdentityReachMemoryOrDisk()
    {
        using var input = JsonDocument.Parse("{\"secret-property-name\":\"password-content\",\"nested\":{\"another-private-key\":[731942,true,null]}}");
        await using var ledger = Create();
        ledger.Record(new("private-call-name", "bearer-must-not-appear", "private-client-label", "servers.list",
            input.RootElement, "completed", 42, "private-job-name"));
        await ledger.FlushAsync();
        var row = Assert.Single(ledger.Snapshot());
        Assert.Equal(AutomationCallLedger.ClientIdentity("bearer-must-not-appear"), row.ClientId);
        var serialized = JsonSerializer.Serialize(row) + string.Concat(Segments().Select(File.ReadAllText));
        foreach (var secret in new[] { "secret-property-name", "password-content", "another-private-key", "731942",
                     "bearer-must-not-appear", "private-client-label", "private-call-name", "private-job-name" })
            Assert.DoesNotContain(secret, serialized);
        Assert.Contains("[string]", serialized);
        Assert.Contains("[number]", serialized);
    }

    [Fact]
    public async Task UnknownOperationAndUntrustedOutcomeCannotSmuggleText()
    {
        await using var ledger = Create();
        ledger.Record(Call() with { Tool = "secret-in-tool-name", Outcome = "provider-error-with-secret" });
        await ledger.FlushAsync();
        var row = Assert.Single(ledger.Snapshot());
        Assert.Equal("unregistered", row.Tool);
        Assert.Equal("failed", row.Outcome);
        Assert.DoesNotContain("secret", string.Concat(Segments().Select(File.ReadAllText)));
    }

    [Fact]
    public async Task AcceptedAndTerminalEventsAppendWithoutChangingEarlierRows()
    {
        await using var ledger = Create();
        var id = Guid.NewGuid().ToString("N");
        ledger.Record(Call(id, "accepted"));
        await ledger.FlushAsync();
        var path = Assert.Single(Segments());
        var first = File.ReadAllBytes(path);
        ledger.Record(Call(id, "completed"));
        await ledger.FlushAsync();
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(first));
        Assert.Equal(new[] { "completed", "accepted" }, ledger.Snapshot().Select(row => row.Outcome));
        Assert.All(ledger.Snapshot(), row => Assert.Equal(id, row.CallId));
    }

    [Fact]
    public async Task CapacityEvictsOldestMemoryAndSegmentsWhileAdmittingNewCalls()
    {
        var limits = Small with { SegmentBytes = 1100 };
        await using var ledger = Create(capacity: () => 3, limits: limits);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids) { ledger.Record(Call(id)); await ledger.FlushAsync(); }
        Assert.Equal(ids.TakeLast(3).Reverse(), ledger.Snapshot().Select(row => row.CallId));
        Assert.True(Segments().Length <= limits.SegmentCount);
        Assert.All(Segments(), path => Assert.InRange(new FileInfo(path).Length, 1, limits.SegmentBytes));
        Assert.Contains(ids[^1], string.Concat(Segments().Select(File.ReadAllText)));
        Assert.DoesNotContain(ids[0], string.Concat(Segments().Select(File.ReadAllText)));
    }

    [Fact]
    public async Task FailedWritesKeepBoundedMemoryWarnOnceAndRetryWithoutBlockingCalls()
    {
        var fail = true;
        var warnings = 0;
        await using var ledger = Create(capacity: () => 3, warning: _ => warnings++,
            beforeWrite: () => { if (fail) throw new IOException("private-provider-error"); });
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids) { ledger.Record(Call(id)); await ledger.FlushAsync(); }
        Assert.Equal(ids.TakeLast(3).Reverse(), ledger.Snapshot().Select(row => row.CallId));
        Assert.False(ledger.PersistenceHealthy);
        Assert.Equal(1, warnings);
        fail = false;
        await ledger.FlushAsync();
        Assert.True(ledger.PersistenceHealthy);
        Assert.All(ids.TakeLast(3), id => Assert.Contains(id, string.Concat(Segments().Select(File.ReadAllText))));
    }

    [Fact]
    public async Task MalformedLinesAreQuarantinedWithoutDiscardingValidRows()
    {
        var id = Guid.NewGuid().ToString("N");
        await using (var first = Create()) { first.Record(Call(id)); await first.FlushAsync(); }
        var path = Assert.Single(Segments());
        File.AppendAllText(path, "{broken-json\n");
        await using var restarted = Create();
        await restarted.Ready;
        Assert.Equal(id, Assert.Single(restarted.Snapshot()).CallId);
        Assert.Contains("{broken-json", File.ReadAllText(path + ".corrupt"));
        restarted.Record(Call());
        await restarted.FlushAsync();
        Assert.Equal(2, restarted.Snapshot().Count);
    }

    [Fact]
    public async Task UnreadableSegmentDoesNotPreventFreshRecording()
    {
        Directory.CreateDirectory(LedgerRoot);
        var path = Path.Combine(LedgerRoot, "ledger-00000000000000000001.jsonl");
        File.WriteAllText(path, "unreadable");
        await using var ledger = Create();
        await ledger.Ready;
        Assert.Empty(ledger.Snapshot());
        Assert.Equal("unreadable", File.ReadAllText(path + ".corrupt"));
        ledger.Record(Call());
        await ledger.FlushAsync();
        Assert.Single(ledger.Snapshot());
    }

    [Fact]
    public async Task HandEditedStoredArgumentsAndIdentityAreNotReturnedAsSecrets()
    {
        await using (var first = Create()) { first.Record(Call()); await first.FlushAsync(); }
        var path = Assert.Single(Segments());
        var line = File.ReadAllLines(path).Single(value => !string.IsNullOrWhiteSpace(value));
        var row = JsonSerializer.Deserialize<AutomationLedgerEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var changed = row with { Arguments = JsonSerializer.SerializeToElement(new { password = "stored-secret" }),
            Tool = "stored-secret-operation", ClientId = "stored-secret-client" };
        File.WriteAllText(path, JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
        await using var restarted = Create();
        await restarted.Ready;
        var actual = Assert.Single(restarted.Snapshot());
        Assert.DoesNotContain("stored-secret", JsonSerializer.Serialize(actual));
        Assert.Equal("[omitted]", actual.Arguments.GetString());
    }

    [Fact]
    public async Task WriterDoesNotInheritAttributionAndSaturationDropsOldestPendingCalls()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var attribution = new AsyncLocal<string?> { Value = "settings" };
        string? observedAttribution = "not observed";
        await using var ledger = Create(limits: Small with { QueueCapacity = 2 }, beforeWrite: () =>
        {
            observedAttribution = attribution.Value;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        ledger.Record(Call());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var newest = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 10; i++) ledger.Record(Call());
        ledger.Record(Call(newest));
        Assert.True(ledger.Dropped > 0);
        release.Set();
        await ledger.FlushAsync();
        Assert.Null(observedAttribution);
        Assert.Contains(ledger.Snapshot(), row => row.CallId == newest);
    }

    [Fact]
    public async Task DisabledLedgerDoesNotWriteAndCanBeEnabledWithoutRestart()
    {
        var enabled = false;
        await using var ledger = Create(enabled: () => enabled);
        ledger.Record(Call());
        await ledger.FlushAsync();
        Assert.Empty(ledger.Snapshot());
        Assert.False(Directory.Exists(LedgerRoot));
        enabled = true;
        ledger.Record(Call());
        await ledger.FlushAsync();
        Assert.Single(ledger.Snapshot());
    }

    [Fact]
    public void HugeArgumentShapeIsBoundedWithoutKeepingNamesOrValues()
    {
        var input = JsonSerializer.SerializeToElement(Enumerable.Range(0, 1000).ToDictionary(index => "private" + index,
            _ => new { hidden = new string('x', 1000), numeric = 123456789 }));
        var shape = AutomationCallLedger.ArgumentShape(input).GetRawText();
        Assert.True(shape.Length < 4096);
        Assert.DoesNotContain("private", shape); Assert.DoesNotContain("hidden", shape);
        Assert.DoesNotContain("123456789", shape); Assert.DoesNotContain("xxxxx", shape);
    }

    [Fact]
    public async Task ReadOnlyToolReturnsBoundedSafeRowsWithoutWaitingForDisk()
    {
        await using var ledger = Create(beforeWrite: () => throw new IOException());
        var module = new AutomationLedgerAutomation(() => ledger);
        Assert.True(Assert.Single(module.Tools).ReadOnly);
        ledger.Record(Call());
        ledger.Record(Call());
        await ledger.FlushAsync();
        var result = JsonSerializer.SerializeToElement(await module.ExecuteAsync("automation_ledger",
            JsonSerializer.SerializeToElement(new { limit = 1 }), default));
        Assert.Single(result.GetProperty("entries").EnumerateArray());
        Assert.False(result.GetProperty("persistenceHealthy").GetBoolean());
    }

    [Fact]
    public void UiAutomationCannotReachRecoveryExportOrSecretControls()
    {
        var automation = new UiAutomation(() => throw new InvalidOperationException("No desktop required"));
        foreach (var screen in new[] { "recovery", "settings", "credentials" })
            Assert.Throws<ArgumentException>(() => AutomationSchema.Validate(automation.Tools.Single(tool => tool.Name == "ui.invoke").InputSchema,
                JsonSerializer.SerializeToElement(new { screen, command = "Export_Click" })));
        var commands = Assert.IsType<HashSet<string>>(typeof(UiAutomation).GetField("Commands", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Assert.DoesNotContain(commands, command => command.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("Recovery", StringComparison.OrdinalIgnoreCase) || command.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }
}
