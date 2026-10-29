using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class ProblemsServiceTests
{
    [Fact]
    public async Task RefreshAndPreviewNeverSaveAndExplicitApplyIsAttributed()
    {
        var config = HealthDoctorTests.Config();
        config.Logging.Level = "Verbose";
        var saves = 0;
        var refreshes = 0;
        string? actor = null;
        var automation = new ConfigurationAutomation(config, _ => { saves++; actor = ConfigWriteScope.Current.Actor; }, settingsApplied: () => refreshes++);
        using var service = new ProblemsService(automation, HealthDoctorTests.Context);
        var snapshot = await service.RefreshAsync();
        var preview = service.PreviewFix(Assert.Single(snapshot.Problems).Id);
        Assert.Contains("Before: \"Verbose\"", preview.Diff);
        Assert.Contains("After: \"Information\"", preview.Diff);
        Assert.Contains("Changes disk logging immediately", preview.Impact);
        Assert.Contains("refreshes runtime settings for all servers", preview.Impact);
        Assert.Contains("resume due mount retries", preview.Impact);
        Assert.DoesNotContain("no server restart or site traffic", preview.Impact);
        Assert.Equal("Verbose", config.Logging.Level);
        Assert.Equal(0, saves);
        await service.ApplyFixAsync(preview);
        Assert.Equal(1, saves);
        Assert.Equal(1, refreshes);
        Assert.Equal("settings-ui:problems", actor);
        Assert.Equal("Information", config.Logging.Level);
    }

    [Fact]
    public async Task UnrelatedEditInvalidatesWholePreviewRevision()
    {
        var config = HealthDoctorTests.Config();
        config.Logging.Level = "Verbose";
        int saves = 0;
        using var service = new ProblemsService(new(config, _ => saves++), HealthDoctorTests.Context);
        var preview = service.PreviewFix(Assert.Single((await service.RefreshAsync()).Problems).Id);
        config.Downloads.MaxRetries++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyFixAsync(preview));
        Assert.Equal(0, saves);
        Assert.Equal("Verbose", config.Logging.Level);
    }

    [Fact]
    public async Task ClearedRuntimeConditionCannotApplyOldFix()
    {
        var config = HealthDoctorTests.Config();
        config.Servers[0].Irc.Enabled = true;
        config.Servers[0].Irc.AnnounceRules = [new() { Channel = "#feed" }];
        config.Servers[0].Irc.Channels = [new() { Name = "#feed", AutoJoin = false }];
        bool recovered = false;
        DoctorContext Capture(AppConfig candidate) => HealthDoctorTests.Context(candidate) with
        {
            Servers = [new("server-a", null, false, false, "", null, true, recovered ? ["#feed"] : [], [])]
        };
        int saves = 0;
        using var service = new ProblemsService(new(config, _ => saves++), Capture);
        var preview = service.PreviewFix(Assert.Single((await service.RefreshAsync()).Problems).Id);
        recovered = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyFixAsync(preview));
        Assert.Equal(0, saves);
        Assert.False(config.Servers[0].Irc.Channels[0].AutoJoin);
    }

    [Fact]
    public async Task FixPreviewShowsSearchAndIrcRuntimeClassifications()
    {
        var config = HealthDoctorTests.Config();
        config.Servers[0].Search.SearchPaths = ["/incoming"];
        config.Servers[0].SpreadSite.Sections["FILMS"] = "/archive/films";
        using var service = new ProblemsService(new(config, _ => { }), HealthDoctorTests.Context);
        var search = service.PreviewFix(Assert.Single((await service.RefreshAsync()).Problems).Id);
        Assert.Contains("immediately rebuilds the whole index", search.Impact);
        Assert.Contains("Example: search.searchPaths; remount: no; IRC restart: no", search.Impact);
        Assert.Contains("resume due mount retries", search.Impact);
        config.Servers[0].SpreadSite.Sections.Clear();
        config.Servers[0].Irc.Enabled = true;
        config.Servers[0].Irc.AnnounceRules = [new() { Channel = "#feed" }];
        config.Servers[0].Irc.Channels = [new() { Name = "#feed", AutoJoin = false }];
        var irc = service.PreviewFix(Assert.Single((await service.RefreshAsync()).Problems).Id);
        Assert.Contains("irc.channels; remount: no; IRC restart: yes", irc.Impact);
        Assert.Contains("SITE INVITE", irc.Impact);
    }

    [Fact]
    public async Task FailedCheckRetainsSnapshotAndDoesNotGateOrSave()
    {
        var config = HealthDoctorTests.Config();
        config.Logging.Level = "Verbose";
        bool fail = false;
        using var service = new ProblemsService(new(config, _ => throw new Xunit.Sdk.XunitException("Unexpected save")),
            c => fail ? throw new InvalidOperationException() : HealthDoctorTests.Context(c));
        var before = await service.RefreshAsync();
        fail = true;
        var after = await service.RefreshAsync();
        Assert.Equal(before.Problems, after.Problems);
        Assert.True(after.Partial);
    }

    [Fact]
    public async Task PublicSnapshotsAreRedactedAndMcpIsReadOnly()
    {
        var config = HealthDoctorTests.Config();
        using var service = new ProblemsService(new(config, _ => throw new Xunit.Sdk.XunitException("Unexpected save")),
            c => HealthDoctorTests.Context(c) with { FrozenDecisions = [new("id", "/example", "now", "credential-secret")] },
            () => text => text.Replace("credential-secret", "[redacted]"));
        await service.RefreshAsync();
        var module = new ProblemAutomation(() => service);
        Assert.All(module.Tools, tool => Assert.True(tool.ReadOnly));
        var result = await module.ExecuteAsync("problems.list", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("credential-secret", json);
        Assert.DoesNotContain("Operations", json);
        Assert.DoesNotContain("Before", json);
        Assert.Contains("[redacted]", json);
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("problems.apply", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
    }

    [Fact]
    public void OversizedEvidenceCannotLeakCredentialPrefixAcrossTruncationBoundary()
    {
        var secret = "credential-secret-that-crosses-boundary";
        var raw = new string('x', 4080) + secret;
        var safe = ProblemsService.RedactField(raw, text => text.Replace(secret, "[redacted]"));
        Assert.DoesNotContain("credential", safe);
        Assert.Empty(safe);
        Assert.Equal("[redacted]", ProblemsService.RedactField(secret, text => text.Replace(secret, "[redacted]")));
    }

    [Fact]
    public void FrozenRowsIgnoreMalformedOptionalPayloadAndResolvedTargets()
    {
        var text = """
            {"Id":"one","Target":"/logging/level","RejectionReason":"frozen","Applied":false,"ExpectedEffect":42,"Ts":"earlier"}
            malformed row
            {"Id":"two","Target":"/downloads/maxRetries","RejectionReason":"frozen","Applied":false,"Ts":"earlier"}
            {"Id":"three","Target":"/downloads/maxRetries","Applied":true,"Ts":"later"}
            """;
        var result = FrozenDoctorReader.Parse(text,
            [new("/logging", "then", "keep"), new("/downloads", "then", "keep")], HealthDoctorTests.Config());
        Assert.True(result.Partial);
        Assert.Equal("one", Assert.Single(result.Decisions).Id);
    }

    [Fact]
    public void FrozenRowsRequireCurrentFreezeAndCanonicalizeServerIndex()
    {
        var text = """{"Id":"one","Target":"/servers/0/spreadSite/priority","RejectionReason":"frozen","Applied":false,"Ts":"earlier"}""";
        var config = HealthDoctorTests.Config();
        Assert.Empty(FrozenDoctorReader.Parse(text, [], config).Decisions);
        var rows = FrozenDoctorReader.Parse(text, [new("/servers/server-a/spread/sitePriority", "then", "keep")], config).Decisions;
        Assert.Equal("/servers/server-a/spread/sitePriority", Assert.Single(rows).Target);
        Assert.Single(FrozenDoctorReader.Parse(text, [new("/servers/0/spreadSite/priority", "then", "keep")], config).Decisions);
        Assert.True(FrozenDoctorReader.Affects("/servers/0/search/searchPaths/0", "/servers/server-a/search/searchPaths", config));
    }

    [Fact]
    public void NormalAuditWindowIsBoundedWithoutReportingUnavailableChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "doctor-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var row = JsonSerializer.Serialize(new { Id = "one", Target = "/logging/level", RejectionReason = "frozen", Applied = false,
                Ts = "earlier", ExpectedEffect = new string('x', 300) });
            File.WriteAllLines(path, Enumerable.Repeat(row, 1200));
            var result = FrozenDoctorReader.Read(path, [new("/logging", "then", "keep")], HealthDoctorTests.Config());
            Assert.True(result.Bounded);
            Assert.False(result.Partial);
            Assert.Single(result.Decisions);
            var snapshot = HealthDoctor.Evaluate(HealthDoctorTests.Context(HealthDoctorTests.Config()) with
                { FrozenDecisions = result.Decisions, Partial = result.Partial, Bounded = result.Bounded }, DateTimeOffset.UtcNow);
            Assert.True(snapshot.Bounded);
            Assert.False(snapshot.Partial);
            foreach (var applied in new[] { false, true })
            {
                Assert.Contains("recent audit history only", snapshot.StatusText(applied));
                Assert.DoesNotContain("unavailable", snapshot.StatusText(applied));
                Assert.DoesNotContain("refresh to try again", snapshot.StatusText(applied));
            }
            File.AppendAllText(path, "torn-row");
            Assert.True(FrozenDoctorReader.Read(path, [new("/logging", "then", "keep")], HealthDoctorTests.Config()).Partial);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FrozenDecisionResultCapStillReportsIncompleteEvidence()
    {
        var rows = Enumerable.Range(0, 65).Select(index => JsonSerializer.Serialize(new
            { Id = "row-" + index, Target = "/logging/entry" + index, RejectionReason = "frozen", Applied = false, Ts = "earlier" }));
        var result = FrozenDoctorReader.Parse(string.Join("\n", rows), [new("/logging", "then", "keep")], HealthDoctorTests.Config());
        Assert.True(result.Partial);
        Assert.Equal(64, result.Decisions.Count);
    }

    [Fact]
    public async Task MaskedDiffNeverProducesAnActionablePreview()
    {
        var config = HealthDoctorTests.Config();
        config.Logging.Level = "Verbose";
        using var service = new ProblemsService(new(config, _ => throw new Xunit.Sdk.XunitException("Unexpected save")),
            HealthDoctorTests.Context, () => _ => "[redaction unavailable]");
        var row = Assert.Single((await service.RefreshAsync()).Problems);
        Assert.Throws<InvalidOperationException>(() => service.PreviewFix(row.Id));
    }

    [Fact]
    public async Task RecurringChecksDoNotInheritRequestAttribution()
    {
        var config = HealthDoctorTests.Config();
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = ConfigWriteScope.Begin("mcp:synthetic-request");
        using var service = new ProblemsService(new(config, _ => { }), c =>
        {
            observed.TrySetResult(ConfigWriteScope.Current.Actor);
            return HealthDoctorTests.Context(c);
        }, start: true, initialDelay: TimeSpan.Zero);
        Assert.Equal("application", await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FailedSaveLeavesExistingLoggingChoice()
    {
        var config = HealthDoctorTests.Config();
        config.Logging.Level = "Debug";
        using var service = new ProblemsService(new(config, _ => throw new System.IO.IOException("read only")), HealthDoctorTests.Context);
        var preview = service.PreviewFix(Assert.Single((await service.RefreshAsync()).Problems).Id);
        await Assert.ThrowsAsync<System.IO.IOException>(() => service.ApplyFixAsync(preview));
        Assert.Equal("Debug", config.Logging.Level);
    }
}
