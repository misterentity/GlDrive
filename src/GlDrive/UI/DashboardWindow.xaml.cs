using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    private readonly AppConfig _config;
    private readonly ServerManager _serverManager;
    private bool _upcomingLoaded;
    private bool _preDbLoaded;
    private bool _spreadLoaded;
    private bool _browseLoaded;
    private bool _worldMonitorLoaded;
    private bool _discordLoaded;
    private bool _streemsLoaded;
    private bool _playerLoaded;
    private Point _dragStartPoint;
    private PlayerViewModel? _playerVm;
    private SpreadViewModel? _spreadVm;
    private BrowseViewModel? _browseVm;
    private Window? _fullscreenWindow;
    private DispatcherTimer? _clockTimer;
    private IInputElement? _shortcutsReturnFocus;
    private readonly bool _demoMode;
    private readonly AutoExpandLatch _streamsExpandLatch = new();
    private bool _expandingStreams;

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        new LogConsoleWindow(_config) { Owner = this }.Show();
    }

    private void Problems_Click(object sender, RoutedEventArgs e)
    {
        if (!_demoMode && App.Problems is { } problems)
            new ProblemsWindow(problems) { Owner = this }.Show();
    }

    private void Statistics_Click(object sender, RoutedEventArgs e)
    {
        if (!_demoMode && App.Statistics is { } statistics)
            new StatisticsWindow(statistics) { Owner = this }.Show();
    }

    private void Dossier_Click(object sender, RoutedEventArgs e)
    {
        if (!_demoMode && App.Dossiers is { } dossier) new ReleaseDossierWindow(dossier) { Owner = this }.Show();
    }

    private void OpsJournal_Click(object sender, RoutedEventArgs e)
    {
        if (!_demoMode && App.Operations is { } journal) new OpsJournalWindow(journal) { Owner = this }.Show();
    }

    private void CollectionGaps_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || DataContext is not DashboardViewModel vm) return;
        new CollectionGapsWindow(_serverManager.Collections, vm.SelectedWishlistMovieTmdbId) { Owner = this }.Show();
    }

    private void UpdateIrcReading()
    {
        if (_demoMode) return;
        if (DataContext is DashboardViewModel vm)
            vm.Irc.SetReading(IsActive && IsVisible && MainTabControl.SelectedItem is TabItem tab && GetTabName(tab) == "IRC");
    }

    private void IrcMarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.Irc.MarkSelectedRead();
    }

    private void IrcSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || DataContext is not DashboardViewModel vm || vm.Irc.SelectedChannel is not { } selected) return;
        if (_serverManager.GetIrcService(selected.ServerId) is { } service)
            new IrcSearchWindow(service) { Owner = this }.Show();
    }

    public DashboardWindow(ServerManager serverManager, AppConfig config, NotificationStore notificationStore, bool demoMode = false)
    {
        _demoMode = demoMode;
        _config = config;
        _serverManager = serverManager;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        var vm = new DashboardViewModel(serverManager, config, notificationStore);
        DataContext = vm;
        Activated += (_, _) => UpdateIrcReading();
        Deactivated += (_, _) => vm.Irc.SetReading(false);
        vm.PropertyChanged += Vm_StreamMatrixChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= Vm_StreamMatrixChanged;
            vm.Dispose();
            _playerVm?.Dispose();
            _spreadVm?.Dispose();
            _browseVm?.Dispose();
        };

        // Auto-scroll IRC messages
        vm.Irc.ScrollToBottom += ScrollIrcToBottom;

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

        // Live UTC clock in the top-right corner. 1-second tick is fine —
        // resolution-wise this just changes the seconds digit, no animation.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) =>
            UtcClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss") + " UTC";
        _clockTimer.Start();
        UtcClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss") + " UTC";

        Closed += (_, _) =>
        {
            _clockTimer?.Stop();
            _clockTimer = null;
        };

        // Toggle keyboard-shortcuts overlay with `?` (Shift+/) and dismiss with Esc.
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control &&
                MainTabControl.SelectedItem is TabItem ircTab && GetTabName(ircTab) == "IRC")
            {
                IrcSearch_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && ShortcutsOverlay.Visibility == Visibility.Visible)
            {
                ToggleShortcuts();
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.OemQuestion
                && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                // Don't fire while typing in a TextBox / ComboBox / TextBox-like
                if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
                if (Keyboard.FocusedElement is System.Windows.Controls.ComboBox) return;
                ToggleShortcuts();
                e.Handled = true;
            }
        };
    }

    private void ShortcutsOverlay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ToggleShortcuts();
        e.Handled = true;
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        ToggleShortcuts();
        e.Handled = true;
    }

    private void ToggleShortcuts()
    {
        if (ShortcutsOverlay.Visibility == Visibility.Visible)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (_shortcutsReturnFocus != null) Keyboard.Focus(_shortcutsReturnFocus);
        }
        else
        {
            _shortcutsReturnFocus = Keyboard.FocusedElement;
            ShortcutsOverlay.Visibility = Visibility.Visible;
            ShortcutsOverlay.Focus();
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Find the named TabControl directly rather than walking Content.Children[0];
        // name lookup does not depend on the shell's layout.
        MainTabControl.SelectionChanged += TabControl_SelectionChanged;
        if (MainTabControl.SelectedItem is TabItem tab)
        {
            UpdateSectionHeading(GetTabName(tab));
            if (!_demoMode && ReferenceEquals(tab, OverviewTab) && DataContext is DashboardViewModel vm)
                vm.RefreshOverview();
        }
    }

    private void UpdateSectionHeading(string? name)
    {
        CurrentSectionText.Text = name ?? "Overview";
        CurrentSectionHint.Text = name switch
        {
            "Overview" => "Connections, transfers, and app activity at a glance.",
            "Notifications" => "Discover new releases from your connected servers.",
            "Wishlist" => "Track movies and shows and download matching releases automatically.",
            "Downloads" => "Follow progress, retry interrupted transfers, and open your files.",
            "Search" => "Find releases across connected servers. Enter a title below to begin.",
            "Player" => "Browse media, choose a release, and watch it here.",
            "Upcoming" => "Explore upcoming episodes and movies, then add them to your wishlist.",
            "Plex" => "Connect your Plex account to manage library access and invitations.",
            "IRC" => "Choose a channel to read or send messages. Enter sends your message.",
            "PreDB" => "Search release announcements, then find them on your servers.",
            "Spread" => "Transfer releases between servers and monitor active races.",
            "Browse" => "Browse two servers side by side and transfer selected files between them.",
            "Mounts" => "Manage server connections, mounted drives, and trusted certificates.",
            "AI Agent" => "Review recommendations, run history, and configuration changes.",
            _ => ""
        };
    }

    // The v1.72 sidebar nav uses structured Grid headers (number prefix + label)
    // instead of plain string headers. tab.Header?.ToString() now returns
    // "System.Windows.Controls.Grid", which broke every `header == "..."`
    // comparison below. Walk the Grid to find the label TextBlock and return its
    // text. Fallback to ToString() for any TabItem that still uses string Header.
    private static string? GetTabName(TabItem tab)
    {
        if (tab.Header is string s) return s;
        if (tab.Header is System.Windows.Controls.Grid g)
        {
            foreach (var child in g.Children)
            {
                if (child is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    // The number prefix is 2 digits like "07"; the label is what we want.
                    if (tb.Text.Length > 2 || !char.IsDigit(tb.Text[0]))
                        return tb.Text;
                }
            }
        }
        return tab.Header?.ToString();
    }

    // The IRC message ListBox only realizes its items once its tab is shown, so the
    // VM's ctor-time scroll (and any scroll while another tab is active) is a no-op —
    // the hydrated backlog would open scrolled to the TOP. Scroll to the newest line
    // once when the tab becomes active, deferred until the list has laid out.
    private void ScrollIrcToBottom()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IrcMessageList.Items.Count > 0)
                IrcMessageList.ScrollIntoView(IrcMessageList.Items[^1]);
        }));
    }

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabControl)) return;
        var tc = MainTabControl;
        if (tc.SelectedItem is not TabItem tab) return;
        var header = GetTabName(tab);
        UpdateSectionHeading(header);
        // Rendering demo screens must not initialize network clients or load private logs.
        if (_demoMode) return;
        var vm = DataContext as DashboardViewModel;

        UpdateIrcReading();

        if (header == "IRC")
            ScrollIrcToBottom();

        if (header == "Overview" && vm != null)
        {
            // Refresh whenever the tab becomes active — cheap (a directory scan
            // + log tail) and keeps the operations log preview current.
            vm.RefreshOverview();
        }

        if (header == "Mounts" && vm != null)
        {
            vm.RefreshMounts();
            vm.RefreshOverview();  // capacity placeholders share the OverviewServerVm
        }

        if (header == "Downloads" && vm != null)
            vm.RefreshDownloadQueue();

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
            _playerVm.DownloadsRequested += () =>
            {
                vm?.RefreshDownloadQueue();
                var downloads = MainTabControl.Items.OfType<TabItem>().First(t => GetTabName(t) == "Downloads");
                MainTabControl.SelectedItem = downloads;
            };
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
            _ = Task.Run(() =>
            {
                _playerVm.InitVLC();
            }).ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_playerVm.Player != null)
                    {
                        PlayerVideoView.MediaPlayer = _playerVm.Player;
                        Log.Information("VLC MediaPlayer connected to VideoView");
                    }
                    else
                    {
                        Log.Warning("VLC init failed — Player is null, VideoView not connected");
                    }
                    _ = _playerVm.LoadTrending();
                });
            }, TaskScheduler.Default);
        }

        if (header == "Spread" && !_spreadLoaded)
        {
            _spreadLoaded = true;
            _spreadVm = new SpreadViewModel(_serverManager, _config);
            if (vm != null) _spreadVm.SetBulkScheduler(vm.TryScheduleBulk);
            _spreadVm.SetOpenSettingsAction(async () =>
            {
                var window = new SettingsWindow(_config, _serverManager) { Owner = this };
                if (window.ShowDialog() == true)
                    await _serverManager.SyncAfterConfigChange();
                _spreadVm?.RefreshSections();
            });
            SpreadTab.DataContext = _spreadVm;
            _spreadVm.WireAutoRaceEvents();
        }

        // Activate/deactivate spread timer only when tab is visible
        if (header == "Spread")
            _spreadVm?.Activate();
        else
            _spreadVm?.Deactivate();

        if (header == "Browse" && !_browseLoaded)
        {
            _browseLoaded = true;
            _browseVm = new BrowseViewModel(_serverManager, _config);
            BrowseTab.DataContext = _browseVm;
        }

        if (header == "World Monitor" && !_worldMonitorLoaded)
        {
            _worldMonitorLoaded = true;
            _ = WorldMonitorHost.InitializeAsync("https://www.worldmonitor.app/");
        }

        if (header == "Discord" && !_discordLoaded)
        {
            _discordLoaded = true;
            _ = DiscordHost.InitializeAsync("https://discord.com/app", allowCrossOrigin: true);
        }

        if (header == "Streems" && !_streemsLoaded)
        {
            _streemsLoaded = true;
            _ = StreemsHost.InitializeAsync("https://streems.redactor.site/", allowCrossOrigin: true);
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

    private void CastButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
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

    // Auto-open the stream matrix when a multi-stream transfer starts, unless the user closed it.
    private void Vm_StreamMatrixChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardViewModel.HasStreamMatrix) || sender is not DashboardViewModel vm) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Vm_StreamMatrixChanged(sender, e));
            return;
        }
        if (!_streamsExpandLatch.SignalChanged(vm.HasStreamMatrix) || StreamsExpander.IsExpanded) return;
        _expandingStreams = true;
        try { StreamsExpander.IsExpanded = true; }
        finally { _expandingStreams = false; }
    }

    private void StreamsExpander_Toggled(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, StreamsExpander) || _expandingStreams) return;
        _streamsExpandLatch.UserToggled(StreamsExpander.IsExpanded);
    }

    // "Clear ▾": the four clear commands live in the button's context menu (also reachable
    // with Shift+F10 / the Menu key); Enter, Space or a click opens it under the button.
    private async void StreamMatrix_ConfigRequested(object? sender, EventArgs e)
    {
        var window = new SettingsWindow(_config, _serverManager, initialTab: SettingsWindow.StreamsTab) { Owner = this };
        if (window.ShowDialog() == true)
            await _serverManager.SyncAfterConfigChange();
    }

        private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, _serverManager);
        window.Owner = this;
        if (window.ShowDialog() == true)
            await _serverManager.SyncAfterConfigChange();
    }

    private void SearchGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            vm.SelectedSearchItems = SearchGrid.SelectedItems.Cast<SearchResultVm>();
    }

    private void DownloadsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            vm.SelectedDownloadItems = DownloadsGrid.SelectedItems.Cast<DownloadItemVm>();
    }

    private void WishlistGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.SelectedWishlistItems = WishlistGrid.SelectedItems.Cast<WishlistItemVm>().ToArray();
    }
    private void NotificationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.SelectedNotificationItems = NotificationsGrid.SelectedItems.Cast<NotificationItemVm>().ToArray();
    }
    private void QueuedRacesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_spreadVm != null) _spreadVm.SelectedQueuedRaces = QueuedRacesList.SelectedItems.Cast<QueuedRaceVm>().ToArray();
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
