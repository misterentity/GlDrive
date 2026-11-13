using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RequestFillDestinationTests
{
    [Fact]
    public async Task Restored_expanded_fill_neither_uploads_to_nor_waits_for_an_unrequested_peer()
    {
        var paths = new Dictionary<string, string>
        {
            ["source"] = "/source/Release", ["requester"] = "/requests/REQUEST-Release/Release",
            ["peer"] = "/ordinary/Release"
        };
        var pools = paths.Keys.ToDictionary(id => id, _ => new FtpConnectionPool(null!, 1));
        try
        {
            using var job = new SpreadJob("TV", "Release", SpreadMode.Race, new SpreadConfig(), pools, pools,
                paths.Keys.ToDictionary(id => id, id => new ServerConfig { Id = id, Name = id }),
                new SpeedTracker(), new SkiplistEvaluator())
            {
                DestinationPaths = new Dictionary<string, string> { ["requester"] = paths["requester"] }
            };
            typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, paths);
            Field<HashSet<string>>(job, "_sourceServersField").Add("source");
            Observe("source", 10);
            Observe("peer", 5); // An old queued fill could have retained an ordinary partial receiver.
            Field<IDictionary<string, bool>>(job, "_destHasMissingStub")["peer"] = true;

            var next = Next();
            Assert.NotNull(next);
            Assert.Equal("requester", next.Value.dstId);
            Assert.False((bool)Invoke(job, "IsJobComplete")!);
            Observe("requester", 10);
            Assert.Null(Next());
            Assert.True((bool)Invoke(job, "IsJobComplete")!);
            Field<IDictionary<string, DestState>>(job, "_destStates")["requester"] = DestState.Complete;
            typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
            Assert.True(job.DeliveryOutcome().CleanComplete);

            void Observe(string id, long size) => Invoke(job, "ProcessFiles", id,
                new List<SpreadFileInfo> { new() { Name = "payload.rar", FullPath = paths[id] + "/payload.rar", Size = size } }, 0L);
            (SpreadFileInfo file, string srcId, string dstId)? Next() =>
                ((SpreadFileInfo file, string srcId, string dstId)?)Invoke(job, "FindBestTransfer", paths, new SpreadScorer(new SpeedTracker()));
        }
        finally
        {
            foreach (var pool in pools.Values) await pool.DisposeAsync();
        }
    }

    private static T Field<T>(SpreadJob job, string name) =>
        (T)typeof(SpreadJob).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!;
    private static object? Invoke(SpreadJob job, string name, params object[] args) =>
        typeof(SpreadJob).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, args);
}
