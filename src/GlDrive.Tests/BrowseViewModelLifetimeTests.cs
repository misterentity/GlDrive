using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class BrowseViewModelLifetimeTests
{
    [Fact]
    public void Reopening_and_closing_browse_does_not_accumulate_server_subscribers()
    {
        // A synthetic empty manager avoids constructing stores or connecting any servers.
        var manager = (ServerManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerManager));
        typeof(ServerManager).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new ConcurrentDictionary<string, MountService>());
        var eventField = typeof(ServerManager).GetField("ServerStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

        for (var i = 0; i < 10; i++)
        {
            var vm = new BrowseViewModel(manager, new AppConfig());
            Assert.Single(((Delegate)eventField.GetValue(manager)!).GetInvocationList());
            vm.Dispose();
            vm.Dispose();
            Assert.Null(eventField.GetValue(manager));
        }
    }
}
