using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

// 2026-10-26: the nightly agent's config change remounted superbnc + zephyr at 04:03,
// replacing each IrcService under the same server id. The dashboard's id-keyed guard
// skipped the new instances, so their channels sat frozen all day while IRC was live.
public sealed class IrcViewModelResubscribeTests
{
    [Fact]
    public void Replaced_service_for_same_server_is_resubscribed_and_old_one_released()
    {
        var vm = EmptyIrcViewModel();
        var old = Service("bb90928a");
        var fresh = Service("bb90928a");

        Subscribe(vm, old);
        Assert.NotNull(Handler(old, "MessageReceived"));
        Buffers(vm)["bb90928a:#supers"] = [];
        Buffers(vm)["a8a08694:#ent"] = [];

        Subscribe(vm, fresh);

        foreach (var evt in new[] { "MessageReceived", "NamesUpdated", "TopicChanged", "StateChanged" })
        {
            Assert.Null(Handler(old, evt));
            Assert.NotNull(Handler(fresh, evt));
        }
        Assert.False(Buffers(vm).ContainsKey("bb90928a:#supers"));
        Assert.True(Buffers(vm).ContainsKey("a8a08694:#ent"));

        Subscribe(vm, fresh);
        Assert.Single(((MulticastDelegate)Handler(fresh, "MessageReceived")!).GetInvocationList());

        vm.Dispose();
        Assert.Null(Handler(fresh, "MessageReceived"));
    }

    private static IrcViewModel EmptyIrcViewModel()
    {
        var vm = Raw<IrcViewModel>();
        var manager = Raw<ServerManager>();
        Set(manager, "_ircServices", new ConcurrentDictionary<string, IrcService>());
        Set(vm, "_serverManager", manager);
        Set(vm, "_messageBuffers", new Dictionary<string, List<IrcMessageVm>>(StringComparer.OrdinalIgnoreCase));
        Set(vm, "_hydratedRecent", new HashSet<IrcMessageItem>());
        Set(vm, "_serviceHandlers", Activator.CreateInstance(Field(vm, "_serviceHandlers").FieldType)!);
        Set(vm, "<Channels>k__BackingField", new ObservableCollection<IrcChannelVm>());
        Set(vm, "<Messages>k__BackingField", new ObservableCollection<IrcMessageVm>());
        Set(vm, "<NickList>k__BackingField", new ObservableCollection<string>());
        return vm;
    }

    private static IrcService Service(string id)
    {
        var irc = Raw<IrcService>();
        Set(irc, "_serverConfig", new ServerConfig { Id = id, Name = id });
        return irc;
    }

    private static void Subscribe(IrcViewModel vm, IrcService irc) =>
        typeof(IrcViewModel).GetMethod("SubscribeToIrcService", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [irc]);

    private static Dictionary<string, List<IrcMessageVm>> Buffers(IrcViewModel vm) =>
        (Dictionary<string, List<IrcMessageVm>>)Field(vm, "_messageBuffers").GetValue(vm)!;

    private static Delegate? Handler(IrcService irc, string evt) => (Delegate?)Field(irc, evt).GetValue(irc);

    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
}
