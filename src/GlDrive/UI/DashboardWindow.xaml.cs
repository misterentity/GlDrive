using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    private readonly AppConfig _config;
    private readonly ServerManager _serverManager;
    private bool _upcomingLoaded;
    private bool _preDbLoaded;
    private bool _worldMonitorLoaded;
    private bool _discordLoaded;
    private bool _streemsLoaded;
    private bool _playerLoaded;
    private Point _dragStartPoint;
    private PlayerViewModel? _playerVm;
    private Window? _fullscreenWindow;

    public DashboardWindow(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _config = config;
        _serverManager = serverManager;
        InitializeComponent();
        var vm = new DashboardViewModel(serverManager, config, notificationStore);
        DataContext = vm;
        Closed += (_, _) =>
        {
            vm.Dispose();
            _playerVm?.Dispose();
        };

        // Auto-scroll IRC messages
        vm.Irc.ScrollToBottom += () =>
        {
            if (IrcMessageList.Items.Count > 0)
                IrcMessageList.ScrollIntoView(IrcMessageList.Items[^1]);
        };

        // Re-focus input after sending
        vm.Irc.FocusInput += () => IrcInputBox.Focus();

        // Tab-complete nicks
        IrcInputBox.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Tab)
            {
                e.Handled = true;
                if (vm.Irc.HandleTabComplete())
                    IrcInputBox.CaretIndex = IrcInputBox.Text?.Length ?? 0;
            }
            else
            {
                vm.Irc.ResetTabComplete();
            }
        };

    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Find the TabControl and subscribe to selection changes
        if (Content is Grid grid && grid.Children[0] is TabControl tabControl)
        {
            tabControl.SelectionChanged += TabControl_SelectionChanged;
        }
    }

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tc) return;
        if (tc.SelectedItem is not TabItem tab) return;
        var header = tab.Header?.ToString();
        var vm = DataContext as DashboardViewModel;

        if (header == "Upcoming" && !_upcomingLoaded)
        {
            _upcomingLoaded = true;
            if (vm != null) await vm.LoadUpcoming();
        }

        if (vm != null)
            vm.IsPreDbTabActive = header == "PreDB";

        if (header == "PreDB" && !_preDbLoaded)
        {
            _preDbLoaded = true;
            if (vm != null) _ = vm.LoadLatestPreDb();
        }

        if (header == "Player" && !_playerLoaded)
        {
            _playerLoaded = true;
            _playerVm = new PlayerViewModel(_serverManager, _config);
            _playerVm.PlayerStatus = "Initializing player...";
            PlayerTab.DataContext = _playerVm;

            // Keyboard shortcuts for player
            PlayerTab.PreviewKeyDown += PlayerTab_KeyDown;

            // Enter key in search box triggers search
            PlayerSearchBox.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == System.Windows.Input.Key.Enter)
                {
                    ev.Handled = true;
                    _playerVm.SearchCommand.Execute(null);
                }
            };

            // Fullscreen handler
            _playerVm.FullscreenRequested += TogglePlayerFullscreen;

            // Auto-switch to Now Playing when user selects a card
            _playerVm.SwitchToNowPlaying += () =>
                Dispatcher.Invoke(() => NowPlayingMode.IsChecked = true);

            // Defer heavy VLC init so the tab appears instantly
            _ = Task.Run(() => _playerVm.InitVLC()).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    PlayerVideoView.MediaPlayer = _playerVm.Player;
                    _ = _playerVm.LoadTrending();
                });
            }, TaskScheduler.Default);
        }

        if (header == "World Monitor" && !_worldMonitorLoaded)
        {
            _worldMonitorLoaded = true;
            _ = WorldMonitorHost.InitializeAsync("https://www.worldmonitor.app/");
        }

        if (header == "Discord" && !_discordLoaded)
        {
            _discordLoaded = true;
            _ = DiscordHost.InitializeAsync("https://discord.com/app");
        }

        if (header == "Streems" && !_streemsLoaded)
        {
            _streemsLoaded = true;
            _ = StreemsHost.InitializeAsync("https://streems.redactor.site/");
        }
    }

    // Movie poster card clicked
    private void MovieCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MediaCardVm card && _playerVm != null)
            _playerVm.SelectedMovie = card;
    }

    // TV poster card clicked
    private void TvCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MediaCardVm card && _playerVm != null)
            _playerVm.SelectedTvShow = card;
    }

    // Double-click FTP result to play
    private void PlayerResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_playerVm?.PlayResultCommand is ICommand cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    private void TorrentResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_playerVm?.PlayTorrentCommand is ICommand cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    // Seek bar drag completed
    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider slider && _playerVm != null)
            _playerVm.SeekTo(slider.Value);
    }

    // Click-to-seek (IsMoveToPointEnabled handles the click position)
    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && _playerVm != null)
            _playerVm.SeekTo(slider.Value);
    }

    // Drag-and-drop: record start point
    private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    // Drag-and-drop: initiate drag if moved enough
    private void DragSource_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not DataGrid grid || grid.SelectedItem == null) return;

        var data = new DataObject("DashboardDragItem", grid.SelectedItem);
        DragDrop.DoDragDrop(grid, data, DragDropEffects.Copy);
    }

    // Keyboard shortcuts for the Player tab
    private void PlayerTab_KeyDown(object sender, KeyEventArgs e)
    {
        if (_playerVm == null) return;

        // Don't handle if typing in a text box
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Space:
                e.Handled = true;
                _playerVm.PlayPauseCommand.Execute(null);
                break;
            case System.Windows.Input.Key.F:
            case System.Windows.Input.Key.F11:
                e.Handled = true;
                TogglePlayerFullscreen();
                break;
            case System.Windows.Input.Key.Escape:
                e.Handled = true;
                ExitFullscreen();
                break;
            case System.Windows.Input.Key.Right:
                e.Handled = true;
                _playerVm.SeekForwardCommand.Execute(null);
                break;
            case System.Windows.Input.Key.Left:
                e.Handled = true;
                _playerVm.SeekBackwardCommand.Execute(null);
                break;
            case System.Windows.Input.Key.Up:
                e.Handled = true;
                _playerVm.Volume = Math.Min(100, _playerVm.Volume + 5);
                break;
            case System.Windows.Input.Key.Down:
                e.Handled = true;
                _playerVm.Volume = Math.Max(0, _playerVm.Volume - 5);
                break;
        }
    }

    // Double-click video area → fullscreen
    private void VideoArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            TogglePlayerFullscreen();
        }
    }

    private void TogglePlayerFullscreen()
    {
        if (_fullscreenWindow != null)
        {
            ExitFullscreen();
            return;
        }

        _fullscreenWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Background = System.Windows.Media.Brushes.Black,
            Title = "GlDrive Player"
        };

        // Move VideoView to fullscreen window
        var parent = PlayerVideoView.Parent as System.Windows.Controls.Panel;
        parent?.Children.Remove(PlayerVideoView);
        _fullscreenWindow.Content = PlayerVideoView;

        _fullscreenWindow.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape || e.Key == System.Windows.Input.Key.F ||
                e.Key == System.Windows.Input.Key.F11)
            {
                e.Handled = true;
                ExitFullscreen();
            }
            else if (e.Key == System.Windows.Input.Key.Space)
            {
                e.Handled = true;
                _playerVm?.PlayPauseCommand.Execute(null);
            }
            else if (e.Key == System.Windows.Input.Key.Right)
            {
                e.Handled = true;
                _playerVm?.SeekForwardCommand.Execute(null);
            }
            else if (e.Key == System.Windows.Input.Key.Left)
            {
                e.Handled = true;
                _playerVm?.SeekBackwardCommand.Execute(null);
            }
        };

        _fullscreenWindow.Closed += (_, _) => ExitFullscreen();
        _fullscreenWindow.Show();
    }

    private void ExitFullscreen()
    {
        if (_fullscreenWindow == null) return;

        // Move VideoView back to the player tab
        _fullscreenWindow.Content = null;
        if (PlayerVideoArea != null)
            PlayerVideoArea.Children.Insert(0, PlayerVideoView);

        _fullscreenWindow.Close();
        _fullscreenWindow = null;
    }

    // Search result card clicked
    private void SearchCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MediaCardVm card && _playerVm != null)
        {
            if (card.MediaType == "TV")
                _playerVm.SelectedTvShow = card;
            else
                _playerVm.SelectedMovie = card;
        }
    }

    // Library item clicked
    private void LibraryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LibraryItemVm item && _playerVm != null)
            _playerVm.PlayLibraryItemCommand.Execute(item);
    }

    // Episode clicked
    private void Episode_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TmdbEpisode ep && _playerVm != null)
            _playerVm.PlayEpisodeCommand.Execute(ep);
    }

    // Library mode selected — refresh library list
    private void LibraryMode_Checked(object sender, RoutedEventArgs e)
    {
        _playerVm?.LoadLibrary();
    }

        private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, _serverManager);
        window.Owner = this;
        window.ShowDialog();
    }

    // Drag-and-drop: handle drop on Downloads grid
    private void Downloads_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm) return;

        if (e.Data.GetData("DashboardDragItem") is NotificationItemVm)
        {
            vm.DownloadNotificationCommand.Execute(null);
        }
        else if (e.Data.GetData("DashboardDragItem") is SearchResultVm)
        {
            vm.DownloadSearchResultCommand.Execute(null);
        }
    }
}
