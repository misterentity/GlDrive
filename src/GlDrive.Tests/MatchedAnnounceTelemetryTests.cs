using System.Text.Json;
using System.Text.Json.Serialization;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class MatchedAnnounceTelemetryTests
{
    // Mirror TelemetryRecorder.JsonOpts: nulls are omitted on write so new optional fields stay back-compatible.
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public void MatchedAnnounceEvent_serializes_with_expected_json_names()
    {
        var evt = new MatchedAnnounceEvent
        {
            ServerId = "syn",
            Channel = "#syn",
            Section = "MP3",
            Release = "Artist-Album-2026-GRP",
            ParsedType = "music",
            Quality = "320",
            Source = "WEB",
            Group = "GRP",
            RuleSource = "builtin",
            AutoRace = true
        };

        var json = JsonSerializer.Serialize(evt, evt.GetType(), Opts);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("syn", root.GetProperty("serverId").GetString());
        Assert.Equal("#syn", root.GetProperty("channel").GetString());
        Assert.Equal("MP3", root.GetProperty("section").GetString());
        Assert.Equal("Artist-Album-2026-GRP", root.GetProperty("release").GetString());
        Assert.Equal("music", root.GetProperty("parsedType").GetString());
        Assert.Equal("320", root.GetProperty("quality").GetString());
        Assert.Equal("WEB", root.GetProperty("source").GetString());
        Assert.Equal("GRP", root.GetProperty("group").GetString());
        Assert.Equal("builtin", root.GetProperty("ruleSource").GetString());
        Assert.True(root.GetProperty("autoRace").GetBoolean());
        // Envelope fields from TelemetryEnvelope.
        Assert.True(root.TryGetProperty("ts", out _));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
    }

    [Fact]
    public void MatchedAnnounceEvent_round_trips()
    {
        var evt = new MatchedAnnounceEvent
        {
            ServerId = "zephyr",
            Channel = "#zephyr",
            Section = "TV-1080",
            Release = "Show.S01E01.1080p.WEB.H264-GRP",
            ParsedType = "tv",
            Quality = "1080p",
            Source = "WEB",
            Group = "GRP",
            RuleSource = "custom",
            AutoRace = false
        };

        var json = JsonSerializer.Serialize(evt, evt.GetType(), Opts);
        var back = JsonSerializer.Deserialize<MatchedAnnounceEvent>(json, Opts);

        Assert.NotNull(back);
        Assert.Equal(evt.ServerId, back!.ServerId);
        Assert.Equal(evt.Channel, back.Channel);
        Assert.Equal(evt.Section, back.Section);
        Assert.Equal(evt.Release, back.Release);
        Assert.Equal(evt.ParsedType, back.ParsedType);
        Assert.Equal(evt.Quality, back.Quality);
        Assert.Equal(evt.Source, back.Source);
        Assert.Equal(evt.Group, back.Group);
        Assert.Equal(evt.RuleSource, back.RuleSource);
        Assert.Equal(evt.AutoRace, back.AutoRace);
    }

    [Fact]
    public void RaceOutcomeEvent_omits_new_fields_when_null()
    {
        // New section→folder fields left at their null defaults must NOT appear in the serialized line.
        var evt = new RaceOutcomeEvent
        {
            RaceId = "r1",
            Section = "MP3",
            Release = "Artist-Album-2026-GRP",
            Result = "complete"
        };

        var json = JsonSerializer.Serialize(evt, evt.GetType(), Opts);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("resolvedRemoteSection", out _));
        Assert.False(root.TryGetProperty("destFolderPath", out _));
        Assert.False(root.TryGetProperty("wasAutoRaced", out _));
        Assert.False(root.TryGetProperty("matchedTriggerRegex", out _));
    }

    [Fact]
    public void RaceOutcomeEvent_includes_new_fields_when_set()
    {
        var evt = new RaceOutcomeEvent
        {
            RaceId = "r2",
            ResolvedRemoteSection = "/site/MP3",
            DestFolderPath = "/site/MP3/Artist-Album-2026-GRP",
            WasAutoRaced = true,
            MatchedTriggerRegex = @"(?i).*-GRP$"
        };

        var json = JsonSerializer.Serialize(evt, evt.GetType(), Opts);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("/site/MP3", root.GetProperty("resolvedRemoteSection").GetString());
        Assert.Equal("/site/MP3/Artist-Album-2026-GRP", root.GetProperty("destFolderPath").GetString());
        Assert.True(root.GetProperty("wasAutoRaced").GetBoolean());
        Assert.Equal(@"(?i).*-GRP$", root.GetProperty("matchedTriggerRegex").GetString());
    }

    [Fact]
    public void Old_race_line_without_new_fields_deserializes_with_nulls()
    {
        // Back-compat: a pre-feature race JSONL line (no new keys) must deserialize cleanly,
        // leaving the four optional section→folder fields null.
        const string oldLine =
            "{\"ts\":\"2026-01-01T00:00:00.0000000Z\",\"v\":1,\"raceId\":\"old1\"," +
            "\"section\":\"MP3\",\"release\":\"Artist-Album-2026-GRP\",\"result\":\"complete\"," +
            "\"filesExpected\":10,\"filesTotal\":10}";

        var evt = JsonSerializer.Deserialize<RaceOutcomeEvent>(oldLine, Opts);

        Assert.NotNull(evt);
        Assert.Equal("old1", evt!.RaceId);
        Assert.Equal("complete", evt.Result);
        Assert.Equal(10, evt.FilesTotal);
        Assert.Null(evt.ResolvedRemoteSection);
        Assert.Null(evt.DestFolderPath);
        Assert.Null(evt.WasAutoRaced);
        Assert.Null(evt.MatchedTriggerRegex);
    }
}
