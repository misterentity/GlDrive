using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpcomingViewModelTests
{
    private static UpcomingViewModel Upcoming(WishlistStore? store = null, Action? wishlistChanged = null, Func<Task>? refresh = null) =>
        new(new AppConfig(), store ?? new WishlistStore(() => null, _ => { }), wishlistChanged ?? (() => { }), refresh ?? (() => Task.CompletedTask));

    // A dashboard without its constructor (which loads settings, stores and timers);
    // only the nested Upcoming view model is real.
    private static DashboardViewModel DashboardWith(UpcomingViewModel upcoming)
    {
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        typeof(DashboardViewModel).GetField("_upcoming", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, upcoming);
        return vm;
    }

    [Fact]
    public void Dashboard_screen_state_lists_upcoming_commands_and_rows_under_the_section_prefix()
    {
        var upcoming = Upcoming();
        upcoming.UpcomingTvEpisodes.Add(new UpcomingTvEpisodeVm { ShowName = "Fixture Show", TvMazeId = 7 });
        var state = JsonSerializer.SerializeToElement(UiAutomation.State(DashboardWith(upcoming)));

        var commands = state.GetProperty("commands").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("Upcoming.LoadUpcomingCommand", commands);
        Assert.Contains("Upcoming.SearchShowScheduleCommand", commands);
        Assert.Contains("Upcoming.ClearShowSearchCommand", commands);
        Assert.DoesNotContain("LoadUpcomingCommand", commands);
        // Wishlist writes and TMDB date lookups stay off the unattended allowlist.
        Assert.DoesNotContain("Upcoming.AddUpcomingToWishlistCommand", commands);

        var rows = state.GetProperty("state").GetProperty("Upcoming").GetProperty("UpcomingTvEpisodes");
        Assert.Equal("Fixture Show", Assert.Single(rows.EnumerateArray()).GetProperty("ShowName").GetString());
    }

    [Fact]
    public void Section_members_resolve_only_for_allowlisted_nested_view_models()
    {
        var upcoming = Upcoming();
        var dashboard = DashboardWith(upcoming);

        var (target, member) = UiAutomation.Locate(dashboard, "Upcoming.LoadUpcomingCommand");
        Assert.Same(upcoming, target);
        Assert.Equal("LoadUpcomingCommand", member);
        Assert.Same(dashboard, UiAutomation.Locate(dashboard, "SearchCommand").Target);

        foreach (var name in new[] { "Irc.SendCommand", "Plex.RefreshCommand", "Upcoming.", "Upcoming.A.B", ".LoadUpcomingCommand" })
            Assert.Throws<ArgumentException>(() => UiAutomation.Locate(dashboard, name));
    }

    [Fact]
    public async Task Adding_a_tv_episode_writes_the_shared_store_and_notifies_the_dashboard_once()
    {
        var store = new WishlistStore(() => null, _ => { });
        var refreshed = 0;
        var upcoming = Upcoming(store, () => refreshed++);
        upcoming.SelectedTvEpisode = new UpcomingTvEpisodeVm { ShowName = "Fixture Show", TvMazeId = 42 };
        upcoming.UpcomingQualityText = "1080p";

        await ((IAsyncRelayCommand)upcoming.AddUpcomingToWishlistCommand).ExecuteAsync(null);
        var item = Assert.Single(store.Items);
        Assert.Equal(MediaType.TvShow, item.Type);
        Assert.Equal(42, item.TvMazeId);
        Assert.Equal(1, refreshed);
        Assert.Equal("Added Fixture Show to wishlist", upcoming.UpcomingStatus);

        await ((IAsyncRelayCommand)upcoming.AddUpcomingToWishlistCommand).ExecuteAsync(null);
        Assert.Single(store.Items);
        Assert.Equal(1, refreshed);
        Assert.Equal("Fixture Show is already in wishlist", upcoming.UpcomingStatus);
    }

    [Fact]
    public async Task Release_dates_command_runs_the_dashboard_refresh_and_selection_is_exclusive()
    {
        var calls = 0;
        var upcoming = Upcoming(refresh: () => { calls++; return Task.CompletedTask; });
        await ((IAsyncRelayCommand)upcoming.RefreshUpcomingCalendarCommand).ExecuteAsync(null);
        Assert.Equal(1, calls);

        upcoming.SelectedTvEpisode = new UpcomingTvEpisodeVm { ShowName = "Show", EpisodeInfo = "S01E01 — Pilot" };
        upcoming.SelectedUpcomingMovie = new UpcomingMovieVm { Title = "Film", Year = "2026" };
        Assert.Null(upcoming.SelectedTvEpisode);
        Assert.Equal("Film (2026)", upcoming.UpcomingDetailTitle);

        var changed = 0;
        upcoming.PropertyChanged += (_, _) => changed++;
        upcoming.Dispose();
        upcoming.UpcomingStatus = "late";
        Assert.Equal(0, changed);
    }
}
