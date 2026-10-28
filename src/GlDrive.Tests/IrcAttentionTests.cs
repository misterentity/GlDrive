using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services.Alerts;
using GlDrive.Services.Automation;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class IrcAttentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "irc-attention-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_directory, "unread.dat");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public IrcAttentionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("hey Viewer!", "Viewer", true)]
    [InlineData("Viewer: hello", "viewer", true)]
    [InlineData("Viewers hello", "Viewer", false)]
    [InlineData("other-Viewer hello", "Viewer", false)]
    [InlineData("{User}: hello", "[User]", true)]
    [InlineData("literal a.b?", "a.b", true)]
    [InlineData("literal axb?", "a.b", false)]
    public void Mentions_use_literal_tokens_and_irc_case_mapping(string message, string nick, bool expected) =>
        Assert.Equal(expected, IrcHighlightMatcher.IsHighlight(message, "peer", [nick], new()));

    [Fact]
    public void Own_echoes_and_ignored_nicks_do_not_highlight()
    {
        var options = new IrcAttentionConfig { Keywords = ["wanted film"], IgnoredNicks = ["bot"] };
        Assert.False(IrcHighlightMatcher.IsHighlight("Viewer wanted film", "VIEWER", ["Viewer"], options));
        Assert.False(IrcHighlightMatcher.IsHighlight("Viewer wanted film", "BOT", ["Viewer"], options));
        Assert.True(IrcHighlightMatcher.IsHighlight("wanted film released", "peer", ["Viewer"], options));
        Assert.False(IrcHighlightMatcher.IsHighlight("unwanted films", "peer", ["Viewer"], options));
        options.NickHighlightsEnabled = false;
        Assert.False(IrcHighlightMatcher.IsHighlight("Viewer", "peer", ["Viewer"], options));
    }

    [Fact]
    public void Unread_persists_encrypted_and_mark_read_is_per_conversation()
    {
        using (var store = new IrcUnreadStore(StateFile))
        {
            store.Record("private-peer", true, DateTime.UtcNow);
            store.Record("#public", false, DateTime.UtcNow);
            store.Record("#PUBLIC", true, DateTime.UtcNow);
            store.Save();
        }
        Assert.DoesNotContain("private-peer", Encoding.UTF8.GetString(File.ReadAllBytes(StateFile)));
        using (var restored = new IrcUnreadStore(StateFile))
        {
            Assert.Equal(3, restored.Snapshot().Sum(e => e.Messages));
            Assert.Equal(2, restored.Snapshot().Sum(e => e.Highlights));
            restored.MarkRead("#PUBLIC"); restored.Save();
        }
        using var final = new IrcUnreadStore(StateFile);
        Assert.Equal("private-peer", Assert.Single(final.Snapshot()).Target);
    }

    [Fact]
    public void Corruption_starts_empty_and_preserves_evidence()
    {
        File.WriteAllText(StateFile, "broken");
        using var store = new IrcUnreadStore(StateFile);
        Assert.Empty(store.Snapshot());
        Assert.Equal("broken", File.ReadAllText(StateFile + ".corrupt"));
        store.Record("#channel", false, DateTime.UtcNow);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void Failed_write_keeps_memory_and_capacity_evicts_oldest()
    {
        using var store = new IrcUnreadStore(StateFile, 2, (_, _) => throw new IOException());
        for (var i = 0; i < 3; i++) { store.Record("peer" + i, true, DateTime.UnixEpoch.AddMinutes(i)); store.Save(); }
        Assert.Equal(new[] { "peer1", "peer2" }, store.Snapshot().Select(e => e.Target).Order());
    }

    [Fact]
    public void Oversized_bookkeeping_starts_empty_without_refusing_future_messages()
    {
        using (var file = File.Create(StateFile)) file.SetLength(4 * 1024 * 1024 + 1);
        using var store = new IrcUnreadStore(StateFile);
        Assert.Empty(store.Snapshot()); Assert.True(File.Exists(StateFile + ".corrupt"));
        store.Record("peer", true, DateTime.UtcNow);
        Assert.Single(store.Snapshot());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"nickHighlightsEnabled\":\"yes\"}")]
    [InlineData("{\"keywords\":[3]}")]
    [InlineData("{\"ignoredNicks\":null}")]
    public void Bad_optional_settings_do_not_reset_other_configuration(string raw)
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{\"downloads\":{\"qualityDefault\":\"2160p\"},\"ircAttention\":" + raw + "}", Json)!;
        Assert.Equal("2160p", config.Downloads.QualityDefault);
        Assert.False(config.IrcAttention.NickHighlightsEnabled);
        Assert.False(config.IrcAttention.PrivateMessageAlertsEnabled);
        Assert.Equal(raw, JsonNode.Parse(JsonSerializer.Serialize(config, Json))!["ircAttention"]?.ToJsonString(Json) ?? "null");
        ConfigurationAutomation.Validate(config);
    }

    [Fact]
    public void Invalid_attention_edits_are_rejected_but_existing_draft_allows_unrelated_changes()
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => { });
        var bad = JsonNode.Parse("""[{"op":"replace","path":"/ircAttention/nickHighlightsEnabled","value":"yes"}]""")!.AsArray();
        Assert.Throws<ArgumentException>(() => automation.Apply(bad));
        Assert.Throws<ArgumentException>(() => automation.Preview(bad));
        config.IrcAttention = JsonSerializer.Deserialize<IrcAttentionConfig>("{\"keywords\":123}", Json)!;
        automation.Apply(JsonNode.Parse("""[{"op":"replace","path":"/downloads/qualityDefault","value":"2160p"}]""")!.AsArray());
        Assert.Equal("2160p", config.Downloads.QualityDefault);
        Assert.NotNull(config.IrcAttention.UnparsedJson);
    }

    [Fact]
    public void Attention_configuration_is_never_a_credential()
    {
        foreach (var path in new[] { "/ircAttention", "/ircAttention/keywords/0", "/ircAttention/ignoredNicks/0", "/ircAttention/nickHighlightsEnabled" })
            Assert.False(ConfigSecretPointers.IsSecret(path));
        var config = JsonSerializer.Deserialize<AppConfig>("{}", Json)!;
        Assert.True(config.IrcAttention.NickHighlightsEnabled);
        Assert.True(config.IrcAttention.PrivateMessageAlertsEnabled);
        Assert.Empty(config.IrcAttention.Keywords);
    }

    [Theory]
    [InlineData(IrcMessageType.Normal, "peer", "hello", true)]
    [InlineData(IrcMessageType.Action, "peer", "waves", true)]
    [InlineData(IrcMessageType.Notice, "services", "registration complete", false)]
    [InlineData(IrcMessageType.System, "*", "connected", false)]
    public void Service_attention_distinguishes_private_messages_from_service_chatter(IrcMessageType type, string target, string text, bool expected)
    {
        using var store = new IrcUnreadStore(StateFile);
        var service = (IrcService)RuntimeHelpers.GetUninitializedObject(typeof(IrcService));
        void Set(string name, object value) => typeof(IrcService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
        Set("_serverConfig", new ServerConfig { Id = "fixture", Irc = new() { Nick = "viewer" } });
        Set("_currentNick", "viewer"); Set("_attention", (Func<IrcAttentionConfig>)(() => new())); Set("_unread", store);
        AlertEvent? emitted = null;
        service.AttentionReceived += alert => emitted = alert;
        var message = new IrcMessageItem { Nick = "peer", Type = type, Text = text };
        typeof(IrcService).GetMethod("TrackAttention", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [target, message]);
        Assert.Equal(expected, emitted != null); Assert.Equal(expected ? 1 : 0, store.Snapshot().Count);
        if (emitted != null) { Assert.True(emitted.LocalOnly); Assert.DoesNotContain(text, emitted.Message); Assert.Equal(AlertKind.IrcPrivateMessage, emitted.Kind); }
    }

    [Fact]
    public async Task Irc_alerts_remain_local_even_if_flag_is_missing_and_digest_target_enabled()
    {
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets = [new() { Id = "mail", Kind = "smtp", Enabled = true }] } };
        var trays = 0;
        await using var router = new AlertRouter(config, (_, _, _) => trays++, Path.Combine(_directory, "alerts.json"),
            (_, body, _) => { sent.TrySetResult(body); return Task.CompletedTask; }, () => []);
        router.Publish(new(AlertKind.IrcPrivateMessage, AlertSeverity.Information, "IRC", "private fixture"));
        router.Publish(new(AlertKind.IrcMention, AlertSeverity.Information, "IRC", "mention fixture", LocalOnly: true));
        router.Publish(new(AlertKind.Digest, AlertSeverity.Information, "Digest", ""));
        var delivered = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, trays);
        Assert.DoesNotContain("IrcPrivateMessage", delivered); Assert.DoesNotContain("IrcMention", delivered);
        Assert.DoesNotContain("private fixture", delivered); Assert.DoesNotContain("mention fixture", delivered);
        Assert.Equal(3, router.Store.Snapshot().Length);
    }

    [Fact]
    public void Hidden_and_disposed_readers_preserve_unread_while_active_reader_clears_only_selected()
    {
        using var store = new IrcUnreadStore(StateFile);
        static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        var service = Raw<IrcService>();
        Set(service, "_serverConfig", new ServerConfig { Id = "site", Name = "Fixture" }); Set(service, "_unread", store);
        var manager = Raw<ServerManager>();
        Set(manager, "_ircServices", new ConcurrentDictionary<string, IrcService>([new KeyValuePair<string, IrcService>("site", service)]));
        var vm = Raw<IrcViewModel>(); Set(vm, "_serverManager", manager);
        Set(vm, "_serviceHandlers", Activator.CreateInstance(typeof(IrcViewModel).GetField("_serviceHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType)!);
        Set(vm, "<Channels>k__BackingField", new ObservableCollection<IrcChannelVm>
        {
            new() { ServerId = "site", Name = "#selected" }, new() { ServerId = "site", Name = "#other" }
        });
        Set(vm, "_selectedChannel", vm.Channels[0]);
        var marker = store.Record("#selected", true, DateTime.UtcNow); store.Record("#other", true, DateTime.UtcNow);
        Set(vm, "<Messages>k__BackingField", new ObservableCollection<IrcMessageVm> { new() { UnreadMarker = marker } });
        vm.SetReading(false); Assert.Equal(2, store.Snapshot().Count);
        vm.SetReading(true); Assert.Equal("#other", Assert.Single(store.Snapshot()).Target);
        vm.Dispose();
        store.Record("#selected", true, DateTime.UtcNow);
        vm.SetReading(true); vm.MarkSelectedRead();
        typeof(IrcViewModel).GetMethod("OnIrcMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm,
            ["site", "Fixture", "#selected", new IrcMessageItem { Nick = "peer", Text = "hello" }]);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Theory]
    [InlineData(AlertKind.IrcMention)]
    [InlineData(AlertKind.IrcPrivateMessage)]
    public void Irc_kind_forces_local_policy(AlertKind kind) => Assert.True(AlertRouter.IsLocalOnly(new(kind, AlertSeverity.Information, "", "")));

    [Fact]
    public async Task Direct_target_delivers_sentinel_but_never_local_irc_events()
    {
        var sent = new ConcurrentQueue<string>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets = [new() { Id = "push", Kind = "ntfy", Enabled = true, MinimumSeverity = 0 }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, Path.Combine(_directory, "direct-alerts.json"),
            (_, body, _) => { sent.Enqueue(body); drained.TrySetResult(); return Task.CompletedTask; }, () => []);
        router.Publish(new(AlertKind.IrcPrivateMessage, AlertSeverity.Information, "IRC", "private fixture"));
        router.Publish(new(AlertKind.IrcMention, AlertSeverity.Information, "IRC", "mention fixture", LocalOnly: true));
        router.Publish(new(AlertKind.General, AlertSeverity.Information, "Local", "local fixture", LocalOnly: true));
        router.Publish(new(AlertKind.SiteRecovered, AlertSeverity.Information, "Sentinel", "ready"));
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var body = Assert.Single(sent);
        Assert.Contains("SiteRecovered", body);
        Assert.DoesNotContain("fixture", body);
    }

    [Fact]
    public void Pm_load_retains_existing_caps_and_dpapi_storage()
    {
        var path = Path.Combine(_directory, "pm.dat");
        var entries = Enumerable.Range(0, 52).ToDictionary(i => "peer" + i, i => Enumerable.Range(0, 202)
            .Select(j => new PmHistoryEntry { Timestamp = DateTime.UnixEpoch.AddMinutes(i * 1000 + j), Nick = "peer", Text = "private fixture " + j }).ToList());
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(entries, Json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, bytes);
        using var store = new PmHistoryStore(path, true);
        Assert.Equal(50, store.GetAll().Count);
        Assert.All(store.GetAll().Values, messages => Assert.Equal(200, messages.Count));
        Assert.DoesNotContain("peer0", store.GetAll().Keys);
    }
}
