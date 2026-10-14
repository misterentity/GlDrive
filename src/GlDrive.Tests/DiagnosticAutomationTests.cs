using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class DiagnosticAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-automation-tests-" + Guid.NewGuid().ToString("N"));
    public DiagnosticAutomationTests() => Directory.CreateDirectory(Path.Combine(_root, "logs"));
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void LogReadIsBoundedAndMasksKnownControlToken()
    {
        var token = "synthetic-private-control-token";
        var path = Path.Combine(_root, "logs", "gldrive-test.log");
        File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"line {i} {token} password=old-unknown-secret"));
        var diagnostics = new DiagnosticAutomation(new AppConfig { ControlApi = new() { Token = token } }, _root);
        var result = JsonSerializer.SerializeToElement(diagnostics.Read(name: "gldrive-test.log", lines: 2, maxBytes: 1024));
        var text = result.GetProperty("text").GetString()!;
        Assert.Contains("line 500", text);
        Assert.DoesNotContain("line 498", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("old-unknown-secret", text);
        Assert.True(result.GetProperty("Truncated").GetBoolean());
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("..\\appsettings.json")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("gldrive-test.log:stream")]
    [InlineData("appsettings.json")]
    public void DiagnosticsRejectsTraversalAndNonLogFiles(string name)
    {
        var diagnostics = new DiagnosticAutomation(new(), _root);
        Assert.Throws<ArgumentException>(() => diagnostics.Read(name: name));
    }

    [Fact]
    public void DiagnosticListDoesNotExposeOtherAppFiles()
    {
        File.WriteAllText(Path.Combine(_root, "logs", "gldrive-test.log"), "test");
        File.WriteAllText(Path.Combine(_root, "logs", "credentials.json"), "private");
        var result = JsonSerializer.Serialize(new DiagnosticAutomation(new(), _root).ListFiles());
        Assert.Contains("gldrive-test.log", result);
        Assert.DoesNotContain("credentials.json", result);
        Assert.DoesNotContain(_root, result);
    }

    [Fact]
    public void TailDiscardsAPartialFirstLineWithoutReadingBeyondBudget()
    {
        var path = Path.Combine(_root, "logs", "gldrive-test.log");
        File.WriteAllText(path, new string('a', 5000) + "\nlast line\n");
        var result = DiagnosticAutomation.ReadTail(path, 10, 32);
        Assert.Equal("last line", result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ByteCursorPagesThroughUnicodeHistoryWithoutGapsOrDuplicateLines()
    {
        var path = Path.Combine(_root, "logs", "gldrive-test.log");
        var lines = Enumerable.Range(0, 30).Select(i => $"Entry {i} café 東京").ToArray();
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        long? before = null;
        var collected = new List<string>();
        for (var page = 0; page < 20; page++)
        {
            var tail = DiagnosticAutomation.ReadTail(path, 3, 180, before);
            collected.InsertRange(0, tail.Text.Split('\n').Select(s => s.TrimEnd('\r')));
            if (tail.NextBeforeByte == 0) break;
            Assert.True(before == null || tail.NextBeforeByte < before);
            before = tail.NextBeforeByte;
        }
        Assert.Equal(lines, collected);
    }
}
