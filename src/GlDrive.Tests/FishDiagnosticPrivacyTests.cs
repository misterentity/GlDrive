using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Tls;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("FiSH diagnostic logger", DisableParallelization = true)]
public sealed class FishDiagnosticLoggerCollection { }

[Collection("FiSH diagnostic logger")]
public sealed class FishDiagnosticPrivacyTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _id = "diagnostic-test-" + Guid.NewGuid().ToString("N");
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly CaptureSink _sink = new();
    private readonly Logger _logger;
    private readonly IrcService _service;

    public FishDiagnosticPrivacyTests()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
        _service = new IrcService(new ServerConfig { Id = _id, Irc = new() { FishEnabled = true } },
            new CertificateManager(Path.Combine(Path.GetTempPath(), _id + ".json")));
    }

    [Theory]
    [InlineData("PRIVMSG", FishMode.ECB, "abcdWXYZ", false)]
    [InlineData("PRIVMSG", FishMode.CBC, "abcdWXYZ", false)]
    [InlineData("PRIVMSG", FishMode.ECB, "abcd-long-private-key-WXYZ", false)]
    [InlineData("PRIVMSG", FishMode.ECB, "abcdWXYZ", true)]
    [InlineData("PRIVMSG", FishMode.CBC, "abcd-long-private-key-WXYZ", true)]
    [InlineData("NOTICE", FishMode.ECB, "abcdWXYZ", true)]
    [InlineData("NOTICE", FishMode.CBC, "abcd-long-private-key-WXYZ", true)]
    public void MessageDiagnosticsNeverExposeKeyMaterial(string command, FishMode mode, string key, bool reject)
    {
        const string plain = "A private message that should remain readable after diagnostic changes.";
        _service.KeyStore.SetKey("#fixture", key, mode);
        var cipher = reject ? (mode == FishMode.CBC ? "+OK *!" : "+OK !") : FishCipher.Encrypt(plain, key, mode);
        var message = IrcMessage.Parse($":peer!u@fixture {command} #fixture :{cipher}");
        Invoke(command == "NOTICE" ? "HandleNotice" : "HandlePrivmsg", message);

        var displayed = _service.GetScrollback("#fixture").Last();
        Assert.Equal(!reject, displayed.WasEncrypted);
        if (!reject) Assert.Equal(plain, displayed.Text);
        Assert.Contains(_sink.Events, e => e.RenderMessage().Contains(reject ? "decrypt failed" : "decrypted="));
        AssertNoKeyMaterial(key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyExchangeDiagnosticsNeverExposeDerivedKeys(bool incomingInit)
    {
        var peer = new Dh1080();
        if (incomingInit)
        {
            // No socket is connected; NoticeAsync cannot send anything externally.
            typeof(IrcService).GetField("_client", Private)!.SetValue(_service, new IrcClient());
            await (Task)Invoke("HandleDh1080InitAsync", "peer", peer.GetPublicKeyBase64())!;
        }
        else
        {
            var pending = (Dictionary<string, (Dh1080, DateTime)>)typeof(IrcService)
                .GetField("_pendingKeyExchanges", Private)!.GetValue(_service)!;
            pending["peer"] = (new Dh1080(), DateTime.UtcNow);
            Invoke("HandleDh1080Finish", "peer", peer.GetPublicKeyBase64());
        }

        var stored = Assert.IsType<FishKeyEntry>(_service.KeyStore.GetKey("peer"));
        Assert.False(stored.Manual);
        Assert.NotEmpty(stored.Key);
        Assert.NotEmpty(stored.AltKey);
        Assert.Contains(_sink.Events, e => e.RenderMessage().Contains(incomingInit ? "FINISH send" : "derived for"));
        AssertNoKeyMaterial(stored.Key, stored.AltKey, stored.DhSecretHex);
    }

    private object? Invoke(string method, params object[] args) =>
        typeof(IrcService).GetMethod(method, Private)!.Invoke(_service, args);

    private void AssertNoKeyMaterial(params string[] keys)
    {
        // Check both rendered output and structured properties, which feed telemetry sinks.
        var output = string.Join("\n", _sink.Events.Select(e => e.RenderMessage() + " " +
            string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value))));
        Assert.DoesNotContain("keyMask=", output);
        Assert.DoesNotContain("primaryKey=", output);
        Assert.DoesNotContain("altKey=", output);
        foreach (var key in keys)
        {
            Assert.DoesNotContain(key, output);
            Assert.DoesNotContain(key[..4] + "..." + key[^4..], output);
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        Log.Logger = _previousLogger;
        _logger.Dispose();
        // Only this fixture's generated stores; no application configuration is changed.
        foreach (var prefix in new[] { "fish-keys-", "pm-history-" })
        {
            var path = Path.Combine(ConfigManager.AppDataPath, prefix + _id + ".json");
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
