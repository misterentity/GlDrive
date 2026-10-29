using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Logging;
using GlDrive.Services.Automation;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Flight recorder logger", DisableParallelization = true)]
public sealed class FlightRecorderCollection;

[Collection("Flight recorder logger")]
public sealed class FlightRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-recorder-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    public FlightRecorderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private RecordedLog Row(string text, int seconds = 0, string correlation = "") => new(_now.AddSeconds(seconds), LogEventLevel.Debug, "FTP", correlation, "Template", text);
    private static LogEvent Event(DateTimeOffset time, string text, LogEventLevel level = LogEventLevel.Debug, string template = "{Message}")
        => new(time, level, null, new MessageTemplateParser().Parse(template), [new LogEventProperty("Message", new ScalarValue(text))]);
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }
    private static async Task Stop(FlightRecorder recorder) { recorder.Dispose(); await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(6)); }

    [Fact]
    public void RingEvictsOldestByCountAgeAndBytesAndFilters()
    {
        var ring = new LogRing(2000, 2, TimeSpan.FromSeconds(10));
        ring.Add(Row("first")); ring.Add(Row("second", 1)); ring.Add(Row("third", 2));
        Assert.Equal(new[] { "second", "third" }, ring.Snapshot(_now.AddSeconds(3)).Select(x => x.Text));
        Assert.Single(ring.Snapshot(_now.AddSeconds(3), component: "FTP", text: "third"));
        Assert.Empty(ring.Snapshot(_now.AddSeconds(3), minimum: LogEventLevel.Error));
        Assert.Empty(ring.Snapshot(_now.AddSeconds(13)));
        for (var i = 0; i < 20; i++) ring.Add(Row(new string('x', 700), 20 + i));
        Assert.InRange(ring.Bytes, 0, 2000);
        Assert.Single(ring.Snapshot(_now.AddSeconds(39)));
    }

    [Fact]
    public void ComponentTraceExpiresExactlyAndDoesNotElevateOthers()
    {
        var traces = new ComponentTraceSwitches();
        Assert.Equal(_now.AddMinutes(15), traces.Enable("FTP", TimeSpan.FromMinutes(15), _now));
        Assert.True(traces.IsEnabled("FTP", _now.AddMinutes(14)));
        Assert.False(traces.IsEnabled("IRC", _now));
        Assert.False(traces.IsEnabled("FTP", _now.AddMinutes(15)));
        Assert.Equal(_now.AddHours(2), traces.Enable("IRC", TimeSpan.FromDays(100), _now));
        Assert.Throws<ArgumentException>(() => traces.Enable("arbitrary", TimeSpan.FromMinutes(5), _now));
    }

    [Fact]
    public void RateSequenceAndAbsenceAreBoundedCorrelatedAndRearm()
    {
        var rules = new LogSignatureRules([
            new("rate", "rate", "bad", "", 2, 10),
            new("sequence", "sequence", "first", "second", 1, 10, true),
            new("absence", "absence", "start", "finish", 1, 10, true)]);
        Assert.Empty(rules.Observe(Row("bad")));
        Assert.Single(rules.Observe(Row("bad", 1)));
        Assert.Empty(rules.Observe(Row("bad", 2)));
        rules.Observe(Row("first", 0, "one"));
        rules.Observe(Row("first", 20, "one"));
        Assert.Empty(rules.Observe(Row("second", 21, "two")));
        Assert.Single(rules.Observe(Row("second", 22, "one")));
        rules.Observe(Row("start", 0, "one")); rules.Observe(Row("finish", 9, "one"));
        Assert.Empty(rules.Tick(_now.AddSeconds(10)));
        rules.Observe(Row("start", 20, "one"));
        Assert.Single(rules.Tick(_now.AddSeconds(30)));
        Assert.Empty(rules.Tick(_now.AddSeconds(31)));
    }

    [Fact]
    public void ShippedRulesMatchRealGenericStallTemplates()
    {
        var rules = LogSignatureRules.Shipped();
        Assert.Single(rules.Observe(Row("GnuTLS: native I/O did not drain within 30s")));
        Assert.Single(rules.Observe(Row("Download lane: local disk write took 10s")));
        Assert.Single(rules.Observe(Row("Spread stalled: Synthetic.Release")));
        rules.Observe(Row("Initial server mounts started"));
        Assert.Single(rules.Tick(_now.AddMinutes(10)));
    }

    [Fact]
    public async Task ShippedRemountTellMatchesActualTemplatesWithoutSharedCorrelation()
    {
        var recorder = new FlightRecorder(_root, () => value => value, () => false);
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(recorder).CreateLogger();
        logger.Information("Configuration for {ServerName}: remounting: {Properties}", "Synthetic site", "connection.port");
        logger.Warning(new ObjectDisposedException("Synthetic pool"), "FXP transfer error: {File} ({Src} -> {Dst})", "part.rar", "source-id", "destination-id");
        await WaitFor(() => recorder.SignatureSnapshot().Any(hit => hit.Id == "remount-disposed"));
        Assert.Single(recorder.SignatureSnapshot(), hit => hit.Id == "remount-disposed");
        await Stop(recorder);
    }

    [Fact]
    public async Task FullRecorderBundleRedactsRowsWithoutMaskingTheWholeFile()
    {
        var diagnostics = new DiagnosticAutomation(new AppConfig { ControlApi = new() { Token = "synthetic-export-secret" } }, _root);
        var redact = diagnostics.CreateTextRedactor();
        var recorder = new FlightRecorder(_root, () => redact, () => false);
        try
        {
            for (var batch = 0; batch < 16; batch++)
            {
                for (var row = 0; row < 512; row++)
                    recorder.Emit(Event(_now, $"evidence-{batch * 512 + row}\ntrace", row == 511 ? LogEventLevel.Error : LogEventLevel.Debug));
                var expected = (batch + 1) * 512;
                await WaitFor(() => recorder.Snapshot(limit: 8192).Length == expected);
            }
            var zip = Path.Combine(_root, "full.zip");
            var result = await recorder.ExportBundleAsync(zip, new());
            Assert.Equal(0, result.MaskedRows);
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("recorder.log")!.Open());
            var text = reader.ReadToEnd();
            Assert.Contains("evidence-0\ntrace", text);
            Assert.Contains("evidence-8191\ntrace", text);
            Assert.True(text.Split('\n').Length > 8192);
        }
        finally { await Stop(recorder); }
    }

    [Fact]
    public async Task BundleReportsMaskedRowsWhileRetainingOtherEvidence()
    {
        var recorder = new FlightRecorder(_root, () => text => text.Contains("unavailable-row", StringComparison.Ordinal) ? "[redaction unavailable]" : text, () => false);
        recorder.Emit(Event(_now, "unavailable-row"));
        recorder.Emit(Event(_now, "retained evidence"));
        await Stop(recorder);
        var zip = Path.Combine(_root, "partial.zip");
        var result = await recorder.ExportBundleAsync(zip, new());
        Assert.Equal(1, result.MaskedRows);
        using var archive = ZipFile.OpenRead(zip);
        using var evidence = new StreamReader(archive.GetEntry("recorder.log")!.Open());
        Assert.Contains("retained evidence", evidence.ReadToEnd());
        using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal(1, JsonNode.Parse(manifest.ReadToEnd())!["maskedRows"]!.GetValue<int>());
    }

    [Fact]
    public async Task IdleRecorderDoesNotBuildASecretInventory()
    {
        var builds = 0;
        var recorder = new FlightRecorder(_root, () => { Interlocked.Increment(ref builds); return text => text; }, () => false);
        await Task.Delay(1200);
        Assert.Equal(0, Volatile.Read(ref builds));
        recorder.Emit(Event(_now, "first evidence"));
        await Stop(recorder);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task RecorderRedactsBeforeRingAndAgainBeforeCapsuleAndBundle()
    {
        var secrets = new List<string> { "synthetic-fish-key", "control-token" };
        Func<Func<string, string>> factory = () => { var snapshot = secrets.ToArray(); return text => OutboundPayloadRedactor.PrepareText(text, snapshot); };
        var recorder = new FlightRecorder(_root, factory, () => false);
        recorder.Emit(Event(_now, "synthetic-fish-key control-token later-secret"));
        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.DoesNotContain("synthetic-fish-key", recorder.Snapshot()[0].Text);
        secrets.Add("later-secret");
        var capture = await recorder.CaptureAsync("manual");
        Assert.NotNull(capture);
        Assert.DoesNotContain("later-secret", File.ReadAllText(capture));
        Assert.Null(await recorder.CaptureAsync("again"));
        var zip = Path.Combine(_root, "bundle.zip");
        await recorder.ExportBundleAsync(zip, new AppConfig { ControlApi = new() { Token = "control-token" } });
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd();
                Assert.DoesNotContain("synthetic-fish-key", text); Assert.DoesNotContain("control-token", text); Assert.DoesNotContain("later-secret", text);
                Assert.True(entry.Length <= FlightRecorder.CapsuleBytes);
            }
        Assert.True(new FileInfo(capture).Length <= FlightRecorder.CapsuleBytes);
        await Stop(recorder);
    }

    [Fact]
    public async Task ErrorCapturesAutomaticallyButDisabledCaptureDoesNot()
    {
        var recorder = new FlightRecorder(_root, () => text => text);
        recorder.Emit(Event(_now, "failure", LogEventLevel.Error));
        await WaitFor(() => Directory.Exists(Path.Combine(_root, "logs", "capsules")) && Directory.GetFiles(Path.Combine(_root, "logs", "capsules")).Length == 1);
        await Stop(recorder);
        var disabledRoot = Path.Combine(_root, "disabled");
        var disabled = new FlightRecorder(disabledRoot, () => text => text, () => false);
        disabled.Emit(Event(_now, "failure", LogEventLevel.Error));
        await WaitFor(() => disabled.Snapshot().Length == 1);
        Assert.False(Directory.Exists(Path.Combine(disabledRoot, "logs", "capsules")));
        await Stop(disabled);
    }

    [Fact]
    public async Task OversizedMessagesAreOmittedWholeAndBacklogIsBounded()
    {
        using var paused = new ManualResetEventSlim();
        var recorder = new FlightRecorder(_root, () => { paused.Wait(TimeSpan.FromSeconds(6)); return text => text; }, () => false);
        for (var i = 0; i < FlightRecorder.QueueCapacity * 2; i++) recorder.Emit(Event(_now, "entry " + i));
        recorder.Emit(Event(_now, new string('s', FlightRecorder.MaxEntryChars + 1)));
        Assert.True(recorder.Dropped >= FlightRecorder.QueueCapacity);
        paused.Set();
        await WaitFor(() => recorder.Snapshot(limit: 8192).Any(x => x.Text.Contains("omitted")));
        Assert.DoesNotContain(recorder.Snapshot(limit: 8192), x => x.Text.Contains(new string('s', 100)));
        Assert.True(recorder.Snapshot(limit: 8192).Length <= FlightRecorder.QueueCapacity);
        await Stop(recorder);
    }

    [Fact]
    public async Task InventoryFailureMasksWholeRowsAndDoesNotStopWorker()
    {
        var fail = true;
        var recorder = new FlightRecorder(_root, () => fail ? throw new IOException() : text => text, () => false);
        recorder.Emit(Event(_now, "unlabelled-private-value"));
        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.Equal("[redaction unavailable]", recorder.Snapshot()[0].Text);
        fail = false; recorder.Emit(Event(_now, "recovered"));
        await WaitFor(() => recorder.Snapshot().Any(r => r.Text.Contains("recovered")));
        await Stop(recorder);
    }

    [Fact]
    public async Task CensusMergesRestartAndLateHourAndPreservesCorruptBucket()
    {
        var now = _now;
        async Task Record(DateTimeOffset at, int count)
        {
            var recorder = new FlightRecorder(_root, () => text => text, () => false, () => now);
            for (var i = 0; i < count; i++) recorder.Emit(Event(at, "value"));
            await Stop(recorder);
        }
        await Record(now, 2); await Record(now, 3);
        var file = Path.Combine(_root, "logs", "census", $"census-{now.UtcDateTime:yyyyMMdd-HH}.json");
        Assert.Equal(5, JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file))!.Values.Sum());
        now = now.AddHours(1);
        await Record(_now, 1);
        Assert.Equal(6, JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file))!.Values.Sum());
        File.WriteAllText(file, "invalid"); await Record(_now, 1);
        Assert.Equal("invalid", File.ReadAllText(file + ".corrupt"));
        Assert.Equal(1, JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file))!.Values.Sum());
    }

    [Fact]
    public async Task FailedPersistenceKeepsRingAndLaterEventsFlow()
    {
        File.WriteAllText(Path.Combine(_root, "logs"), "directory blocker");
        var recorder = new FlightRecorder(_root, () => text => text);
        recorder.Emit(Event(_now, "first", LogEventLevel.Error));
        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.Null(await recorder.CaptureAsync("manual"));
        recorder.Emit(Event(_now, "second"));
        await WaitFor(() => recorder.Snapshot().Length == 2);
        await Stop(recorder);
    }

    [Fact]
    public void PruningEvictsOldestWithoutRefusingNewFiles()
    {
        for (var i = 0; i < 5; i++)
        { var path = Path.Combine(_root, $"incident-{i}.log"); File.WriteAllText(path, new string('x', 100)); File.SetLastWriteTimeUtc(path, _now.AddSeconds(i).UtcDateTime); }
        FlightRecorder.Prune(_root, "incident-*.log", 3, 200, _now.AddDays(-30));
        Assert.Equal(new[] { "incident-3.log", "incident-4.log" }, Directory.GetFiles(_root).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void DefaultsExplicitLoggingAndMalformedOptionalFlagRemainCompatible()
    {
        Assert.Equal("Information", new LoggingConfig().Level); Assert.Equal(30, new LoggingConfig().RetainedFiles);
        var explicitConfig = JsonSerializer.Deserialize<AppConfig>("""{"logging":{"level":"Verbose","retainedFiles":7,"incidentCaptureEnabled":{"draft":true}},"servers":[{"name":"Synthetic server"}]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal("Verbose", explicitConfig.Logging.Level); Assert.Equal(7, explicitConfig.Logging.RetainedFiles);
        Assert.False(explicitConfig.Logging.IncidentCaptureEnabled); Assert.Single(explicitConfig.Servers);
        Assert.Equal(30, SerilogSetup.RetainedFileCount(new()));
        Assert.True(SerilogSetup.RetainedFileCount(new() { MaxFileSizeMb = int.MaxValue, RetainedFiles = int.MaxValue }) > 0);
        var automation = new ConfigurationAutomation(new(), _ => { });
        Assert.Throws<ArgumentException>(() => automation.Apply(JsonNode.Parse("""[{"op":"replace","path":"/logging/incidentCaptureEnabled","value":"invalid"}]""")!.AsArray()));
    }

    [Fact]
    public void DiagnosticRedactorRemovesFishEscapesAndActualIrcProtocolPrefixes()
    {
        File.WriteAllText(Path.Combine(_root, "fish-keys-test.json"), """{"#synthetic":{"key":"synthetic-fish","altKey":"alternate-secret","dhSecretHex":"derived-secret"}}""");
        var diagnostics = new DiagnosticAutomation(new AppConfig { ControlApi = new() { Token = "quoted\"token" } }, _root);
        var redact = diagnostics.CreateTextRedactor();
        foreach (var secret in new[] { "synthetic-fish", "alternate-secret", "derived-secret", "quoted\\\"token" }) Assert.DoesNotContain(secret, redact(secret));
        foreach (var line in new[] { "[DBG] [IRC >] JOIN #synthetic unknown-key", "[VRB] [IRC <] :server MODE #synthetic +k unknown-key", "[IRC >] NICKSERV IDENTIFY unknown-key", "[FTP] Command: PASS unknown-key" })
            Assert.DoesNotContain("unknown-key", redact(line));
    }

    [Fact]
    public async Task ActualDiskSinkPreservesVerboseAndComponentTraceExpires()
    {
        var priorRoot = ConfigManager.AppDataPathOverride;
        var priorLogger = Log.Logger;
        try
        {
            ConfigManager.AppDataPathOverride = _root;
            var config = new LoggingConfig { Level = "Verbose", RetainedFiles = 7, IncidentCaptureEnabled = false };
            SerilogSetup.Configure(config);
            Log.Verbose("explicit verbose retained");
            SerilogSetup.SetLevel("Information");
            Log.Debug("ordinary debug suppressed");
            SerilogSetup.Traces.Enable("FTP", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
            Log.Debug("[FTP] {Message}", "component trace visible");
            await Task.Delay(1100);
            Log.Debug("[FTP] {Message}", "component trace expired");
            Log.CloseAndFlush();
            await SerilogSetup.Recorder!.Completion.WaitAsync(TimeSpan.FromSeconds(6));
            var text = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "logs"), "gldrive-*.log").Select(File.ReadAllText));
            Assert.Contains("explicit verbose retained", text); Assert.Contains("component trace visible", text);
            Assert.DoesNotContain("ordinary debug suppressed", text); Assert.DoesNotContain("component trace expired", text);
            Assert.Equal("Verbose", config.Level); Assert.Equal(7, config.RetainedFiles);
        }
        finally { Log.Logger = priorLogger; ConfigManager.AppDataPathOverride = priorRoot; }
    }

    [Fact]
    public void RetentionFailuresCannotAdmitAnotherDiagnosticFile()
    {
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(_root, $"incident-{i}.log"), new string('x', 100));
        Assert.False(FlightRecorder.MakeRoom(_root, "incident-*.log", 3, 300, 100, delete: _ => throw new IOException("locked")));
        var locked = Path.Combine(_root, "incident-0.log");
        Assert.True(FlightRecorder.MakeRoom(_root, "incident-*.log", 3, 300, 100, delete: path =>
        { if (path == locked) throw new IOException("locked"); File.Delete(path); }));
        Assert.True(Directory.GetFiles(_root).Length <= 2);
    }

    [Fact]
    public async Task RedactionExpansionKeepsCensusAndCapsulesBoundedAndReadable()
    {
        var recorder = new FlightRecorder(_root, () => text => text.Replace("zz", new string('r', 100), StringComparison.Ordinal), () => false);
        recorder.Emit(Event(_now, string.Concat(Enumerable.Repeat("zz", 1000)), template: string.Concat(Enumerable.Repeat("zz", 500))));
        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.Equal("[expanded template omitted]", recorder.Snapshot()[0].Template);
        Assert.Equal("[expanded log entry omitted]", recorder.Snapshot()[0].Text);
        var captured = await recorder.CaptureAsync("manual");
        Assert.NotNull(captured); Assert.True(new FileInfo(captured).Length <= FlightRecorder.CapsuleBytes);
        await Stop(recorder);
        var census = Directory.GetFiles(Path.Combine(_root, "logs", "census"), "*.json").Single();
        Assert.True(new FileInfo(census).Length <= FlightRecorder.CapsuleBytes);
        var restarted = new FlightRecorder(_root, () => text => text, () => false);
        restarted.Emit(Event(_now, "value")); await Stop(restarted);
        Assert.False(File.Exists(census + ".corrupt"));
    }

    [Fact]
    public async Task CapsuleRateLimitSurvivesRestartAndFatalDisposeDrains()
    {
        var now = _now;
        var recorder = new FlightRecorder(_root, () => text => text, now: () => now);
        recorder.Emit(Event(now, "fatal evidence", LogEventLevel.Fatal));
        await Stop(recorder);
        var first = Directory.GetFiles(Path.Combine(_root, "logs", "capsules"), "incident-*.log").Single();
        Assert.Contains("fatal evidence", File.ReadAllText(first));
        var restarted = new FlightRecorder(_root, () => text => text, now: () => now);
        Assert.Null(await restarted.CaptureAsync("manual"));
        now = now.AddMinutes(6);
        Assert.NotNull(await restarted.CaptureAsync("manual"));
        await Stop(restarted);
    }

    [Fact]
    public async Task ShutdownDrainsLateFatalBeyondTheCurrentBatch()
    {
        using var blocked = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var recorder = new FlightRecorder(_root, () => text =>
        {
            if (text.Contains("batch blocker", StringComparison.Ordinal))
            { blocked.Set(); resume.Wait(TimeSpan.FromSeconds(6)); }
            return text;
        });
        recorder.Emit(Event(_now, "batch blocker"));
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(6)));
        for (var i = 0; i < FlightRecorder.QueueCapacity - 1; i++) recorder.Emit(Event(_now, "ordinary " + i));
        recorder.Emit(Event(_now, "late fatal evidence", LogEventLevel.Fatal));
        var stop = Task.Run(() => recorder.Dispose());
        await Task.Delay(20);
        resume.Set();
        await stop;
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(6));
        var capture = Directory.GetFiles(Path.Combine(_root, "logs", "capsules"), "incident-*.log").Single();
        Assert.Contains("late fatal evidence", File.ReadAllText(capture));
    }

    [Fact]
    public void ShortSecretsNeverRescanMarkersAndBoundsReturnWholeMask()
    {
        var secrets = new[] { "redacted", "hidden", "a", "r", "e", "d", "c", "t", "h", "i", "n" };
        var redact = DiagnosticTextRedactor.Create(secrets);
        var text = redact(new string('a', 1000) + " XYZ");
        Assert.Equal(new string('█', 1000) + " XYZ", text);
        foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
        Assert.Equal("█", redact(new string('a', DiagnosticTextRedactor.MaxInputChars + 1)));
        Assert.Equal("", DiagnosticTextRedactor.Create([new string('q', 65537)])("private input"));
        Assert.Equal("", DiagnosticTextRedactor.Create(Enumerable.Range(0, 4097).Select(i => "value-" + i))("private input"));
        var expanded = DiagnosticTextRedactor.Create(["x"])(new string('x', 300000));
        Assert.True(expanded.Length <= DiagnosticTextRedactor.MaxOutputChars);
        Assert.DoesNotContain("x", expanded);
    }

    [Theory]
    [InlineData("I", "[DBG] [IRC >] JOIN #synthetic old-unknown-value")]
    [InlineData("k", "token=old-unknown-value")]
    [InlineData("P", "[FTP] Command: PASS old-unknown-value")]
    public void ProtocolMaskingPrecedesShortKnownValues(string secret, string message)
    {
        var redact = new DiagnosticAutomation(new AppConfig { ControlApi = new() { Token = secret } }, _root).CreateTextRedactor();
        Assert.DoesNotContain("old-unknown-value", redact(message));
    }

    [Theory]
    [InlineData("alpha beta", "password=\"alpha beta\"")]
    [InlineData("alpha  beta", "unlabelled alpha  beta suffix")]
    [InlineData("alpha  beta", "[IRC >] PRIVMSG bot :identify alpha  beta")]
    public void StructuralMaskingCannotLeaveKnownCredentialSuffixes(string secret, string message)
    {
        var redact = new DiagnosticAutomation(new AppConfig { ControlApi = new() { Token = secret } }, _root).CreateTextRedactor();
        var safe = redact(message);
        Assert.DoesNotContain("alpha", safe); Assert.DoesNotContain("beta", safe);
    }

    [Fact]
    public void DiagnosticInventoryBoundsFileReadsAndCredentialEnumeration()
    {
        var config = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(config, "{}");
        var traversed = 0;
        IEnumerable<string> Many()
        {
            while (true) { traversed++; yield return "x"; }
        }
        Assert.Throws<InvalidDataException>(() => OutboundSecretInventory.CollectForDiagnostics(config, _root, Many));
        Assert.Equal(8193, traversed);
        File.WriteAllBytes(config, new byte[2 * 1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => OutboundSecretInventory.CollectForDiagnostics(config, _root, () => []));
        File.WriteAllText(config, "{}");
        File.WriteAllBytes(Path.Combine(_root, "fish-keys-test.json"), new byte[1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => OutboundSecretInventory.CollectForDiagnostics(config, _root, () => []));
    }

    [Theory]
    [InlineData("password=\"alpha beta\"")]
    [InlineData("token='alpha beta'")]
    [InlineData("password=alpha beta")]
    [InlineData("password=\"alpha \\\"quoted\\\" beta\"")]
    [InlineData("password=\"alpha beta")]
    public void UnknownHistoricalCredentialAssignmentsMaskTheWholeValue(string message)
    {
        var safe = new DiagnosticAutomation(new(), _root).RedactText(message);
        Assert.DoesNotContain("alpha", safe); Assert.DoesNotContain("beta", safe);
    }

    [Fact]
    public void ProtocolMaskingPreservesStructurallyRedactedBundleJson()
    {
        var safe = new DiagnosticAutomation(new(), _root).RedactText("""{"controlApi":{"token":"[redacted]"},"logging":{"level":"Information"}}""");
        var json = JsonNode.Parse(safe)!;
        Assert.Equal("Information", json["logging"]!["level"]!.GetValue<string>());
        Assert.Equal("[redacted]", json["controlApi"]!["token"]!.GetValue<string>());
    }
}
