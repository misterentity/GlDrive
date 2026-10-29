using System.Text.Json;
using GlDrive.Services.Automation;
using GlDrive.Services.Operations;
using Xunit;

namespace GlDrive.Tests;

public sealed class OperationsAutomationTests
{
    [Theory]
    [InlineData("release.trace", "{}")]
    [InlineData("release.trace", "{\"release\":\" \"}")]
    [InlineData("release.trace", "{\"release\":5}")]
    [InlineData("release.trace", "{\"release\":\"Synthetic.Release\",\"days\":0}")]
    [InlineData("release.trace", "{\"release\":\"Synthetic.Release\",\"section\":[]}")]
    [InlineData("ops.timeline", "{\"limit\":501}")]
    [InlineData("ops.timeline", "{\"limit\":1.5}")]
    [InlineData("ops.timeline", "{\"serverId\":false}")]
    [InlineData("ops.timeline", "{\"days\":null}")]
    [InlineData("ops.incidents", "{\"limit\":101}")]
    [InlineData("ops.incidents", "{\"days\":91}")]
    public async Task Invalid_queries_are_rejected_before_any_service_is_reached(string operation, string json)
    {
        var calls = 0;
        var module = new OperationsAutomation(() => { calls++; throw new InvalidOperationException(); },
            () => { calls++; throw new InvalidOperationException(); });
        using var args = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync(operation, args.RootElement, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Tools_are_read_only_and_cancellation_or_unknown_operation_has_no_effect()
    {
        var calls = 0;
        var module = new OperationsAutomation(() => { calls++; throw new InvalidOperationException(); },
            () => { calls++; throw new InvalidOperationException(); });
        Assert.All(module.Tools, tool => { Assert.True(tool.ReadOnly); Assert.False(tool.Destructive); });
        Assert.Equal(3, module.Tools.Count);
        using var args = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync("ops.timeline", args.RootElement, new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("release.race", args.RootElement, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Dossier_only_calls_the_pure_report_delegate_with_validated_arguments()
    {
        var expected = new ReleaseDossier(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            "Synthetic.Release", [], [], new(0, 0, 0, 0, 0, 0, false, []));
        string? actualRelease = null, actualSection = null; var actualDays = 0;
        var module = new OperationsAutomation((release, section, days, ct) =>
        {
            actualRelease = release; actualSection = section; actualDays = days;
            return Task.FromResult(expected);
        }, (_, _, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException());
        using var args = JsonDocument.Parse("{\"release\":\" Synthetic.Release \",\"section\":\" movies \"}");
        var result = await module.ExecuteAsync("release.trace", args.RootElement, default);
        Assert.Same(expected, result); Assert.Equal("Synthetic.Release", actualRelease);
        Assert.Equal("movies", actualSection); Assert.Equal(30, actualDays);
    }

    [Theory]
    [InlineData("ops.timeline", 200)]
    [InlineData("ops.incidents", 50)]
    public async Task Journal_queries_use_bounded_defaults_and_only_the_selected_reader(string operation, int expectedLimit)
    {
        var timelineCalls = 0; var incidentCalls = 0;
        var module = new OperationsAutomation((_, _, _, _) => throw new InvalidOperationException(),
            (server, days, limit, ct) =>
            {
                timelineCalls++; Assert.Null(server); Assert.Equal(7, days); Assert.Equal(expectedLimit, limit);
                return Task.FromResult<IReadOnlyList<OpsEvent>>([]);
            },
            (server, days, limit, ct) =>
            {
                incidentCalls++; Assert.Null(server); Assert.Equal(7, days); Assert.Equal(expectedLimit, limit);
                return Task.FromResult<IReadOnlyList<OpsIncident>>([]);
            });
        using var args = JsonDocument.Parse("{}");
        await module.ExecuteAsync(operation, args.RootElement, default);
        Assert.Equal(operation == "ops.timeline" ? 1 : 0, timelineCalls);
        Assert.Equal(operation == "ops.incidents" ? 1 : 0, incidentCalls);
    }

    [Fact]
    public async Task Oversized_release_is_rejected_without_truncating_to_another_identity()
    {
        var module = new OperationsAutomation(() => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        var args = JsonSerializer.SerializeToElement(new { release = new string('x', 1025) });
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("release.trace", args, default));
    }

    [Theory]
    [InlineData("ops.timeline", "events")]
    [InlineData("ops.incidents", "incidents")]
    public async Task Journal_envelopes_surface_failed_persistence_and_dropped_backlog(string operation, string rowsName)
    {
        var module = new OperationsAutomation((_, _, _, _) => throw new InvalidOperationException(),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<OpsEvent>>([]),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<OpsIncident>>([]), () => (false, 17));
        using var args = JsonDocument.Parse("{}");
        var result = JsonSerializer.SerializeToElement(await module.ExecuteAsync(operation, args.RootElement, default));
        Assert.False(result.GetProperty("persistenceHealthy").GetBoolean());
        Assert.Equal(17, result.GetProperty("dropped").GetInt64());
        Assert.Equal(JsonValueKind.Array, result.GetProperty(rowsName).ValueKind);
    }

    [Theory]
    [InlineData("ops.timeline")]
    [InlineData("ops.incidents")]
    public async Task Bounded_journal_query_coverage_reaches_the_mcp_envelope(string operation)
    {
        var module = new OperationsAutomation((_, _, _, _) => throw new InvalidOperationException(),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<OpsEvent>>(new OpsReadResult<OpsEvent>([], ["time-limit"])),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<OpsIncident>>(new OpsReadResult<OpsIncident>([], ["time-limit"])));
        using var args = JsonDocument.Parse("{}");
        var result = JsonSerializer.SerializeToElement(await module.ExecuteAsync(operation, args.RootElement, default));
        Assert.True(result.GetProperty("partial").GetBoolean());
        Assert.Equal("time-limit", result.GetProperty("limits")[0].GetString());
    }
}
