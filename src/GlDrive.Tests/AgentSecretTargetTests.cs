using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentSecretTargetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-secret-target-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("/controlApi/token", false)]
    [InlineData("/controlApi/token", true)]
    [InlineData("/servers/0/irc/channels/0/key", false)]
    [InlineData("/servers/site-a/irc/channels/0/key", false)]
    [InlineData("/servers/0/connection/password", false)]
    [InlineData("/servers/0/connection/proxy/password", false)]
    [InlineData("/downloads/openRouterApiKey", false)]
    [InlineData("/newFeature/accessToken", false)]
    public void SecretTargetIsRejectedBeforeValidatorAndAuditedWithoutItsValues(string target, bool dryRun)
    {
        var validator = new PermissiveValidator();
        var config = new AppConfig { Servers = [new() { Id = "site-a" }] };
        config.ControlApi.Token = "original-secret-value";
        var rows = new List<AuditRow>();
        var applier = new ChangeApplier([validator], new FreezeStore(_directory), new AuditTrail(_directory));

        var report = applier.Apply([new AgentChange
        {
            Category = validator.Category, Target = target, Confidence = 1,
            Before = "original-secret-value", After = "replacement-secret-value"
        }], config, new AgentConfig(), "test-run", dryRun, rows.Add);

        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, report.Applied);
        Assert.Equal(1, report.Rejected);
        Assert.Equal(1, report.RejectionByReason["secret-target"]);
        Assert.Equal("original-secret-value", config.ControlApi.Token);
        var row = Assert.Single(rows);
        Assert.Equal("secret-target", row.RejectionReason);
        Assert.False(row.IsSuggestion);
        var json = JsonSerializer.Serialize(row);
        Assert.DoesNotContain("original-secret-value", json);
        Assert.DoesNotContain("replacement-secret-value", json);
        Assert.Contains("sha256:", json);
    }

    [Fact]
    public void NonSecretTargetStillReachesValidator()
    {
        var validator = new PermissiveValidator();
        var applier = new ChangeApplier([validator], new FreezeStore(_directory), new AuditTrail(_directory));
        var report = applier.Apply([new AgentChange
        {
            Category = validator.Category, Target = "/downloads/theme", Confidence = 1,
            After = "changed"
        }], new AppConfig(), new AgentConfig(), "test-run", true, _ => { });
        Assert.Equal(2, validator.Calls); // validate original plus detached no-op projection
        Assert.Equal(1, report.Applied);
    }

    private sealed class PermissiveValidator : IChangeValidator
    {
        public string Category => "test-category";
        public int Calls { get; private set; }
        public ValidationResult Validate(AgentChange change, AppConfig config)
        {
            Calls++;
            return new(true, null, candidate => candidate.ControlApi.Token = "replacement-secret-value");
        }
    }
}
