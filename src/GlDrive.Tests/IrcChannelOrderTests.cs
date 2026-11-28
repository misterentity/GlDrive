using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public class IrcChannelOrderTests
{
    [Fact]
    public void Rows_sort_by_server_then_header_channels_and_pms_alphabetically()
    {
        var list = new List<IrcChannelVm>();
        foreach (var (server, name) in new[]
        {
            ("siteb", "#zeta"), ("siteb", "*"), ("sitea", "user9"), ("sitea", "#Spam"),
            ("siteb", "#alpha"), ("sitea", "*"), ("sitea", "#chat"), ("sitea", "Buddy"), ("SiteC", "*")
        })
        {
            var vm = new IrcChannelVm { ServerId = server, ServerName = server, Name = name };
            list.Insert(IrcChannelVm.SortedInsertIndex(list, vm), vm);
        }

        Assert.Equal(
            ["sitea:*", "sitea:#chat", "sitea:#Spam", "sitea:Buddy", "sitea:user9",
             "siteb:*", "siteb:#alpha", "siteb:#zeta", "SiteC:*"],
            list.Select(c => $"{c.ServerName}:{c.Name}"));
    }

    [Fact]
    public void Servers_sharing_a_name_stay_grouped_by_id()
    {
        var list = new List<IrcChannelVm>();
        foreach (var (id, name) in new[] { ("2", "#b"), ("1", "#z"), ("2", "#a"), ("1", "*") })
        {
            var vm = new IrcChannelVm { ServerId = id, ServerName = "same", Name = name };
            list.Insert(IrcChannelVm.SortedInsertIndex(list, vm), vm);
        }

        Assert.Equal(["1:*", "1:#z", "2:#a", "2:#b"], list.Select(c => $"{c.ServerId}:{c.Name}"));
    }
}
