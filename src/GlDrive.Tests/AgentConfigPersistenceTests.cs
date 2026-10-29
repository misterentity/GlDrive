using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config downgrade")]
public sealed class AgentConfigPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-agent-persistence-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot = ConfigManager.AppDataPathOverride;
    private readonly bool _priorReadOnly = ConfigManager.ReadOnly;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private string Journal => Path.Combine(_root, "pending-config-commit.json");
    private string AuditPath => Path.Combine(_root, "ai-audit.jsonl");

    public AgentConfigPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        ConfigManager.AppDataPathOverride = _root;
        ConfigManager.ReadOnly = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_save_commits_and_publishes_with_default_omission_or_nondefault_options(bool nondefault)
    {
        var live = new AppConfig();
        if (nondefault)
        {
            live.Recovery.AutoInstallUpdates = false;
            live.DataStewardship.GuardFeedPreferences = true;
            live.ControlApi.Security.RequireLegacyApproval = true;
        }
        ConfigManager.Save(live);
        var baseline = File.ReadAllText(ConfigManager.ConfigPath);
        var candidate = Clone(live);
        candidate.Spread.SpreadPoolSize++;
        var audit = new AuditTrail(_root);
        AgentConfigCommit.CommitAndPublish(ConfigManager.ConfigPath, baseline, candidate, [Row()],
            _root, ConfigManager.Save, audit, live);

        Assert.Equal(candidate.Spread.SpreadPoolSize, live.Spread.SpreadPoolSize);
        Assert.Equal(candidate.Spread.SpreadPoolSize, Read().Spread.SpreadPoolSize);
        Assert.Single(audit.ReadAll());
        Assert.False(File.Exists(Journal));
        Assert.Equal(nondefault, JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!["recovery"] != null);
        Assert.Equal(!nondefault, Read().Recovery.AutoInstallUpdates);
    }

    [Fact]
    public void Commit_uses_persisted_hook_text_and_first_library_enable_normalization()
    {
        var live = new AppConfig();
        ConfigManager.Save(live);
        var saved = File.ReadAllText(ConfigManager.ConfigPath);
        using var parsed = JsonDocument.Parse(saved);
        var oldHooks = parsed.RootElement.GetProperty("hooks").GetRawText();
        var compactHooks = JsonNode.Parse(oldHooks)!.ToJsonString();
        File.WriteAllText(ConfigManager.ConfigPath, saved.Replace(oldHooks, compactHooks, StringComparison.Ordinal));
        var baseline = File.ReadAllText(ConfigManager.ConfigPath);
        var candidate = Clone(live);
        candidate.Library.Enabled = true;
        candidate.Library.DryRun = false;
        AgentConfigCommit.CommitAndPublish(ConfigManager.ConfigPath, baseline, candidate, [Row()],
            _root, ConfigManager.Save, new AuditTrail(_root), live);

        using var result = JsonDocument.Parse(File.ReadAllText(ConfigManager.ConfigPath));
        Assert.Equal(compactHooks, result.RootElement.GetProperty("hooks").GetRawText());
        Assert.True(Read().Library.DryRun);
        Assert.True(live.Library.DryRun);
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public void Successful_real_save_recovers_audit_once_after_audit_write_failure()
    {
        var live = new AppConfig();
        ConfigManager.Save(live);
        var baseline = File.ReadAllText(ConfigManager.ConfigPath);
        var candidate = Clone(live);
        candidate.Spread.SpreadPoolSize++;
        var audit = new AuditTrail(_root);
        Directory.CreateDirectory(AuditPath);
        Assert.ThrowsAny<Exception>(() => AgentConfigCommit.Commit(ConfigManager.ConfigPath, baseline, candidate,
            [Row()], _root, ConfigManager.Save, audit));
        Assert.Equal(candidate.Spread.SpreadPoolSize, Read().Spread.SpreadPoolSize);
        Assert.True(File.Exists(Journal));
        Directory.Delete(AuditPath);

        AgentConfigCommit.Recover(ConfigManager.ConfigPath, _root, audit);
        AgentConfigCommit.Recover(ConfigManager.ConfigPath, _root, audit);
        Assert.Single(audit.ReadAll());
        Assert.False(File.Exists(Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_journal_recovers_exact_candidate_including_no_setting_change(bool changesSettings)
    {
        var candidate = new AppConfig();
        ConfigManager.Save(candidate);
        var before = File.ReadAllText(ConfigManager.ConfigPath);
        if (changesSettings) candidate.Spread.SpreadPoolSize++;
        WriteLegacyJournal(before, candidate);
        ConfigManager.Save(candidate);
        var persisted = File.ReadAllText(ConfigManager.ConfigPath);
        var audit = new AuditTrail(_root);

        AgentConfigCommit.Recover(ConfigManager.ConfigPath, _root, audit);
        AgentConfigCommit.Recover(ConfigManager.ConfigPath, _root, audit);
        Assert.Single(audit.ReadAll());
        Assert.Equal(persisted, File.ReadAllText(ConfigManager.ConfigPath));
        Assert.False(File.Exists(Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_recovery_retains_evidence_when_known_or_unknown_settings_changed(bool unknown)
    {
        var candidate = new AppConfig();
        ConfigManager.Save(candidate);
        var before = File.ReadAllText(ConfigManager.ConfigPath);
        candidate.Spread.SpreadPoolSize++;
        WriteLegacyJournal(before, candidate);
        ConfigManager.Save(candidate);
        var changed = JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!;
        if (unknown) changed["futureSetting"] = true;
        else changed["logging"]!["level"] = "Debug";
        File.WriteAllText(ConfigManager.ConfigPath, changed.ToJsonString());
        var bytes = File.ReadAllBytes(ConfigManager.ConfigPath);
        var audit = new AuditTrail(_root);

        Assert.Throws<IOException>(() => AgentConfigCommit.Recover(ConfigManager.ConfigPath, _root, audit));
        Assert.True(File.Exists(Journal));
        Assert.Empty(audit.ReadAll());
        Assert.Equal(bytes, File.ReadAllBytes(ConfigManager.ConfigPath));
    }

    [Fact]
    public void Readonly_real_save_does_not_publish_or_claim_application()
    {
        var live = new AppConfig();
        ConfigManager.Save(live);
        var before = File.ReadAllText(ConfigManager.ConfigPath);
        var candidate = Clone(live);
        candidate.Spread.SpreadPoolSize++;
        ConfigManager.ReadOnly = true;
        var audit = new AuditTrail(_root);
        Assert.Throws<IOException>(() => AgentConfigCommit.CommitAndPublish(ConfigManager.ConfigPath, before, candidate,
            [Row()], _root, ConfigManager.Save, audit, live));
        Assert.Equal(before, File.ReadAllText(ConfigManager.ConfigPath));
        Assert.NotEqual(candidate.Spread.SpreadPoolSize, live.Spread.SpreadPoolSize);
        Assert.Empty(audit.ReadAll());
        Assert.True(File.Exists(Journal));
    }

    private void WriteLegacyJournal(string before, AppConfig candidate) => File.WriteAllText(Journal,
        JsonSerializer.Serialize(new AgentConfigCommit.Pending(Hash(before), Hash(JsonSerializer.Serialize(candidate, Json)), [Row()])));
    private static AuditRow Row() => new() { Id = Guid.NewGuid().ToString("N"), RunId = "regression", Applied = true };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static AppConfig Clone(AppConfig value) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(value, Json), Json)!;
    private static AppConfig Read() => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigManager.ConfigPath), Json)!;

    public void Dispose()
    {
        ConfigManager.AppDataPathOverride = _priorRoot;
        ConfigManager.ReadOnly = _priorReadOnly;
        Directory.Delete(_root, recursive: true);
    }
}
