using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services.Automation;
using GlDrive.Spread;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class CertificateParkingRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trust-regression-" + Guid.NewGuid().ToString("N"));
    private string Pins => Path.Combine(_root, "pins.json");
    public CertificateParkingRegressionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    public async Task Parked_endpoint_is_not_scheduled_reinitialized_admitted_or_borrowed(string parkedId)
    {
        var manager = new CertificateManager(Pins);
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        var configs = new[] { "source", "destination" }.ToDictionary(id => id,
            id => new ServerConfig { Id = id, Connection = new() { Host = id + ".test", Port = 21 } });
        var pools = configs.ToDictionary(p => p.Key, p => new FtpConnectionPool(new FtpClientFactory(p.Value, manager), 1));
        try
        {
            await manager.ValidateCertificate(parkedId + ".test", 21, original, "ftp:" + parkedId);
            await manager.ValidateCertificate(parkedId + ".test", 21, changed, "ftp:" + parkedId);
            Assert.True(pools[parkedId].IsTrustParked);
            Assert.True(pools[parkedId].IsThrottled);
            var paths = configs.Keys.ToDictionary(id => id, id => "/fixture/Release");
            using var job = new SpreadJob("TV", "Fixture.S01E01-GROUP", SpreadMode.Race, new(), pools, pools,
                configs, new SpeedTracker(), new SkiplistEvaluator());
            Field<HashSet<string>>(job, "_sourceServersField").Add("source");
            var file = new SpreadFileInfo { Name = "fixture.r00", FullPath = "/fixture/Release/fixture.r00", Size = 1024 };
            Invoke(job, "ProcessFiles", "source", new List<SpreadFileInfo> { file }, 0L);
            job.ReconcileProgressFromOwnership();
            Assert.Null(Invoke(job, "FindBestTransfer", paths, new SpreadScorer(new SpeedTracker())));
            var gates = 0;
            job.AcquireTransferGates = (_, _, _) => { gates++; throw new InvalidOperationException("peer work started"); };
            for (var pass = 0; pass < 20; pass++)
                await (Task)Invoke(job, "ExecuteTransfer", file, "source", "destination", "/fixture/Release",
                    CancellationToken.None, CancellationToken.None, null!)!;
            Assert.Equal(0, gates);
            Assert.All(pools.Values, pool => Assert.Equal(0, pool.TotalCreated));
            Assert.True(Field<Dictionary<string, DateTime>>(job, "_poolRetryAt")[parkedId] > DateTime.UtcNow.AddSeconds(20));
            Assert.Equal(parkedId, Assert.Single(Field<HashSet<string>>(job, "_trustParkingLogged")));
            using var spread = new SpreadManager(new() { Servers = configs.Values.ToList() }, Path.Combine(_root, "race-state"));
            foreach (var p in pools) Field<Dictionary<string, FtpConnectionPool>>(spread, "_spreadPools").Add(p.Key, p.Value);
            await (Task)Invoke(spread, "ReinitDeadPools", (object)new[] { parkedId })!;
            Assert.Equal(0, pools[parkedId].ReinitCountSinceFlush);
            Assert.Equal(RaceStartOutcome.Unavailable, spread.StartRaceDetailed("TV", "Fixture.S01E01-GROUP",
                configs.Keys.ToArray(), SpreadMode.Race).Outcome);
        }
        finally { foreach (var pool in pools.Values) await pool.DisposeAsync(); }
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("proxy")]
    public async Task Configuration_patch_and_rebuilt_factory_cannot_clear_pending_trust(string change)
    {
        var manager = new CertificateManager(Pins);
        var server = new ServerConfig { Id = "fixture", Connection = new() { Host = "fixture.test", Port = 21 } };
        var config = new AppConfig { Servers = [server] };
        _ = new FtpClientFactory(server, manager);
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        await manager.ValidateCertificate("fixture.test", 21, original, "ftp:fixture");
        await manager.ValidateCertificate("fixture.test", 21, changed, "ftp:fixture");
        var writes = 0;
        var automation = new ConfigurationAutomation(config, _ => writes++, certificates: manager);
        var operation = change switch
        {
            "host" => """[{"op":"replace","path":"/servers/0/connection/host","value":"alias.test"}]""",
            "port" => """[{"op":"replace","path":"/servers/0/connection/port","value":2121}]""",
            _ => """[{"op":"add","path":"/servers/0/connection/proxy","value":{"enabled":true,"host":"proxy.test","port":1080}}]"""
        };
        if (change == "proxy") automation.Apply(JsonNode.Parse(operation)!.AsArray());
        else Assert.Throws<InvalidOperationException>(() => automation.Apply(JsonNode.Parse(operation)!.AsArray()));
        Assert.Equal(change == "proxy" ? 1 : 0, writes);
        Assert.Throws<CertificateTrustPendingException>(() => new FtpClientFactory(server, manager).Create());
        // Direct editor/remount mutation still inherits the stable identity hold.
        server.Connection.Host = "alias.test";
        var aliasFactory = new FtpClientFactory(server, manager);
        Assert.True(aliasFactory.IsTrustPending);
        Assert.Throws<CertificateTrustPendingException>(() => aliasFactory.Create());
        Assert.False(await manager.ValidateCertificate("alias.test", 21, changed, "ftp:fixture"));
        // The alias also carries the old pin on disk, so a restart cannot turn it into TOFU.
        var restarted = CopyForRestart();
        Assert.False(await restarted.ValidateCertificate("alias.test", 21, changed, "ftp:fixture"));
    }

    [Fact]
    public async Task Snapshot_restore_cannot_rekey_a_pending_endpoint()
    {
        var manager = new CertificateManager(Pins);
        var server = new ServerConfig { Id = "fixture", Connection = new() { Host = "alias.test", Port = 21 } };
        var config = new AppConfig { Servers = [server] };
        var writes = 0;
        var maintenance = new MaintenanceAutomation(config, manager, _root, _ => writes++);
        var snapshot = JsonSerializer.SerializeToNode(await maintenance.ExecuteAsync("config_snapshot_create", JsonSerializer.SerializeToElement(new { }), default))!;
        server.Connection.Host = "fixture.test";
        _ = new FtpClientFactory(server, manager);
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        await manager.ValidateCertificate("fixture.test", 21, original, "ftp:fixture");
        await manager.ValidateCertificate("fixture.test", 21, changed, "ftp:fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ExecuteAsync("config_snapshot_restore",
            JsonSerializer.SerializeToElement(new { name = snapshot["name"]!.GetValue<string>() }), default));
        Assert.Equal(0, writes);
        Assert.Equal("fixture.test", server.Connection.Host);
    }

    [Fact]
    public async Task Restarted_disabled_server_still_cannot_rekey_or_replace_its_identity()
    {
        var manager = new CertificateManager(Pins);
        var server = new ServerConfig { Id = "fixture", Connection = new() { Host = "fixture.test", Port = 21 } };
        _ = new FtpClientFactory(server, manager);
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        await manager.ValidateCertificate("fixture.test", 21, original, "ftp:fixture");
        await manager.ValidateCertificate("fixture.test", 21, changed, "ftp:fixture");
        server.Enabled = false;
        var restarted = CopyForRestart();
        var config = new AppConfig { Servers = [server] };
        var automation = new ConfigurationAutomation(config, _ => throw new Exception("must not persist"), certificates: restarted);
        Assert.Throws<InvalidOperationException>(() => automation.Apply(JsonNode.Parse("""
            [{"op":"replace","path":"/servers/0/connection/host","value":"alias.test"},
             {"op":"replace","path":"/servers/0/enabled","value":true}]
            """)!.AsArray()));
        Assert.Throws<InvalidOperationException>(() => automation.Apply(JsonNode.Parse("""
            [{"op":"replace","path":"/servers/0/id","value":"replacement"},
             {"op":"replace","path":"/servers/0/connection/host","value":"alias.test"}]
            """)!.AsArray()));
        Assert.False(await restarted.ValidateCertificate("fixture.test", 21, original, "ftp:fixture"));
        Assert.True(restarted.RetrySavedPinFromLocalUi(Assert.Single(restarted.GetPendingDecisions()).Id));
        Assert.True(await restarted.ValidateCertificate("fixture.test", 21, original, "ftp:fixture"));
    }

    [Theory]
    [InlineData("{\"endpoints\":null,\"unknownPriorEndpoints\":false}")]
    [InlineData("{\"endpoints\":{\"fixture.test:21\":null},\"unknownPriorEndpoints\":false}")]
    [InlineData("{\"endpoints\":{\"fixture.test:21\":[null]},\"unknownPriorEndpoints\":false}")]
    public async Task Malformed_endpoint_inventory_cannot_fail_open_or_abort_pin_loading(string inventory)
    {
        File.WriteAllText(Pins, "{torn");
        File.WriteAllText(Pins + ".endpoints", inventory);
        var manager = new CertificateManager(Pins);
        using var certificate = Certificate("presented");
        Assert.False(await manager.ValidateCertificate("unknown.test", 21, certificate));
        Assert.NotNull(Assert.Single(manager.GetPendingDecisions()).Explanation);
    }

    [Fact]
    public async Task Newer_index_retains_pending_identity_when_primary_pin_write_failed()
    {
        File.WriteAllText(Pins, """{"fixture.test:21":{"fingerprint":"OLD","identities":[]}}""");
        File.WriteAllText(Pins + ".endpoints", """
            {"endpoints":{"fixture.test:21":["ftp:fixture"]},"unknownPriorEndpoints":false,"pendingEndpoints":["fixture.test:21"]}
            """);
        var manager = new CertificateManager(Pins);
        var candidate = new AppConfig { Servers = [new() { Id = "fixture", Connection = new() { Host = "alias.test", Port = 21 } }] };
        Assert.Throws<InvalidOperationException>(() => manager.GuardConfiguration(candidate));
        _ = new FtpClientFactory(candidate.Servers[0], manager);
        using var changed = Certificate("changed");
        Assert.False(await manager.ValidateCertificate("alias.test", 21, changed, "ftp:fixture"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolving_original_decision_clears_inherited_alias_hold_without_erasing_alias_pin(bool accept)
    {
        var manager = new CertificateManager(Pins);
        var server = new ServerConfig { Id = "fixture", Connection = new() { Host = "fixture.test", Port = 21 } };
        _ = new FtpClientFactory(server, manager);
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        await manager.ValidateCertificate("fixture.test", 21, original, "ftp:fixture");
        await manager.ValidateCertificate("fixture.test", 21, changed, "ftp:fixture");
        var decision = Assert.Single(manager.GetPendingDecisions());
        manager.RegisterIdentity("ftp:fixture", "alias.test", 21);
        Assert.True(manager.IsPending("alias.test", 21, "ftp:fixture"));
        Assert.True(accept ? manager.ResolveFromLocalUi(decision.Id, true) : manager.RetrySavedPinFromLocalUi(decision.Id));
        manager.GuardConfiguration(new() { Servers = [server] });
        Assert.False(manager.IsPending("fixture.test", 21, "ftp:fixture"));
        var restarted = CopyForRestart();
        restarted.GuardConfiguration(new() { Servers = [server] });
        Assert.False(await restarted.ValidateCertificate("alias.test", 21, changed, "ftp:fixture"));
        Assert.Equal(manager.GetFingerprint(original), restarted.GetTrustedCertificates()["alias.test:21"].Fingerprint);
    }

    [Fact]
    public async Task Index_preserves_inherited_hold_provenance_after_failed_primary_write()
    {
        File.WriteAllText(Pins, """{"fixture.test:21":{"fingerprint":"OLD","identities":[]}}""");
        File.WriteAllText(Pins + ".endpoints", """
            {"endpoints":{"fixture.test:21":["ftp:fixture"],"alias.test:21":["ftp:fixture"]},
             "unknownPriorEndpoints":false,"pendingEndpoints":["fixture.test:21","alias.test:21"],
             "inheritedDecisions":{"alias.test:21":"fixture.test:21"}}
            """);
        var manager = new CertificateManager(Pins);
        using var changed = Certificate("changed");
        Assert.False(await manager.ValidateCertificate("fixture.test", 21, changed, "ftp:fixture"));
        Assert.True(manager.ResolveFromLocalUi(Assert.Single(manager.GetPendingDecisions()).Id, true));
        manager.GuardConfiguration(new() { Servers = [new() { Id = "fixture", Connection = new() { Host = "fixture.test", Port = 21 } }] });
        Assert.False(await manager.ValidateCertificate("alias.test", 21, changed, "ftp:fixture"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_pin_store_quarantines_and_requires_local_review_but_preserves_known_first_use(bool torn)
    {
        using var original = Certificate("original");
        using var changed = Certificate("changed");
        var json = """{"fixture.test:21":{"fingerprint":"OLD","notAfter":"not-a-date"}}""";
        if (torn)
        {
            File.WriteAllText(Pins + ".endpoints", """{"endpoints":{"fixture.test:21":[]},"unknownPriorEndpoints":false}""");
            json = "{torn";
        }
        File.WriteAllText(Pins, json);
        var manager = new CertificateManager(Pins);
        Assert.Equal(json, File.ReadAllText(Pins + ".corrupt"));
        Assert.False(await manager.ValidateCertificate("fixture.test", 21, changed));
        var decision = Assert.Single(manager.GetPendingDecisions());
        Assert.Contains("unreadable", decision.Explanation);
        Assert.False(decision.CanRetrySavedPin);
        Assert.False(manager.RetrySavedPinFromLocalUi(decision.Id));
        manager.ClearTrustedCertificates();
        Assert.True(manager.IsPending("fixture.test", 21));
        Assert.True(await manager.ValidateCertificate("new.test", 21, original));
        var restarted = CopyForRestart();
        Assert.False(await restarted.ValidateCertificate("fixture.test", 21, changed));
        Assert.True(await restarted.ValidateCertificate("another-new.test", 21, original));
        Assert.True(restarted.ResolveFromLocalUi(Assert.Single(restarted.GetPendingDecisions()).Id, true));
        Assert.True(await restarted.ValidateCertificate("fixture.test", 21, changed));
    }

    [Fact]
    public async Task Unindexed_unreadable_legacy_store_never_claims_unknown_endpoints_are_first_use()
    {
        File.WriteAllText(Pins, "{torn");
        using var certificate = Certificate("presented");
        var manager = new CertificateManager(Pins);
        Assert.False(await manager.ValidateCertificate("unknown.test", 21, certificate));
        var restarted = CopyForRestart();
        Assert.False(await restarted.ValidateCertificate("another.test", 21, certificate));
        Assert.Contains("unreadable", Assert.Single(restarted.GetPendingDecisions()).Explanation);
    }

    private CertificateManager CopyForRestart()
    {
        var copy = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.Copy(Pins, copy);
        File.Copy(Pins + ".endpoints", copy + ".endpoints");
        return new(copy);
    }
    private static X509Certificate2 Certificate(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
    private static object? Invoke(object target, string name, params object[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    public void Dispose() => Directory.Delete(_root, true);
}
