using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class RenderArtifactSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-render-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_runs_have_separate_complete_manifests_bound_to_actual_binary()
    {
        var outputs = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            using var run = new RenderArtifactSession(_root);
            run.Write("sample.png", stream => stream.WriteByte((byte)i));
            run.Complete();
            return run.OutputDirectory;
        })));
        Assert.Equal(8, outputs.Distinct().Count());
        foreach (var output in outputs)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
            Assert.Equal("complete", json.RootElement.GetProperty("Status").GetString());
            Assert.Equal(typeof(App).Assembly.GetName().Version!.ToString(), json.RootElement.GetProperty("AssemblyVersion").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(App).Assembly.Location))),
                json.RootElement.GetProperty("AssemblySha256").GetString());
            var file = Assert.Single(json.RootElement.GetProperty("Files").EnumerateArray());
            Assert.Equal("sample.png", file.GetProperty("Name").GetString());
            Assert.Equal(1, file.GetProperty("Size").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "sample.png")))),
                file.GetProperty("Sha256").GetString());
        }
    }

    [Fact]
    public void Explicit_directory_is_claimed_once_and_previous_evidence_is_preserved()
    {
        var output = Path.Combine(_root, "explicit");
        using (var run = new RenderArtifactSession(_root, output))
        {
            Assert.Throws<IOException>(() => new RenderArtifactSession(_root, output));
            run.Write("sample.png", stream => stream.WriteByte(42));
            Assert.Throws<IOException>(() => run.Write("sample.png", stream => stream.WriteByte(99)));
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(output, "sample.png")));
        }
        Assert.Throws<IOException>(() => new RenderArtifactSession(_root, output));
        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("..\\escape.png")]
    [InlineData("manifest.json")]
    [InlineData("sample.png:alternate")]
    [InlineData(".render-owner")]
    [InlineData(".RENDER-OWNER")]
    [InlineData("MANIFEST.JSON")]
    [InlineData("sample.png.")]
    [InlineData("sample.png ")]
    public void Artifact_names_cannot_escape_or_replace_session_metadata(string name)
    {
        using var run = new RenderArtifactSession(_root);
        Assert.Throws<ArgumentException>(() => run.Write(name, _ => { }));
    }

    [Fact]
    public void Completed_run_rejects_further_writes()
    {
        using var run = new RenderArtifactSession(_root);
        run.Complete();
        Assert.Throws<InvalidOperationException>(() => run.Write("late.png", _ => { }));
        Assert.Throws<InvalidOperationException>(run.Complete);
    }

    [Fact]
    public void Failed_encoder_retains_partial_file_and_permanently_blocks_completion()
    {
        using var run = new RenderArtifactSession(_root);
        run.Write("good.png", stream => stream.WriteByte(42));
        Assert.Throws<IOException>(() => run.Write("partial.png", stream =>
        {
            stream.WriteByte(99);
            throw new IOException("encoder failed");
        }));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Path.Combine(run.OutputDirectory, "partial.png")));
        Assert.Throws<InvalidOperationException>(run.Complete);
        Assert.Throws<InvalidOperationException>(() => run.Write("retry.png", _ => { }));
        Assert.False(File.Exists(Path.Combine(run.OutputDirectory, "manifest.json")));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(run.OutputDirectory, "good.png")));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    public void Completion_rechecks_inventory_and_recorded_bytes(string mutation)
    {
        using var run = new RenderArtifactSession(_root);
        run.Write("sample.png", stream => stream.Write(new byte[] { 42, 43 }));
        var path = Path.Combine(run.OutputDirectory, "sample.png");
        switch (mutation)
        {
            case "changed": File.WriteAllBytes(path, new byte[] { 99, 43 }); break;
            case "truncated": File.WriteAllBytes(path, new byte[] { 42 }); break;
            case "missing": File.Delete(path); break;
            case "extra-file": File.WriteAllText(Path.Combine(run.OutputDirectory, "unrecorded.png"), "extra"); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(run.OutputDirectory, "unrecorded")); break;
        }
        Assert.Throws<IOException>(run.Complete);
        Assert.Throws<InvalidOperationException>(run.Complete);
        Assert.False(File.Exists(Path.Combine(run.OutputDirectory, "manifest.json")));
    }

    [Fact]
    public void Completion_failure_preserves_existing_manifest_instead_of_overwriting_it()
    {
        using var run = new RenderArtifactSession(_root);
        run.Write("sample.png", stream => stream.WriteByte(42));
        var path = Path.Combine(run.OutputDirectory, "manifest.json");
        File.WriteAllText(path, "preexisting evidence");
        Assert.Throws<IOException>(run.Complete);
        Assert.Equal("preexisting evidence", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
