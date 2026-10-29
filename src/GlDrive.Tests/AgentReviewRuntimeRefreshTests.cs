using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentReviewRuntimeRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-reviewed-refresh-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public AgentReviewRuntimeRefreshTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_apply_or_undo_refreshes_only_after_persistence_and_publication(bool undo)
    {
        var cfg = Config(undo ? SitePriority.Low : SitePriority.Normal);
        var path = Path.Combine(_root, "config.json");
        var refreshes = 0;
        var expected = undo ? SitePriority.Normal : SitePriority.Low;
        var service = Service(cfg, undo, true, () =>
        {
            Assert.Equal(expected, cfg.Servers[0].SpreadSite.Priority);
            Assert.Equal(expected, JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json)!.Servers[0].SpreadSite.Priority);
            refreshes++;
        });
        var result = await Run(service, undo);
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.False(result.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task Rejected_review_does_not_publish_or_refresh()
    {
        var cfg = Config(); var refreshes = 0;
        var result = await Run(Service(cfg, false, false, () => refreshes++));
        Assert.False(result.GetProperty("applied").GetBoolean());
        Assert.Equal(0, refreshes);
        Assert.Equal(SitePriority.Normal, cfg.Servers[0].SpreadSite.Priority);
        Assert.Contains("insufficient-winner-coverage", result.GetProperty("rejectionReasons").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Failed_commit_does_not_publish_or_refresh()
    {
        var cfg = Config(); var refreshes = 0;
        var service = Service(cfg, false, true, () => refreshes++, _ => throw new IOException("synthetic save failure"));
        await Assert.ThrowsAsync<IOException>(() => Run(service));
        Assert.Equal(0, refreshes);
        Assert.Equal(SitePriority.Normal, cfg.Servers[0].SpreadSite.Priority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_refresh_callback_reports_restart_fallback(bool throws)
    {
        var service = Service(Config(), false, true, throws ? () => throw new InvalidOperationException("dispatcher unavailable") : null);
        var result = await Run(service);
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.True(result.GetProperty("restartRequired").GetBoolean());
    }

    private static AppConfig Config(SitePriority priority = SitePriority.Normal) => new()
    {
        Servers = [new ServerConfig { Id = "site", SpreadSite = new() { Priority = priority } }]
    };

    private AgentAutomation Service(AppConfig cfg, bool undo, bool enoughCoverage, Action? refresh,
        Action<AppConfig>? save = null)
    {
        var aiRoot = Path.Combine(_root, "ai-data");
        Directory.CreateDirectory(aiRoot);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json));
        var audit = new AuditTrail(aiRoot);
        var freeze = new FreezeStore(aiRoot);
        audit.Append(new AuditRow { RunId = "original", Category = AgentCategories.Priority,
            Target = "/servers/site/spread/sitePriority", Before = "Normal", After = "Low",
            Applied = undo, Reasoning = "Measured race outcomes", EvidenceRef = "races-synthetic.jsonl" });
        var applier = new ChangeApplier([new PriorityValidator()], freeze, audit,
            c => enoughCoverage && !undo ? MeasuredWinnerEvidence.ForConfig(c) : null);
        return new AgentAutomation(cfg, () => null, () => freeze, () => audit, () => applier, _root,
            refresh, path, save ?? (candidate => File.WriteAllText(path,
                ConfigManager.PrepareSerializedSettings(JsonSerializer.Serialize(candidate, Json), File.ReadAllText(path)))));
    }

    private static async Task<JsonElement> Run(AgentAutomation service, bool undo = false) =>
        JsonSerializer.SerializeToElement(await service.ExecuteAsync("agent_audit_action", JsonSerializer.SerializeToElement(new
        {
            action = undo ? "undo" : "apply", runId = "original", target = "/servers/site/spread/sitePriority"
        }), default));
}
