using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using GlDrive.Services.Alerts;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class IrcAttentionRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "irc-attention-regression-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public IrcAttentionRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private IrcUnreadStore Store() => new(Path.Combine(_root, Guid.NewGuid() + ".dat"));

    [Theory]
    [InlineData("#chat", "racebot", "viewer wins the race")]
    [InlineData("#feed", "peer", "viewer: ordinary chatter")]
    [InlineData("#chat", "peer", "[ NEW ] in [ FILM ] Example.Film.2026-GROUP by viewer")]
    [InlineData("#chat", "peer", "ANNOUNCE Example.Film.2026-GROUP by viewer")]
    [InlineData("racebot", "racebot", "viewer: status")]
    public void Automated_lines_never_create_highlights_or_notices(string target, string sender, string text)
    {
        using var store = Store();
        var config = new IrcConfig { Nick = "viewer", Channels = [new() { Name = "#feed", Sitebot = new() { BotNick = "RaceBot" } }],
            AnnounceRules = [new() { Channel = "#feed", Pattern = "unused" }, new() { Pattern = @"ANNOUNCE (?<release>\S+)" }] };
        var service = Service(store, config);
        var notices = 0;
        service.AttentionReceived += _ => notices++;
        var item = Track(service, target, sender, text);
        Assert.False(item.IsHighlight);
        Assert.Equal(0, Assert.Single(store.Snapshot()).Highlights);
        Assert.Equal(0, notices);
    }

    [Fact]
    public void Global_announce_rules_do_not_silence_ordinary_human_mentions()
    {
        using var store = Store();
        var service = Service(store, new() { Nick = "viewer", AnnounceRules = [new() { Pattern = @"ANNOUNCE (?<release>\S+)" }] });
        var notices = 0;
        service.AttentionReceived += _ => notices++;
        Assert.True(Track(service, "#chat", "peer", "viewer: hello").IsHighlight);
        Assert.Equal(1, notices);
    }

    [Theory]
    [InlineData(null, "alternate")]
    [InlineData("viewer", null)]
    [InlineData(null, null)]
    public void Null_configured_nicks_are_empty_and_do_not_disable_attention(string? nick, string? alternate)
    {
        using var store = Store();
        var service = Service(store, new() { Nick = nick!, AltNick = alternate! });
        Set(service, "_currentNick", null);
        var notices = 0;
        service.AttentionReceived += _ => notices++;
        Track(service, "peer", "peer", "hello");
        Assert.Equal(1, Assert.Single(store.Snapshot()).Highlights);
        Assert.Equal(1, notices);
        Assert.Equal(0, Field(service, "_attentionWarned").GetValue(service));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Service_throttles_before_both_plain_tray_and_enabled_alert_router(bool enabled)
    {
        using var store = Store();
        var service = Service(store, new() { Nick = "viewer" });
        var trays = new List<string>();
        using var router = new AlertRouter(new() { Alerts = new() { Enabled = enabled } },
            (_, message, _) => trays.Add(message), Path.Combine(_root, "alerts.json"), (_, _, _) => Task.CompletedTask, () => []);
        service.AttentionReceived += router.Publish;
        for (var i = 0; i < 100; i++) Track(service, i % 2 == 0 ? "Peer" : "peer", "peer", "private text");
        Track(service, "other", "other", "private text");
        Assert.Equal(2, trays.Count);
        Assert.All(trays, text => { Assert.Contains("1", text); Assert.DoesNotContain("private text", text); });
        Assert.Equal(101, store.Snapshot().Sum(e => e.Highlights));
        Assert.Equal(enabled ? 2 : 0, router.Store.Snapshot().Length);
    }

    [Fact]
    public void Throttle_reports_suppressed_count_at_exact_interval_and_keeps_targets_independent()
    {
        var throttle = new IrcAttentionThrottle();
        Assert.Equal(1, throttle.Record("[peer]", Now));
        for (var i = 0; i < 12; i++) Assert.Equal(0, throttle.Record("{PEER}", Now.AddSeconds(i + 1)));
        Assert.Equal(1, throttle.Record("other", Now.AddSeconds(2)));
        Assert.Equal(0, throttle.Record("[peer]", Now.AddMinutes(10).AddTicks(-1)));
        Assert.Equal(14, throttle.Record("[peer]", Now.AddMinutes(10)));
        Assert.Equal(0, throttle.Record("[peer]", Now.AddMinutes(10).AddSeconds(1)));
    }

    [Fact]
    public void Throttle_capacity_evicts_oldest_without_refusing_new_attention()
    {
        var throttle = new IrcAttentionThrottle(2);
        Assert.Equal(1, throttle.Record("first", Now));
        Assert.Equal(1, throttle.Record("second", Now.AddSeconds(1)));
        Assert.Equal(1, throttle.Record("third", Now.AddSeconds(2)));
        Assert.Equal(0, throttle.Record("second", Now.AddSeconds(3)));
        Assert.Equal(1, throttle.Record("first", Now.AddSeconds(4)));
    }

    [Fact]
    public void Prominent_badge_counts_attention_and_live_updates_touch_only_affected_row()
    {
        using var store = Store();
        var service = Service(store, new());
        using var vm = Reader(service);
        var chat = new IrcChannelVm { ServerId = "site", Name = "#chat" };
        var other = new IrcChannelVm { ServerId = "site", Name = "#other" };
        vm.Channels.Add(chat); vm.Channels.Add(other);
        for (var i = 0; i < 1000; i++) store.Record("#chat", false, Now);
        var changes = 0;
        other.PropertyChanged += (_, _) => changes++;
        Deliver(vm, "#chat", new() { Nick = "bot", Text = "announce" });
        Assert.Equal(1000, chat.UnreadCount);
        Assert.Equal("", vm.UnreadDisplay);
        store.Record("#chat", true, Now);
        Deliver(vm, "#chat", new() { Nick = "peer", Text = "viewer" });
        store.Record("peer", true, Now);
        Deliver(vm, "peer", new() { Nick = "peer", Text = "hello" });
        Assert.Equal(" (2)", vm.UnreadDisplay);
        Assert.EndsWith(" (2)", vm.CompactStatusText);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Unchanged_sidebar_values_do_not_raise_property_notifications()
    {
        var row = new IrcChannelVm { UnreadCount = 5, HighlightCount = 2, HasFishKey = true };
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        row.UnreadCount = 5; row.HighlightCount = 2; row.HasFishKey = true;
        Assert.Equal(0, changes);
        row.UnreadCount = 6; row.HighlightCount = 3;
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Restored_unread_and_scrollback_do_not_recreate_departed_channel()
    {
        using var store = Store();
        store.Record("#departed", true, Now);
        var service = Service(store, new());
        Set(service, "_channels", new Dictionary<string, IrcChannel>(StringComparer.OrdinalIgnoreCase) { ["#joined"] = new() { Name = "#joined" } });
        var scrollback = new IrcScrollbackBuffer();
        foreach (var target in new[] { "#departed", "#joined", "peer" }) scrollback.Append(target, new() { Nick = "peer", Text = "hello" });
        Set(service, "_scrollback", scrollback);
        using var vm = Reader(service);
        typeof(IrcViewModel).GetMethod("HydrateFromScrollback", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [service]);
        typeof(IrcViewModel).GetMethod("RefreshUnread", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(vm, null);
        Assert.Equal(new[] { "#joined", "peer" }, vm.Channels.Select(c => c.Name));
        Assert.Equal("", vm.UnreadDisplay);
    }

    private static IrcService Service(IrcUnreadStore store, IrcConfig config)
    {
        var service = Raw<IrcService>();
        Set(service, "_serverConfig", new ServerConfig { Id = "site", Name = "Fixture", Irc = config });
        Set(service, "_currentNick", "viewer"); Set(service, "_unread", store);
        Set(service, "_attention", (Func<IrcAttentionConfig>)(() => new()));
        var keys = Raw<FishKeyStore>();
        Set(keys, "_keyLock", new object()); Set(keys, "_keys", new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase));
        Set(service, "_fishKeyStore", keys);
        return service;
    }
    private static IrcMessageItem Track(IrcService service, string target, string sender, string text)
    {
        var item = new IrcMessageItem { Nick = sender, Text = text };
        typeof(IrcService).GetMethod("TrackAttention", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [target, item]);
        return item;
    }
    private static IrcViewModel Reader(IrcService service)
    {
        var manager = Raw<ServerManager>();
        Set(manager, "_ircServices", new ConcurrentDictionary<string, IrcService>([new("site", service)]));
        var vm = Raw<IrcViewModel>();
        Set(vm, "_serverManager", manager);
        Set(vm, "_serviceHandlers", Activator.CreateInstance(Field(vm, "_serviceHandlers").FieldType)!);
        Set(vm, "_messageBuffers", new Dictionary<string, List<IrcMessageVm>>(StringComparer.OrdinalIgnoreCase));
        Set(vm, "_hydratedRecent", new HashSet<IrcMessageItem>());
        Set(vm, "<Channels>k__BackingField", new ObservableCollection<IrcChannelVm>());
        Set(vm, "<Messages>k__BackingField", new ObservableCollection<IrcMessageVm>());
        return vm;
    }
    private static void Deliver(IrcViewModel vm, string target, IrcMessageItem item) => typeof(IrcViewModel)
        .GetMethod("OnIrcMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, ["site", "Fixture", target, item]);
    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(object target, string name, object? value) => Field(target, name).SetValue(target, value);
}
