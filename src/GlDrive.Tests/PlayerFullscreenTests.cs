using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public sealed class PlayerFullscreenTests
{
    [Fact]
    public void Exit_restores_the_player_once_when_Close_reenters_through_Closed() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Enter();

        fixture.Exit();

        fixture.AssertRestored(1);
    });

    [Fact]
    public void Closing_the_fullscreen_window_externally_restores_the_player() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Enter();

        fixture.Fullscreen!.Close();

        fixture.AssertRestored(1);
    });

    [Fact]
    public void Repeated_exit_is_a_no_op_after_the_player_is_restored() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Exit();
        fixture.Enter();
        fixture.Exit();

        fixture.Exit();
        fixture.Exit();

        fixture.AssertRestored(1);
    });

    [Fact]
    public void The_same_player_can_enter_and_leave_fullscreen_repeatedly() => RunSta(() =>
    {
        using var fixture = new Fixture();
        for (var iteration = 1; iteration <= 5; iteration++)
        {
            fixture.Enter();
            fixture.Exit();
            fixture.AssertRestored(iteration);
        }
    });

    [Fact]
    public void Dashboard_close_can_exit_fullscreen_before_player_disposal() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var dashboard = Fixture.Offscreen(fixture.Area);
        var closed = false;
        dashboard.Closed += (_, _) =>
        {
            closed = true;
            fixture.Exit();
            fixture.AssertRestored(1);
        };
        try
        {
            dashboard.Show();
            fixture.Enter();

            dashboard.Close();

            Assert.True(closed);
            fixture.AssertRestored(1);
        }
        finally
        {
            if (!closed) dashboard.Close();
        }
    });

    private sealed class Fixture : IDisposable
    {
        internal readonly Grid Area = new();
        internal readonly Border Player = new();
        internal Window? Fullscreen;
        private readonly Border _overlay = new();
        private readonly List<Window> _windows = [];
        private int _closed;

        internal Fixture()
        {
            Area.Children.Add(Player);
            Area.Children.Add(_overlay);
        }

        internal void Enter()
        {
            Assert.Null(Fullscreen);
            Assert.True(Area.Children.Contains(Player));
            Area.Children.Remove(Player);
            Fullscreen = Offscreen(Player);
            _windows.Add(Fullscreen);
            Fullscreen.Closed += (_, _) => { _closed++; Exit(); };
            Fullscreen.Show();
            Assert.Same(Fullscreen, Player.Parent);
        }

        internal void Exit() => PlayerFullscreen.Exit(ref Fullscreen, Area, Player);

        internal void AssertRestored(int closed)
        {
            Assert.Null(Fullscreen);
            Assert.Same(Area, Player.Parent);
            Assert.Equal(2, Area.Children.Count);
            Assert.Same(Player, Area.Children[0]);
            Assert.Same(_overlay, Area.Children[1]);
            Assert.Equal(closed, _closed);
            foreach (var window in _windows)
            {
                Assert.Null(window.Content);
                Assert.False(window.IsVisible);
            }
        }

        public void Dispose()
        {
            Fullscreen = null;
            foreach (var window in _windows)
            {
                try { window.Content = null; window.Close(); }
                catch (InvalidOperationException) { }
            }
            Area.Children.Clear();
        }

        internal static Window Offscreen(object content) => new()
        {
            Content = content, Width = 160, Height = 100, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow
        };
    }

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Closed runs through a native WmDestroy callback. Capture dispatcher
            // faults as test failures instead of terminating the entire test host.
            Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
            {
                failure ??= args.Exception;
                args.Handled = true;
            };
            try { test(); }
            catch (Exception ex) { failure ??= ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Fullscreen lifecycle test did not finish");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
