using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Irc;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class AnnounceSourceHealthTests
{
    private static void Receive(IrcAnnounceListener listener, int episode) => listener.OnMessage("#releases", new IrcMessageItem
    {
        Timestamp = new DateTime(2026, 1, 5, 12, episode, 0, DateTimeKind.Utc),
        Nick = "fixtureBot", Type = IrcMessageType.Normal,
        Text = $"[ NEW ] in [ TV ] Synthetic.Show.S01E{episode:D2}.1080p.WEB-TEST"
    });

    [Fact]
    public void MatchedTelemetrySamplesCurrentSourceStateWithoutSuppressingAnnounces()
    {
        var rows = new List<MatchedAnnounceEvent>();
        using var listener = new IrcAnnounceListener("site-a", [], true, recordMatchedAnnounce: rows.Add);
        var announces = 0;
        listener.ReleaseAnnounced += (_, _, _, _) => announces++;
        Receive(listener, 1); // An unconfigured source retains compatible false metadata.
        var down = true;
        listener.SourceDown = () => down;
        Receive(listener, 2);
        down = false;
        Receive(listener, 3);

        Assert.Equal(3, announces);
        Assert.Equal(new[] { false, true, false }, rows.Select(row => row.SourceDown));
        Assert.All(rows, row => Assert.Equal("site-a", row.ServerId));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rows[1]));
        Assert.True(json.RootElement.GetProperty("sourceDown").GetBoolean());
    }

    [Fact]
    public void AnnotationFailureRetainsMatchedTelemetryAndNormalDispatch()
    {
        var rows = new List<MatchedAnnounceEvent>();
        using var listener = new IrcAnnounceListener("site-a", [], true, recordMatchedAnnounce: rows.Add)
        {
            SourceDown = () => throw new InvalidOperationException("health unavailable")
        };
        var announces = 0;
        listener.ReleaseAnnounced += (_, _, _, _) => announces++;
        Receive(listener, 1);
        Assert.False(Assert.Single(rows).SourceDown);
        Assert.Equal(1, announces);
    }

    [Fact]
    public void OlderMatchedTelemetryRowsDefaultToSourceAvailable()
    {
        var row = JsonSerializer.Deserialize<MatchedAnnounceEvent>("{\"serverId\":\"site-a\",\"release\":\"Synthetic.Show.S01E01.1080p.WEB-TEST\"}");
        Assert.False(row!.SourceDown);
    }
}
