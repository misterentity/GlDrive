using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class FeedBatchAdmissionTests
{
    [Theory]
    [InlineData("Download", 1, true, 0)]
    [InlineData("Race", 1, true, 0)]
    [InlineData("Download", 2, true, 1)]
    [InlineData("Race", 25, true, 1)]
    [InlineData("Download", 26, false, 0)]
    [InlineData("Race", 1000, false, 0)]
    [InlineData("Download", 0, false, 0)]
    public void Admission_checks_full_count_before_confirmation(string action, int count, bool accepted, int prompts)
    {
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        var asked = new List<(string, int)>();
        vm.ConfirmFeedAdmission = (kind, size) => { asked.Add((kind, size)); return true; };
        Assert.Equal(accepted, vm.TryApproveFeedAdmission(action, count));
        Assert.Equal(prompts, asked.Count);
        if (prompts > 0) Assert.Equal((action, count), asked[0]);
    }

    [Theory]
    [InlineData("DownloadNotification", 2)]
    [InlineData("RaceNotification", 2)]
    [InlineData("DownloadNotification", 26)]
    [InlineData("RaceNotification", 26)]
    public async Task Rejected_command_never_reaches_managers_or_admits_a_partial_selection(string command, int count)
    {
        // Managers intentionally absent: reaching any admission code is a failure.
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        vm.SelectedNotificationItems = Enumerable.Range(0, count).Select(i => new NotificationItemVm { Id = i.ToString() }).ToArray();
        vm.ConfirmFeedAdmission = (_, _) => false;
        await (Task)typeof(DashboardViewModel).GetMethod(command, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        Assert.Contains("no releases were queued", vm.BulkStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Running_batch_cannot_be_started_again_by_repeated_enter()
    {
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        typeof(DashboardViewModel).GetField("_feedAdmissionRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        vm.ConfirmFeedAdmission = (_, _) => throw new InvalidOperationException("No second prompt expected");
        Assert.False(vm.TryApproveFeedAdmission("Download", 2));
    }
}
