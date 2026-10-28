using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class BulkUiSchedulingTests
{
    [Fact]
    public void Wishlist_undo_cancels_removal_without_restoring_over_concurrent_evidence()
    {
        var store = new WishlistStore(() => null, _ => { });
        var item = new WishlistItem { Id = "chosen", Title = "Fixture" };
        store.Add(item);
        using var pending = new DeferredBulkAction();
        var vm = ViewModel(store, pending);
        vm.SelectedWishlistItems = [new() { Id = item.Id }];
        Invoke(vm, "ScheduleWishlistRemoval");
        Assert.NotNull(store.GetById(item.Id)); Assert.True(vm.HasPendingBulk);
        var owned = new WishlistCopy { DownloadId = "completed", ReleaseName = "Fixture.2026.1080p.WEB-GROUP" };
        item.BestOwned.Add(owned); store.Update(item);
        Assert.True(pending.Undo());
        Assert.False(pending.RunDue(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Same(item, store.GetById(item.Id)); Assert.Same(owned, Assert.Single(item.BestOwned));
    }

    [Fact]
    public void Wishlist_expired_removal_uses_captured_ids_and_preserves_later_additions()
    {
        var store = new WishlistStore(() => null, _ => { });
        store.Add(new() { Id = "chosen", Title = "Fixture" });
        using var pending = new DeferredBulkAction();
        var vm = ViewModel(store, pending);
        vm.SelectedWishlistItems = [new() { Id = "chosen" }];
        Invoke(vm, "ScheduleWishlistRemoval");
        store.Add(new() { Id = "later", Title = "Later Fixture" });
        vm.SelectedWishlistItems = [new() { Id = "later" }];
        Assert.True(pending.RunDue(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Null(store.GetById("chosen")); Assert.NotNull(store.GetById("later"));
        Assert.Equal("later", Assert.Single(vm.WishlistItems).Id);
    }

    private static DashboardViewModel ViewModel(WishlistStore store, DeferredBulkAction pending)
    {
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        Set(vm, "_wishlistStore", store); Set(vm, "_bulkAction", pending);
        Set(vm, "<WishlistItems>k__BackingField", new ObservableCollection<WishlistItemVm>());
        return vm;
    }
    private static void Set(object target, string name, object value) => typeof(DashboardViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name) => typeof(DashboardViewModel)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
