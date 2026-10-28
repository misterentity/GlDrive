using System.IO;
using FluentFTP;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceOwnedNavigationTests
{
    [Fact]
    public async Task Existing_root_and_new_child_do_not_claim_root_ownership()
    {
        var created = new List<string>();
        var replies = new Queue<string>(["550", "250", "250", "250", "550", "257", "250"]);
        var result = await SpreadJob.NavigateOwnedPath((_, _) => Task.FromResult(new FtpReply { Code = replies.Dequeue() }),
            "/video/Release/CD1", default, created.Add);
        Assert.True(result.ok);
        Assert.Equal(["/video/Release/CD1"], created);
    }

    [Fact]
    public async Task Successful_root_mkd_is_recorded_before_followup_cwd()
    {
        var created = new List<string>();
        var replies = new Queue<string>(["550", "250", "250", "550", "257", "550"]);
        var result = await SpreadJob.NavigateOwnedPath((_, _) => Task.FromResult(new FtpReply { Code = replies.Dequeue() }),
            "/video/Release", default, created.Add);
        Assert.False(result.ok);
        Assert.Equal(["/video/Release"], created);
    }

    [Fact]
    public async Task Concurrent_creators_already_exists_reply_never_establishes_ownership()
    {
        var created = new List<string>();
        var replies = new Queue<FtpReply>([
            new() { Code = "550" }, new() { Code = "250" }, new() { Code = "250" },
            new() { Code = "550" }, new() { Code = "550", Message = "File already exists" }, new() { Code = "250" }
        ]);
        Assert.True((await SpreadJob.NavigateOwnedPath((_, _) => Task.FromResult(replies.Dequeue()),
            "/video/Release", default, created.Add)).ok);
        Assert.Empty(created);
    }

    [Fact]
    public async Task Failed_provenance_persistence_aborts_navigation_before_upload()
    {
        var commands = new List<string>();
        var replies = new Queue<string>(["550", "250", "250", "550", "257"]);
        await Assert.ThrowsAsync<IOException>(() => SpreadJob.NavigateOwnedPath((command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(new FtpReply { Code = replies.Dequeue() });
        }, "/video/Release", default, _ => throw new IOException("Controlled save failure")));
        Assert.Equal("MKD Release", commands.Last());
        Assert.DoesNotContain(commands, c => c.StartsWith("STOR"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandoned_multicommand_read_keeps_lease_until_actual_completion(bool fault)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var drain = SpreadJob.ReleaseAfterRead(read.Task, () => { Interlocked.Increment(ref released); return Task.CompletedTask; });
        Assert.Equal(0, released);
        Assert.False(drain.IsCompleted);
        if (fault) read.SetException(new IOException("Read failed")); else read.SetResult();
        await drain;
        Assert.Equal(1, released);
    }
}
