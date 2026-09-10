using System.IO;
using System.Text.Json;
using FluentFTP;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Filesystem;
using GlDrive.Ftp;
using GlDrive.Player;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class ReleaseReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-release-tests-" + Guid.NewGuid().ToString("N"));
    public ReleaseReliabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Short_transfer_fails_and_does_not_claim_completion()
    {
        using var source = new MemoryStream("short"u8.ToArray());
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() => StreamTransfer.CopyAsync(source, destination, 6, default));
        Assert.Equal(5, destination.Length);
    }

    [Fact]
    public async Task Range_copy_preserves_boundaries_and_mirror()
    {
        using var source = new MemoryStream("abcdef"u8.ToArray());
        using var destination = new MemoryStream();
        using var mirror = new MemoryStream();
        source.Position = 1;
        await StreamTransfer.CopyAsync(source, destination, 3, default, mirror);
        Assert.Equal("bcd"u8.ToArray(), destination.ToArray());
        Assert.Equal(destination.ToArray(), mirror.ToArray());
        Assert.Equal(4, source.Position);
    }

    [Theory]
    [InlineData("426")]
    [InlineData("150")]
    [InlineData("200")]
    public void Failed_or_unrelated_reply_cannot_complete_a_transfer(string code) =>
        Assert.Throws<IOException>(() => CpsvDataHelper.ValidateCompletion(new FtpReply { Code = code }));

    [Theory]
    [InlineData("226")]
    [InlineData("250")]
    public void Transfer_completion_is_accepted(string code) =>
        CpsvDataHelper.ValidateCompletion(new FtpReply { Code = code });

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[null]")]
    public void Unreadable_freezes_fail_closed_and_preserve_file(string corrupt)
    {
        var path = Path.Combine(_root, "frozen.json");
        File.WriteAllText(path, corrupt);
        var store = new FreezeStore(_root);
        Assert.True(store.IsFrozen("/servers/0"));
        Assert.Throws<IOException>(() => store.Unfreeze("/servers/0"));
        Assert.Throws<IOException>(() => store.Freeze("/servers/1"));
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void Failed_freeze_save_leaves_in_memory_restrictions_unchanged()
    {
        var store = new FreezeStore(_root);
        store.Freeze("/servers");
        File.Delete(Path.Combine(_root, "frozen.json"));
        Directory.CreateDirectory(Path.Combine(_root, "frozen.json"));
        var error = Record.Exception(() => store.Unfreeze("/servers"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(store.IsFrozen("/servers/0"));
    }

    [Fact]
    public void Corrupt_resume_and_cursor_files_are_not_overwritten()
    {
        foreach (var name in new[] { "resume.json", "nuke-cursors.json" }) File.WriteAllText(Path.Combine(_root, name), "{");
        new PlayerResumeStore(_root).SavePosition("release", 40);
        new NukeCursorStore(_root).Set("server", DateTime.UtcNow);
        foreach (var name in new[] { "resume.json", "nuke-cursors.json" }) Assert.Equal("{", File.ReadAllText(Path.Combine(_root, name)));
    }

    [Fact]
    public void Concurrent_resume_updates_survive_reload()
    {
        var store = new PlayerResumeStore(_root);
        Parallel.For(0, 25, i => store.SavePosition("release" + i, 30 + i));
        store.SavePosition("release0", double.NaN);
        var restored = new PlayerResumeStore(_root);
        for (var i = 0; i < 25; i++) Assert.Equal(30 + i, restored.GetPosition("release" + i));
    }

    [Fact]
    public void Disk_staged_read_and_partial_write_preserve_contents()
    {
        using var node = new FileNode("/file") { FileSize = 6, SpillThresholdBytes = 4 };
        node.LoadReadBuffer(stream => stream.Write("abcdef"u8));
        Assert.IsType<FileStream>(node.ReadBuffer);
        var stream = node.PrepareWriteStream(_ => throw new Exception("Must reuse read"), 6);
        Assert.IsType<FileStream>(stream);
        stream.Position = 2;
        stream.WriteByte((byte)'X');
        node.IsDirty = true;
        var recovery = node.PreserveFailedWrite(_root, "test");
        node.Dispose();
        Assert.Equal("abXdef", File.ReadAllText(recovery!));
        Assert.Contains("/file", File.ReadAllText(recovery + ".json"));
    }

    [Fact]
    public void Failed_disk_read_does_not_publish_partial_data()
    {
        using var node = new FileNode("/file");
        Assert.Throws<IOException>(() => node.LoadReadBuffer(stream => { stream.WriteByte(1); throw new IOException(); }));
        Assert.Null(node.ReadBuffer);
        Assert.False(node.ReadBufferLoaded);
        node.LoadReadBuffer(stream => stream.WriteByte(2));
        Assert.Equal(2, node.ReadBuffer!.ReadByte());
    }

    [Fact]
    public void Multiple_open_handles_share_file_state_until_last_close()
    {
        var cache = new DirectoryCache();
        cache.Set("/", [new FtpListItem { Name = "file", Type = FtpObjectType.File, Size = 6 }]);
        var fs = new GlDriveFileSystem(null!, cache, "/", "test");
        Assert.Equal(0, fs.Open("\\file", 0, 0, out var first, out _, out _, out _));
        Assert.Equal(0, fs.Open("\\file", 0, 0, out var second, out _, out _, out _));
        Assert.Same(first, second);
        var node = (FileNode)first;
        node.WriteBuffer = new MemoryStream("abcdef"u8.ToArray());
        fs.Close(first, first);
        Assert.Equal(6, node.WriteBuffer.Length);
        fs.Close(second, second);
        Assert.Null(node.WriteBuffer);
    }

    [Fact]
    public void Volume_flush_returns_upload_failure_and_preserves_dirty_state()
    {
        var cache = new DirectoryCache();
        cache.Set("/", [new FtpListItem { Name = "file", Type = FtpObjectType.File }]);
        var fs = new GlDriveFileSystem(null!, cache, "/", "test");
        Assert.Equal(0, fs.Open("\\file", 0, 0, out var opened, out _, out _, out _));
        var node = (FileNode)opened;
        node.WriteBuffer = new MemoryStream("unsaved"u8.ToArray());
        node.IsDirty = true;
        Assert.NotEqual(0, fs.Flush(null!, null!, out _));
        Assert.True(node.IsDirty);
        Assert.Equal("unsaved"u8.ToArray(), node.WriteBuffer.ToArray());
        node.IsDirty = false; // Unit fixture must not create application recovery files.
        fs.Close(node, node);
    }

    [Fact]
    public async Task Stopped_worker_group_drains_existing_work_and_refuses_new_work()
    {
        var group = new BackgroundTasks();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(group.TryRun(() => finish.Task));
        var drain = group.StopAsync();
        Assert.False(drain.IsCompleted);
        Assert.False(group.TryRun(() => Task.CompletedTask));
        finish.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AI_commit_rejects_concurrent_settings_edits()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "user edit");
        Assert.Throws<IOException>(() => AgentConfigCommit.Commit(path, "old", new AppConfig(), [], _root,
            _ => throw new Exception("Must not save"), new AuditTrail(_root)));
        Assert.Equal("user edit", File.ReadAllText(path));
    }

    [Fact]
    public void AI_commit_recovers_audit_after_settings_save_and_is_idempotent()
    {
        var path = Path.Combine(_root, "settings.json");
        var auditPath = Path.Combine(_root, "ai-audit.jsonl");
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var cfg = new AppConfig();
        var baseline = JsonSerializer.Serialize(cfg, options);
        File.WriteAllText(path, baseline);
        cfg.Agent.DryRunsRemaining = 99;
        var audit = new AuditTrail(_root);
        Directory.CreateDirectory(auditPath); // Settings save succeeds; audit publication cannot.
        Assert.ThrowsAny<Exception>(() => AgentConfigCommit.Commit(path, baseline, cfg,
            [new AuditRow { RunId = "run", Applied = true }], _root,
            c => SecureFile.WriteAllTextRestricted(path, JsonSerializer.Serialize(c, options)), audit));
        Assert.Equal(99, JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)!.Agent.DryRunsRemaining);
        Assert.True(File.Exists(Path.Combine(_root, "pending-config-commit.json")));
        Directory.Delete(auditPath);
        AgentConfigCommit.Recover(path, _root, audit);
        AgentConfigCommit.Recover(path, _root, audit);
        Assert.Single(audit.ReadAll());
        Assert.False(File.Exists(Path.Combine(_root, "pending-config-commit.json")));
    }
}
