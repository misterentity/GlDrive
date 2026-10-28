using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentAttributionScopeTests
{
    [Theory]
    [InlineData("mcp:config_patch")]
    [InlineData("restore:history")]
    [InlineData("lease-revert:fixture")]
    public async Task Scheduled_callbacks_do_not_inherit_the_scheduling_actor(string actor)
    {
        using var scope = ConfigWriteScope.Begin(actor, "human-audit");
        var completed = new TaskCompletionSource<ConfigWriteContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = AgentRunner.CreateScheduledTimer(_ => completed.TrySetResult(ConfigWriteScope.Current), TimeSpan.Zero);
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("application", observed.Actor);
        Assert.Null(observed.AuditRef);
        Assert.Equal(actor, ConfigWriteScope.Current.Actor);
    }

    [Fact]
    public void Timer_creation_preserves_an_existing_flow_suppression()
    {
        using (ExecutionContext.SuppressFlow())
        {
            using var timer = AgentRunner.CreateScheduledTimer(_ => { }, Timeout.InfiniteTimeSpan);
            Assert.True(ExecutionContext.IsFlowSuppressed());
        }
        Assert.False(ExecutionContext.IsFlowSuppressed());
    }

    [Fact]
    public async Task Run_opens_its_own_agent_scope_and_restores_the_caller()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-attribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var outer = ConfigWriteScope.Begin("mcp:config_patch", "human-audit");
            var freeze = new FreezeStore(root);
            var audit = new AuditTrail(root);
            ConfigWriteContext? observed = null;
            // A missing synthetic config exits before any model or live-state access.
            // The reschedule in finally still observes the run's attribution scope.
            using var runner = new AgentRunner(new LogDigester(root), new AgentMemo(root), freeze,
                new ChangeApplier([], freeze, audit), audit, new SnapshotStore(root, 1),
                Path.Combine(root, "missing.json"), _ => { },
                () => { observed = ConfigWriteScope.Current; return new AppConfig(); }, root);
            await runner.RunNowAsync();
            Assert.NotNull(observed);
            Assert.StartsWith("agent:", observed.Actor);
            Assert.Equal(observed.Actor[6..], observed.AuditRef);
            Assert.Equal("mcp:config_patch", ConfigWriteScope.Current.Actor);
        }
        finally { Directory.Delete(root, true); }
    }
}
