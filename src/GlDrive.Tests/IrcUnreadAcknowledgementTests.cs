using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class IrcUnreadAcknowledgementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-unread-ack-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_root, "unread.dat");
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public IrcUnreadAcknowledgementTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Reading_first_message_preserves_newer_queued_message_and_its_highlight()
    {
        using var store = new IrcUnreadStore(StateFile);
        var first = store.Record("#channel", false, Now)!;
        var second = store.Record("#channel", true, Now.AddSeconds(1))!;
        store.ReadThrough("#channel", first);
        var row = Assert.Single(store.Snapshot());
        Assert.Equal(1, row.Messages);
        Assert.Equal(1, row.Highlights);
        store.ReadThrough("#channel", second);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Out_of_order_and_duplicate_acknowledgements_never_reintroduce_or_clear_newer_unread()
    {
        using var store = new IrcUnreadStore(StateFile);
        var first = store.Record("#channel", true, Now)!;
        var second = store.Record("#channel", false, Now.AddSeconds(1))!;
        store.Record("#channel", true, Now.AddSeconds(2));
        store.ReadThrough("#channel", second);
        store.ReadThrough("#channel", first);
        store.ReadThrough("#channel", second);
        var row = Assert.Single(store.Snapshot());
        Assert.Equal(1, row.Messages);
        Assert.Equal(1, row.Highlights);
    }

    [Fact]
    public void Old_marker_after_explicit_clear_cannot_acknowledge_new_generation()
    {
        using var store = new IrcUnreadStore(StateFile);
        var old = store.Record("#channel", true, Now)!;
        store.MarkRead("#CHANNEL");
        var current = store.Record("#channel", true, Now.AddSeconds(1))!;
        Assert.NotEqual(old.Generation, current.Generation);
        store.ReadThrough("#channel", old);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Messages);
    }

    [Fact]
    public void Eviction_and_reappearance_cannot_reuse_old_acknowledgement()
    {
        using var store = new IrcUnreadStore(StateFile, capacity: 1);
        var old = store.Record("#first", true, Now)!;
        store.Record("#second", false, Now.AddSeconds(1));
        var current = store.Record("#first", true, Now.AddSeconds(2))!;
        Assert.NotEqual(old.Generation, current.Generation);
        store.ReadThrough("#first", old);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Highlights);
    }

    [Fact]
    public void Marker_cannot_acknowledge_a_different_conversation()
    {
        using var store = new IrcUnreadStore(StateFile);
        var other = store.Record("#other", true, Now)!;
        store.Record("#selected", true, Now.AddSeconds(1));
        store.ReadThrough("#selected", other);
        Assert.All(store.Snapshot(), row => Assert.Equal(1, row.Messages));
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void Persisted_watermarks_preserve_unread_after_partial_read_and_restart()
    {
        IrcUnreadMarker first;
        using (var store = new IrcUnreadStore(StateFile))
        {
            first = store.Record("#channel", true, Now)!;
            store.Record("#channel", false, Now.AddSeconds(1));
            store.ReadThrough("#channel", first);
            store.Save();
        }
        using var restored = new IrcUnreadStore(StateFile);
        Assert.Equal(1, Assert.Single(restored.Snapshot()).Messages);
        Assert.Equal(0, Assert.Single(restored.Snapshot()).Highlights);
        var latest = restored.Record("#channel", true, Now.AddSeconds(2))!;
        Assert.NotEqual(first.Generation, latest.Generation);
        restored.ReadThrough("#channel", first);
        var unread = Assert.Single(restored.Snapshot());
        Assert.Equal(2, unread.Messages);
        Assert.Equal(1, unread.Highlights);
        restored.ReadThrough("#channel", latest);
        Assert.Empty(restored.Snapshot());
    }

    [Fact]
    public void Display_callback_then_tab_switch_retains_badge_for_later_callback()
    {
        using var store = new IrcUnreadStore(StateFile);
        using var vm = Reader(store);
        vm.SetReading(true);
        var first = new IrcMessageItem { Nick = "peer", Text = "first", UnreadMarker = store.Record("#channel", false, Now) };
        var second = new IrcMessageItem { Nick = "peer", Text = "second", IsHighlight = true,
            UnreadMarker = store.Record("#channel", true, Now.AddSeconds(1)) };

        Deliver(vm, first);
        Assert.Single(vm.Messages);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Messages);
        vm.SetReading(false);
        Deliver(vm, second);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Messages);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Highlights);
        Assert.Equal(" (1)", vm.UnreadDisplay);
        vm.SetReading(true);
        Assert.Empty(store.Snapshot());
        Assert.Equal("", vm.UnreadDisplay);
    }

    [Fact]
    public void Callback_from_replaced_service_cannot_display_or_acknowledge_current_service_messages()
    {
        using var store = new IrcUnreadStore(StateFile);
        using var vm = Reader(store);
        var manager = (ServerManager)Field(vm, "_serverManager").GetValue(vm)!;
        var current = manager.GetIrcService("site")!;
        var handlers = new Dictionary<string, (IrcService irc, Action<string, IrcMessageItem> msg,
            Action<string> names, Action<string, string> topic, Action<IrcServiceState> state)>
        {
            ["site"] = (current, (_, _) => { }, _ => { }, (_, _) => { }, _ => { })
        };
        Set(vm, "_serviceHandlers", handlers);
        var predecessor = Raw<IrcService>();
        Set(predecessor, "_serverConfig", new ServerConfig { Id = "site", Name = "Fixture" });
        vm.SetReading(true);
        var message = new IrcMessageItem { Nick = "peer", Text = "current", UnreadMarker = store.Record("#channel", true, Now) };
        vm.OnServiceMessage(predecessor, "#channel", message);
        Assert.Empty(vm.Messages);
        Assert.Equal(1, Assert.Single(store.Snapshot()).Messages);
        vm.OnServiceMessage(current, "#channel", message);
        Assert.Single(vm.Messages);
        Assert.Empty(store.Snapshot());
    }

    private static IrcViewModel Reader(IrcUnreadStore store)
    {
        var keys = Raw<FishKeyStore>();
        Set(keys, "_keyLock", new object());
        Set(keys, "_keys", new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase));
        var service = Raw<IrcService>();
        Set(service, "_serverConfig", new ServerConfig { Id = "site", Name = "Fixture" });
        Set(service, "_unread", store); Set(service, "_fishKeyStore", keys);
        var manager = Raw<ServerManager>();
        Set(manager, "_ircServices", new ConcurrentDictionary<string, IrcService>([new("site", service)]));
        var vm = Raw<IrcViewModel>();
        Set(vm, "_serverManager", manager);
        Set(vm, "_serviceHandlers", Activator.CreateInstance(Field(vm, "_serviceHandlers").FieldType)!);
        Set(vm, "_messageBuffers", new Dictionary<string, List<IrcMessageVm>>(StringComparer.OrdinalIgnoreCase));
        Set(vm, "_hydratedRecent", new HashSet<IrcMessageItem>());
        Set(vm, "<Channels>k__BackingField", new ObservableCollection<IrcChannelVm> { new() { ServerId = "site", Name = "#channel" } });
        Set(vm, "<Messages>k__BackingField", new ObservableCollection<IrcMessageVm>());
        Set(vm, "_selectedChannel", vm.Channels[0]);
        return vm;
    }
    private static void Deliver(IrcViewModel vm, IrcMessageItem item) => typeof(IrcViewModel)
        .GetMethod("OnIrcMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, ["site", "Fixture", "#channel", item]);
    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
}
