using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Plex;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Drives the Plex dashboard tab. Stateless re: persistence — every list (servers /
/// libraries / shared users) is read live from Plex on demand. Also surfaces the two
/// Plex-backed background features: the library index across every linked server and
/// the Watchlist import.
/// </summary>
public sealed class PlexViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PlexService _service;
    private readonly AppConfig _config;
    private readonly ServerManager? _serverManager;
    private bool _isBusy;
    private string _statusMessage = "";
    private bool _isLoggedIn;
    private string _serverName = "";
    private string _inviteTarget = "";
    private bool _allowDownloads;
    private PlexResource? _selectedServer;
    private PlexSharedUser? _selectedSharedUser;
    private PlexSharedUser? _editingUser;
    private bool _suppressServerSwitch;
    private string _libraryIndexStatus = "";
    private string _watchlistStatus = "";
    private CancellationTokenSource? _loginCts;

    public PlexViewModel(AppConfig config, ServerManager? serverManager = null)
    {
        _config = config;
        _serverManager = serverManager;
        _service = new PlexService(config);
        _isLoggedIn = _service.HasToken;
        _serverName = _service.ServerName;
        _allowDownloads = config.Plex.AllowDownloadsDefault;

        LoginCommand = new RelayCommand(LoginAsync);
        LogoutCommand = new RelayCommand(Logout);
        RefreshCommand = new RelayCommand(RefreshEverythingAsync);
        InviteCommand = new RelayCommand(InviteOrUpdateAsync);
        RevokeCommand = new RelayCommand<PlexSharedUser>(RevokeAsync);
        EditAccessCommand = new RelayCommand<PlexSharedUser>(BeginEdit);
        CancelEditCommand = new RelayCommand(CancelEdit);
        SelectAllLibrariesCommand = new RelayCommand(() => SetAllLibraries(true));
        SelectNoLibrariesCommand = new RelayCommand(() => SetAllLibraries(false));
        TestConnectionCommand = new RelayCommand(TestConnectionAsync);
        RefreshLibraryIndexCommand = new RelayCommand(RefreshLibraryIndexAsync);
        SyncWatchlistCommand = new RelayCommand(SyncWatchlistAsync);

        UpdateLibraryIndexStatus();
        UpdateWatchlistStatus();
        if (_service.HasToken)
        {
            StatusMessage = _service.HasServer ? $"Loading {ServerName}…" : "Logged in. Loading your servers…";
            _ = RefreshEverythingAsync();
        }
        else
            StatusMessage = "Not connected. Click \"Login to Plex\".";
    }

    // ---- collections ----
    public ObservableCollection<PlexResource> Servers { get; } = new();
    public ObservableCollection<PlexLibraryVm> Libraries { get; } = new();
    public ObservableCollection<PlexSharedUser> SharedUsers { get; } = new();

    // ---- bound state ----
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); OnUi(CommandManager.InvalidateRequerySuggested); } }
    public bool IsNotBusy => !_isBusy;
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
    public bool IsLoggedIn { get => _isLoggedIn; set { _isLoggedIn = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoggedOut)); } }
    public bool IsLoggedOut => !_isLoggedIn;
    public string ServerName { get => _serverName; set { _serverName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasServer)); } }
    public bool HasServer => !string.IsNullOrEmpty(_serverName);
    public string InviteTarget { get => _inviteTarget; set { _inviteTarget = value; OnPropertyChanged(); } }
    public bool AllowDownloads { get => _allowDownloads; set { _allowDownloads = value; OnPropertyChanged(); } }
    public PlexSharedUser? SelectedSharedUser { get => _selectedSharedUser; set { _selectedSharedUser = value; OnPropertyChanged(); } }
    public string LibraryIndexStatus { get => _libraryIndexStatus; private set { _libraryIndexStatus = value; OnPropertyChanged(); } }
    public string WatchlistStatus { get => _watchlistStatus; private set { _watchlistStatus = value; OnPropertyChanged(); } }
    public bool CanRefreshLibraryIndex => _serverManager != null;
    public bool CanSyncWatchlist => _serverManager != null && _config.Plex.WatchlistSyncEnabled;

    /// <summary>Choosing a server in the dropdown switches to it — no separate "Use" step.</summary>
    public PlexResource? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value)) return;
            _selectedServer = value;
            OnPropertyChanged();
            if (!_suppressServerSwitch && value != null && value.ClientIdentifier != _config.Plex.ServerMachineId)
                _ = SelectServerAsync(value);
        }
    }

    public bool IsEditing => _editingUser != null;
    public string InviteHeader => _editingUser == null ? "INVITE USER" : $"EDIT ACCESS — {Who(_editingUser).ToUpperInvariant()}";
    public string InviteButtonText => _editingUser == null ? "Send Invite" : "Update Access";

    // ---- commands ----
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand RevokeCommand { get; }
    public ICommand EditAccessCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand SelectAllLibrariesCommand { get; }
    public ICommand SelectNoLibrariesCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand RefreshLibraryIndexCommand { get; }
    public ICommand SyncWatchlistCommand { get; }

    private static string Who(PlexSharedUser user) => !string.IsNullOrEmpty(user.Username) ? user.Username : user.Email;

    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Opening Plex sign-in in your browser…";
        _loginCts = new CancellationTokenSource();
        var ok = false;
        try
        {
            ok = await _service.LoginAsync(OpenBrowser, _loginCts.Token);
            if (ok) IsLoggedIn = true;
            else StatusMessage = "Login timed out — try again.";
        }
        catch (OperationCanceledException) { StatusMessage = "Login cancelled."; }
        catch (Exception ex) { Fail("Login failed", ex); }
        finally { IsBusy = false; }
        if (ok) await RefreshEverythingAsync();
    }

    private void Logout()
    {
        _loginCts?.Cancel();
        _service.Logout();
        IsLoggedIn = false;
        ServerName = "";
        OnUi(() =>
        {
            SetSelectedServerSilently(null);
            Servers.Clear(); Libraries.Clear(); SharedUsers.Clear();
            CancelEdit();
        });
        StatusMessage = "Logged out.";
    }

    /// <summary>Servers, then (when one is chosen) its libraries and shares. Picks the saved
    /// server in the dropdown, or the only one when the account has a single server.</summary>
    private async Task RefreshEverythingAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        PlexResource? autoSelect = null;
        try
        {
            var servers = await _service.GetServersAsync(CancellationToken.None);
            OnUi(() =>
            {
                Servers.Clear();
                foreach (var s in servers) Servers.Add(s);
                var current = servers.FirstOrDefault(s => s.ClientIdentifier == _config.Plex.ServerMachineId);
                SetSelectedServerSilently(current);
            });
            if (servers.Count == 0)
                StatusMessage = "No owned Plex servers found on this account.";
            else if (!_service.HasServer || servers.All(s => s.ClientIdentifier != _config.Plex.ServerMachineId))
            {
                if (servers.Count == 1) autoSelect = servers[0];
                else StatusMessage = $"Found {servers.Count} servers. Choose one from the list.";
            }
            else
                await RefreshAllInternalAsync();
        }
        catch (Exception ex) { Fail("Refresh failed", ex); }
        finally { IsBusy = false; }
        if (autoSelect != null) await SelectServerAsync(autoSelect);
    }

    private async Task SelectServerAsync(PlexResource server)
    {
        if (IsBusy)
        {
            // Snap the dropdown back: the switch did not happen.
            OnUi(() => SetSelectedServerSilently(Servers.FirstOrDefault(s => s.ClientIdentifier == _config.Plex.ServerMachineId)));
            return;
        }
        IsBusy = true;
        StatusMessage = $"Connecting to {server.Name}…";
        try
        {
            await _service.SelectServerAsync(server, CancellationToken.None);
            ServerName = server.Name;
            OnUi(() =>
            {
                SetSelectedServerSilently(server);
                CancelEdit();
                // Library titles belong to the previous server; never carry ticks across.
                Libraries.Clear();
            });
            await RefreshAllInternalAsync();
        }
        catch (Exception ex)
        {
            if (ex is PlexException) StatusMessage = ex.Message;
            else Fail("Couldn't select server", ex);
            OnUi(() => SetSelectedServerSilently(Servers.FirstOrDefault(s => s.ClientIdentifier == _config.Plex.ServerMachineId)));
        }
        finally { IsBusy = false; }
    }

    private void SetSelectedServerSilently(PlexResource? server)
    {
        _suppressServerSwitch = true;
        try { SelectedServer = server; }
        finally { _suppressServerSwitch = false; }
    }

    private async Task RefreshAllInternalAsync()
    {
        var ct = CancellationToken.None;
        var libs = await _service.GetLibrariesAsync(ct);
        var users = await _service.GetSharedUsersAsync(ct);
        OnUi(() =>
        {
            var previouslySelected = Libraries.Where(l => l.IsSelected).Select(l => l.Title).ToHashSet();
            Libraries.Clear();
            foreach (var l in libs)
                Libraries.Add(new PlexLibraryVm(l.Title, l.Type) { IsSelected = previouslySelected.Contains(l.Title) });
            SharedUsers.Clear();
            foreach (var u in users.OrderBy(u => Who(u), StringComparer.OrdinalIgnoreCase)) SharedUsers.Add(u);
            if (_editingUser != null && users.All(u => u.SharedServerId != _editingUser.SharedServerId)) CancelEdit();
        });
        var pending = users.Count(u => !u.Accepted);
        StatusMessage = $"{ServerName}: {libs.Count} libraries, {users.Count} shared user(s)" +
                        (pending > 0 ? $", {pending} pending." : ".");
    }

    private void BeginEdit(PlexSharedUser? user)
    {
        user ??= SelectedSharedUser;
        if (user == null || IsBusy) return;
        _editingUser = user;
        InviteTarget = Who(user);
        AllowDownloads = user.AllowSync;
        var shared = user.Sections.Where(s => s.Shared).Select(s => s.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in Libraries) lib.IsSelected = shared.Contains(lib.Title);
        NotifyEditState();
        StatusMessage = $"Editing {Who(user)}: tick their libraries, then Update Access.";
    }

    private void CancelEdit()
    {
        if (_editingUser == null) return;
        _editingUser = null;
        InviteTarget = "";
        AllowDownloads = _config.Plex.AllowDownloadsDefault;
        foreach (var lib in Libraries) lib.IsSelected = false;
        NotifyEditState();
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(InviteHeader));
        OnPropertyChanged(nameof(InviteButtonText));
    }

    private void SetAllLibraries(bool selected)
    {
        foreach (var lib in Libraries) lib.IsSelected = selected;
    }

    private async Task InviteOrUpdateAsync()
    {
        if (IsBusy) return;
        var editing = _editingUser;
        var target = InviteTarget?.Trim() ?? "";
        if (editing == null && string.IsNullOrEmpty(target)) { StatusMessage = "Enter a Plex username or email."; return; }
        var titles = Libraries.Where(l => l.IsSelected).Select(l => l.Title).ToList();
        if (editing != null)
        {
            // Plex has no in-place edit here: the share is revoked and re-sent, so the user
            // gets a fresh invitation (and is Pending until they accept it again).
            var confirm = MessageBox.Show(
                $"Update {Who(editing)}'s access to {ServerName}?\n\n" +
                $"Libraries: {(titles.Count == 0 ? "all" : string.Join(", ", titles))}\nDownloads/sync: {(AllowDownloads ? "allowed" : "not allowed")}\n\n" +
                "Plex does this by removing the share and inviting them again, so they will need to accept a new invitation.",
                "Update Plex access", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }
        IsBusy = true;
        try
        {
            if (editing != null)
            {
                StatusMessage = $"Updating {Who(editing)}…";
                await _service.UpdateAsync(editing, titles, AllowDownloads, CancellationToken.None);
                StatusMessage = $"Updated {Who(editing)}: {(titles.Count == 0 ? "all" : titles.Count.ToString())} libraries.";
            }
            else
            {
                StatusMessage = $"Inviting {target}…";
                await _service.InviteAsync(target, titles, AllowDownloads, CancellationToken.None);
                StatusMessage = $"Invited {target} to {(titles.Count == 0 ? "all" : titles.Count.ToString())} libraries.";
            }
            OnUi(() => { if (editing != null) CancelEdit(); else InviteTarget = ""; });
            await RefreshAllInternalAsync();
        }
        catch (PlexException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { Fail(editing != null ? "Update failed — check Current Access, the share may already be removed" : "Invite failed", ex); }
        finally { IsBusy = false; }
    }

    private async Task RevokeAsync(PlexSharedUser? user)
    {
        user ??= SelectedSharedUser;
        if (user == null || IsBusy) return;
        var who = Who(user);
        var confirm = MessageBox.Show($"Revoke {who}'s access to {ServerName}?",
            "Revoke Plex access", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        IsBusy = true;
        StatusMessage = $"Revoking {who}…";
        try
        {
            await _service.RevokeAsync(user, CancellationToken.None);
            StatusMessage = $"Revoked {who}.";
            if (_editingUser?.SharedServerId == user.SharedServerId) OnUi(CancelEdit);
            await RefreshAllInternalAsync();
        }
        catch (Exception ex) { Fail("Revoke failed", ex); }
        finally { IsBusy = false; }
    }

    private async Task TestConnectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { var (_, msg) = await _service.TestConnectionAsync(CancellationToken.None); StatusMessage = msg; }
        catch (Exception ex) { Fail("Test failed", ex); }
        finally { IsBusy = false; }
    }

    private async Task RefreshLibraryIndexAsync()
    {
        if (_serverManager == null) return;
        LibraryIndexStatus = "Library index: reading every linked Plex server and your library folders…";
        try { await _serverManager.Collections.RefreshLibraryAsync(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unavailable")) { }
        catch (Exception ex) { Log.Warning(ex, "Plex tab: library index refresh failed"); }
        UpdateLibraryIndexStatus();
        UpdateWatchlistStatus();
    }

    private void UpdateLibraryIndexStatus()
    {
        if (_serverManager == null) { LibraryIndexStatus = ""; return; }
        var index = _serverManager.CurrentLibraryIndex;
        if (index.RefreshedAtUtc == DateTime.MinValue)
        {
            LibraryIndexStatus = "Library index: not read yet. Refresh it to use Plex ownership in collection gaps and release info.";
            return;
        }
        var servers = index.PlexServers.Count == 0 ? "no Plex servers read" : string.Join(", ", index.PlexServers);
        var gaps = index.PlexComplete ? "" : " — some servers unreachable";
        LibraryIndexStatus = $"Library index: {servers}{gaps}; {index.Entries.Count:N0} items; read {index.RefreshedAtUtc.ToLocalTime():g}.";
    }

    private async Task SyncWatchlistAsync()
    {
        if (_serverManager == null) return;
        WatchlistStatus = "Watchlist: syncing…";
        try { await _serverManager.Watchlist.SyncAsync(force: true); }
        catch (Exception ex) { Log.Warning(ex, "Plex tab: watchlist sync failed"); }
        UpdateWatchlistStatus();
    }

    private void UpdateWatchlistStatus()
    {
        OnPropertyChanged(nameof(CanSyncWatchlist));
        if (_serverManager == null) { WatchlistStatus = ""; return; }
        if (!_config.Plex.WatchlistSyncEnabled)
        {
            WatchlistStatus = "Watchlist import: off (turn it on in Settings to add your Plex Watchlist to the wishlist).";
            return;
        }
        var last = _serverManager.Watchlist?.LastResult;
        var lastText = last == null || last.Status == "not-run" ? "not run yet"
            : $"last {last.Status}: {last.Added} added, {last.Existing} already listed, {last.Completed} completed" +
              (last.Skipped > 0 ? $", {last.Skipped} skipped" : "");
        WatchlistStatus = $"Watchlist import: every {_config.Plex.WatchlistSyncMinutes} min — {lastText}.";
    }

    private void Fail(string what, Exception ex)
    {
        Log.Warning(ex, "Plex tab: {What}", what);
        StatusMessage = $"{what}: {ex.Message}";
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Plex: failed to open browser"); }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _service.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Checkbox row in the invite library picker.</summary>
public sealed class PlexLibraryVm : INotifyPropertyChanged
{
    private bool _isSelected;
    public PlexLibraryVm(string title, string type) { Title = title; Type = type; }
    public string Title { get; }
    public string Type { get; }
    public string TypeDisplay => Type switch
    {
        "movie" => "Movies",
        "show" => "TV",
        "artist" => "Music",
        "photo" => "Photos",
        _ => Type
    };
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
