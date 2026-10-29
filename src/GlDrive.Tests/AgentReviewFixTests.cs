using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentReviewFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-agent-review-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, Json);
    private static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(Serialize(config), Json)!;

    public AgentReviewFixTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Agent_commit_publishes_into_existing_references_and_survives_unrelated_settings_save()
    {
        var live = new AppConfig();
        live.Servers.Add(new ServerConfig { Id = "site" });
        var site = live.Servers[0];
        var agent = live.Agent;
        var settings = new SettingsViewModel(live, readLocalState: false);
        var baseline = Serialize(live);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, baseline);
        var candidate = Clone(live);
        candidate.Agent.DryRunsRemaining--;
        candidate.Spread.SpreadPoolSize = 7;
        candidate.Spread.MaxConcurrentRaces = 2;
        candidate.Servers[0].SpreadSite.DownloadOnly = true;

        AgentConfigCommit.CommitAndPublish(path, baseline, candidate, [], _root,
            config => File.WriteAllText(path, ConfigManager.PrepareSerializedSettings(Serialize(config), baseline)), new AuditTrail(_root), live);

        Assert.Same(site, live.Servers[0]);
        Assert.Same(agent, live.Agent);
        Assert.True(site.SpreadSite.DownloadOnly);
        settings.ApplyAgentTunableSpreadSettings(live);
        live.Logging.Level = "Debug";
        File.WriteAllText(path, Serialize(live));
        var saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json)!;
        Assert.Equal(7, saved.Spread.SpreadPoolSize);
        Assert.Equal(2, saved.Spread.MaxConcurrentRaces);
        Assert.Equal(candidate.Agent.DryRunsRemaining, saved.Agent.DryRunsRemaining);
        Assert.True(saved.Servers[0].SpreadSite.DownloadOnly);
        Assert.Equal("Debug", saved.Logging.Level);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_or_suppressed_disk_save_does_not_publish_candidate(bool throws)
    {
        var live = new AppConfig();
        var baseline = Serialize(live);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, baseline);
        var candidate = Clone(live);
        candidate.Spread.SpreadPoolSize++;
        Assert.Throws<IOException>(() => AgentConfigCommit.CommitAndPublish(path, baseline, candidate, [], _root,
            _ => { if (throws) throw new IOException("synthetic disk failure"); }, new AuditTrail(_root), live));
        Assert.Equal(baseline, Serialize(live));
    }

    [Fact]
    public void Unsaved_runtime_edit_rejects_agent_commit_without_overwriting_either_side()
    {
        var live = new AppConfig();
        var baseline = Serialize(live);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, baseline);
        var candidate = Clone(live);
        candidate.Spread.SpreadPoolSize++;
        live.Logging.Level = "Debug";
        var saved = false;
        Assert.Throws<IOException>(() => AgentConfigCommit.CommitAndPublish(path, baseline, candidate, [], _root,
            _ => saved = true, new AuditTrail(_root), live));
        Assert.False(saved);
        Assert.Equal("Debug", live.Logging.Level);
        Assert.Equal(baseline, File.ReadAllText(path));
    }

    [Fact]
    public void Detached_server_editor_merges_unrelated_runtime_changes_and_rejects_conflicts()
    {
        var baseline = new ServerConfig { Id = "site", Name = "Original" };
        var edited = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(baseline))!;
        var current = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(baseline))!;
        edited.Name = "Renamed";
        current.SpreadSite.DownloadOnly = true;
        var merged = ConfigEditMerge.Merge(baseline, edited, current);
        Assert.Equal("Renamed", merged.Name);
        Assert.True(merged.SpreadSite.DownloadOnly);
        current.Name = "Concurrent rename";
        Assert.Throws<InvalidOperationException>(() => ConfigEditMerge.Merge(baseline, edited, current));
    }

    [Fact]
    public void Publication_retains_unchanged_collections_and_does_not_invalidate_active_enumerators()
    {
        var live = new AppConfig();
        live.Servers.Add(new ServerConfig { Id = "one" });
        live.Servers.Add(new ServerConfig { Id = "two" });
        live.Servers[0].SpreadSite.Sections["TV"] = "/tv";
        live.Servers[0].SpreadSite.Sections["MOVIES"] = "/movies";
        var servers = live.Servers;
        var sections = live.Servers[0].SpreadSite.Sections;
        ConfigReconciler.Apply(live, Clone(live));
        Assert.Same(servers, live.Servers);
        Assert.Same(sections, live.Servers[0].SpreadSite.Sections);
        using var serverEnumerator = servers.GetEnumerator();
        using var sectionEnumerator = sections.GetEnumerator();
        Assert.True(serverEnumerator.MoveNext());
        Assert.True(sectionEnumerator.MoveNext());
        var candidate = Clone(live);
        candidate.Servers.Add(new ServerConfig { Id = "three" });
        candidate.Servers[0].SpreadSite.Sections["MUSIC"] = "/music";
        ConfigReconciler.Apply(live, candidate);
        Assert.NotSame(servers, live.Servers);
        Assert.NotSame(sections, live.Servers[0].SpreadSite.Sections);
        Assert.Same(servers[0], live.Servers[0]);
        Assert.True(serverEnumerator.MoveNext());
        Assert.True(sectionEnumerator.MoveNext());
        Assert.Equal(2, servers.Count);
        Assert.Equal(2, sections.Count);
        Assert.Equal(3, live.Servers.Count);
        Assert.Equal(3, live.Servers[0].SpreadSite.Sections.Count);
    }

    [Fact]
    public void Validated_preview_is_reviewable_and_cannot_be_undone_then_manual_apply_mutates()
    {
        var config = new AppConfig();
        config.Servers.Add(new ServerConfig { Id = "site" });
        var applier = new ChangeApplier([new DownloadOnlyValidator()], new FreezeStore(_root), new AuditTrail(_root), MeasuredWinnerEvidence.ForConfig);
        var proposal = new AgentChange { Category = AgentCategories.DownloadOnly,
            Target = "/servers/site/spread/downloadOnly", Before = false, After = true, Confidence = 1 };
        var rows = new List<AuditRow>();
        applier.Apply([proposal], config, config.Agent, "preview", true, rows.Add, configOnly: true);
        var preview = Assert.Single(rows);
        Assert.True(preview.IsSuggestion);
        Assert.False(preview.WasApplied);
        Assert.Equal("Preview", preview.Status);
        Assert.False(config.Servers[0].SpreadSite.DownloadOnly);
        var attempted = false;
        var undo = AgentUndoCompletion.ApplyInverse(preview, () => { attempted = true; return new() { Applied = 1 }; });
        Assert.False(attempted);
        Assert.Equal(1, undo!.Rejected);
        rows.Clear();
        applier.Apply([proposal], config, config.Agent, "manual", false, rows.Add);
        Assert.True(config.Servers[0].SpreadSite.DownloadOnly);
        Assert.True(Assert.Single(rows).WasApplied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dismiss_resolves_only_selected_rejection_or_legacy_preview(bool preview)
    {
        var audit = new AuditTrail(_root);
        var first = new AuditRow { RunId = "run", Target = "/same", Applied = preview, DryRun = preview };
        var second = new AuditRow { RunId = "run", Target = "/same", Applied = preview, DryRun = preview };
        var applied = new AuditRow { RunId = "run", Target = "/same", Applied = true };
        audit.Append(first);
        audit.Append(second);
        audit.Append(applied);
        audit.Dismiss(first);
        var persisted = audit.ReadAll().ToList();
        Assert.True(persisted.Single(r => r.Id == first.Id).Undone);
        Assert.False(persisted.Single(r => r.Id == first.Id).IsSuggestion);
        Assert.True(persisted.Single(r => r.Id == second.Id).IsSuggestion);
        Assert.False(persisted.Single(r => r.Id == applied.Id).Undone);
        audit.Dismiss(applied);
        Assert.False(audit.ReadAll().Single(r => r.Id == applied.Id).Undone);
    }

    [Theory]
    [InlineData(4, 4, "flat")]
    [InlineData(400, 4, "down")]
    [InlineData(4, 400, "up")]
    [InlineData(0, 4, "up")]
    [InlineData(4, 0, "down")]
    public void Error_trends_compare_equal_time_windows(int prior, int recent, string expected)
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        ErrorSignatureEvent Row(DateTime at, int count) => new() { Ts = at.ToString("O"), Component = "FTP",
            ExceptionType = "IOException", NormalizedMessage = "failure", Count = count };
        // Unequal row counts deliberately check that the split is by time, not event count.
        var rows = new[] { Row(from.AddDays(1), prior), Row(from.AddDays(6), recent), Row(from.AddDays(6), 0) };
        var digest = new ErrorsDigester().Build(rows, from, from.AddDays(8));
        Assert.Equal(expected, Assert.Single(digest.TopSignatures).TrendVsPrior);
    }

    [Fact]
    public async Task Async_digest_opens_files_without_the_callers_dispatcher_context()
    {
        var callerContext = new SynchronizationContext();
        SynchronizationContext? readerContext = callerContext;
        var path = Path.Combine(_root, $"transfers-{DateTime.Now:yyyyMMdd}.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new FileTransferEvent { SrcServer = "source", DstServer = "destination", Bytes = 100, ElapsedMs = 10 }));
        var digester = new LogDigester(_root, file => { readerContext = SynchronizationContext.Current; return File.OpenRead(file); });
        var previous = SynchronizationContext.Current;
        Task<DigestBundle> build;
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            build = digester.BuildAsync(1);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var result = await build;
        Assert.NotSame(callerContext, readerContext);
        Assert.Equal(80, result.Transfers.KbpsMatrix["source->destination"]);
    }

    [Fact]
    public void Section_mapping_migration_reports_change_even_after_theme_migration()
    {
        var config = new AppConfig();
        config.Downloads.CyberpunkPromoted = true;
        config.Servers.Add(new ServerConfig { Id = "site" });
        config.Servers[0].SpreadSite.Sections["TV"] = "/tv";
        Assert.True(ConfigManager.MigrateSectionsToMappings(config));
        Assert.Single(config.Servers[0].SpreadSite.SectionMappings);
        Assert.False(ConfigManager.MigrateSectionsToMappings(config));
    }

    [Fact]
    public async Task Digest_observes_cancellation_during_file_read()
    {
        using var cancel = new CancellationTokenSource();
        var path = Path.Combine(_root, $"transfers-{DateTime.Now:yyyyMMdd}.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new FileTransferEvent()));
        var digester = new LogDigester(_root, file => { cancel.Cancel(); return File.OpenRead(file); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => digester.BuildAsync(1, cancel.Token));
    }

    [Fact]
    public void Streaming_transfer_aggregation_preserves_weighted_rates_and_exact_percentile()
    {
        var transfers = Enumerable.Range(0, 1000).Select(i => new FileTransferEvent
        {
            SrcServer = "source", DstServer = "destination", Bytes = i + 1, ElapsedMs = 10,
            TtfbMs = i, AbortReason = i % 2 == 0 ? "aborted" : null
        });
        var digest = new TransfersDigester().Build(transfers);
        Assert.Equal(989, digest.TtfbP99Ms);
        Assert.Equal(500 * 501 * 8.0 / 5000, digest.KbpsMatrix["source->destination"]);
    }
}
