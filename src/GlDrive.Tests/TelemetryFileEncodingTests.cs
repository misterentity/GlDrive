using System.IO;
using System.Text;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Every ai-data/*.jsonl file on disk on 2026-08-25 began with a UTF-8 BOM (EF BB BF), so the
/// FIRST row of EVERY stream, EVERY day, was not valid JSON on its own. Cause: the writer passed
/// <c>Encoding.UTF8</c> to File.AppendAllTextAsync, and that instance is
/// <c>UTF8Encoding(encoderShouldEmitUTF8Identifier: true)</c> — AppendAllText writes the preamble
/// when it creates the file, and a new file is created every day.
///
/// It happened to be survivable in-process only because every current reader goes through
/// StreamReader, whose default BOM detection eats the preamble. That is luck, not design: the row
/// is genuinely malformed, and any reader over raw bytes (Utf8JsonReader,
/// JsonSerializer.Deserialize(ReadOnlySpan&lt;byte&gt;), jq, python json.loads, or any external
/// tool) fails on line 1 of every file. These files are the AI agent's evidence trail, so they
/// must be well-formed as written rather than well-formed only when read a particular way.
/// </summary>
public sealed class TelemetryFileEncodingTests
{
    [Fact]
    public void FileEncoding_EmitsNoPreamble()
    {
        // Mutation guard: reverting the writer to Encoding.UTF8 makes this preamble 3 bytes.
        Assert.Empty(TelemetryRecorder.FileEncoding.GetPreamble());
    }

    [Fact]
    public void AppendingToANewFile_WritesNoBomBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Exercise the writer's exact call shape against a file that does not yet exist,
            // which is the only moment AppendAllText would emit a preamble.
            var path = Path.Combine(dir, "races-20260825.jsonl");
            File.AppendAllText(path, "{\"raceId\":\"r1\"}\n", TelemetryRecorder.FileEncoding);
            File.AppendAllText(path, "{\"raceId\":\"r2\"}\n", TelemetryRecorder.FileEncoding);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "telemetry file must not start with a UTF-8 BOM");
            Assert.Equal((byte)'{', bytes[0]);

            // And the bytes must parse as JSON line-by-line without BOM-aware decoding.
            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
                System.Text.Json.JsonDocument.Parse(line).Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
