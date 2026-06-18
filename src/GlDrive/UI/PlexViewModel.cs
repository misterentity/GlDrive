using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Plex;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Drives the Plex invite-manager dashboard tab. Stateless re: persistence — every
/// list (servers / libraries / shared users) is read live from Plex on demand, matching
/// the chosen "no local member store" scope.
/// </summary>
public sealed class PlexViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PlexService _service;
    private bool _isBusy;
    private string _statusMessage = "";
    private bool _isLoggedIn;
    private string _serverName = "";
    private string _inviteTarget = "";
    private bool _allowDownloads;
    private PlexResource? _selectedServer;
    private PlexSharedUser? _selectedSharedUser;
    private CancellationTokenSource? _loginCts;

    public PlexViewModel(AppConfig config)
    {
        _service = new PlexService(config);
        _isLoggedIn = _service.HasToken;
        _serverName = _service.ServerName;
        _allowDownloads = config.Plex.AllowDownloadsDefault;

        LoginCommand = new RelayCommand(LoginAsync);
        LogoutCommand = new RelayCommand(Logout);
        RefreshServersCommand = new RelayCommand(RefreshServersAsync);
        SelectServerCommand = new RelayCommand<PlexResource>(SelectServerAsync);
        RefreshCommand = new RelayCommand(RefreshAllAsync);
        InviteCommand = new RelayCommand(InviteAsync);
        RevokeCommand = new RelayCommand<PlexSharedUser>(RevokeAsync);
        TestConnectionCommand = new RelayCommand(TestConnectionAsync);

        if (_service.HasToken && _service.HasServer)
            _ = RefreshAllAsync();
        else if (_service.HasToken)
            StatusMessage = "Logged in. Pick your server.";
        else
            StatusMessage = "Not connected. Click \"Login to Plex\".";
    }

    // ---- collections ----
    public ObservableCollection<PlexResource> Servers { get; } = new();
    public ObservableCollection<PlexLibraryVm> Libraries { get; } = new();
    public ObservableCollection<PlexSharedUser> SharedUsers { get; } = new();

    // ---- bound state ----
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
    public bool IsLoggedIn { get => _isLoggedIn; set { _isLoggedIn = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoggedOut)); } }
    public bool IsLoggedOut => !_isLoggedIn;
    public string ServerName { get => _serverName; set { _serverName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasServer)); } }
    public bool HasServer => !string.IsNullOrEmpty(_serverName);
    public string InviteTarget { get => _inviteTarget; set { _inviteTarget = value; OnPropertyChanged(); } }
    public bool AllowDownloads { get => _allowDownloads; set { _allowDownloads = value; OnPropertyChanged(); } }
    public PlexResource? SelectedServer { get => _selectedServer; set { _selectedServer = value; OnPropertyChanged(); } }
    public PlexSharedUser? SelectedSharedUser { get => _selectedSharedUser; set { _selectedSharedUser = value; OnPropertyChanged(); } }

    // ---- commands ----
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RefreshServersCommand { get; }
    public ICommand SelectServerCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand RevokeCommand { get; }
    public ICommand TestConnectionCommand { get; }

    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Opening Plex sign-in in your browser…";
        _loginCts = new CancellationTokenSource();
        try
        {
            var ok = await _service.LoginAsync(OpenBrowser, _loginCts.Token);
            if (ok)
            {
                IsLoggedIn = true;
                StatusMessage = "Logged in. Loading your servers…";
                await RefreshServersInternalAsync();
            }
            else
            {
                StatusMessage = "Login timed out — try again.";
            }
        }
        catch (OperationCanceledException) { StatusMessage = "Login cancelled."; }
        catch (Exception ex) { Fail("Login failed", ex); }
        finally { IsBusy = false; }
    }

    private void Logout()
    {
        _loginCts?.Cancel();
        _service.Logout();
        IsLoggedIn = false;
        ServerName = "";
        OnUi(() => { Servers.Clear(); Libraries.Clear(); SharedUsers.Clear(); });
        StatusMessage = "Logged out.";
    }

    private async Task RefreshServersAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshServersInternalAsync(); }
        catch (Exception ex) { Fail("Couldn't load servers", ex); }
        finally { IsBusy = false; }
    }

    private async Task RefreshServersInternalAsync()
    {
        var servers = await _service.GetServersAsync(CancellationToken.None);
        OnUi(() => { Servers.Clear(); foreach (var s in servers) Servers.Add(s); });
        StatusMessage = servers.Count == 0
            ? "No owned Plex servers found on this account."
            : $"Found {servers.Count} server(s). Select one.";
    }

    private async Task SelectServerAsync(PlexResource? server)
    {
        server ??= SelectedServer;
        if (server == null || IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Connecting to {server.Name}…";
        try
        {
            await _service.SelectServerAsync(server, CancellationToken.None);
            ServerName = server.Name;
            await RefreshAllInternalAsync();
        }
        catch (PlexException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { Fail("Couldn't select server", ex); }
        finally { IsBusy = false; }
    }

    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshAllInternalAsync(); }
        catch (Exception ex) { Fail("Refresh failed", ex); }
        finally { IsBusy = false; }
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
            foreach (var u in users) SharedUsers.Add(u);
        });
        StatusMessage = $"{ServerName}: {libs.Count} libraries, {users.Count} shared user(s).";
    }

    private async Task InviteAsync()
    {
        if (IsBusy) return;
        var target = InviteTarget?.Trim() ?? "";
        if (string.IsNullOrEmpty(target)) { StatusMessage = "Enter a Plex username or email."; return; }
        IsBusy = true;
        StatusMessage = $"Inviting {target}…";
        try
        {
            var titles = Libraries.Where(l => l.IsSelected).Select(l => l.Title).ToList();
            await _service.InviteAsync(target, titles, AllowDownloads, CancellationToken.None);
            StatusMessage = $"Invited {target} to {(titles.Count == 0 ? "all" : titles.Count.ToString())} libraries.";
            InviteTarget = "";
            await RefreshAllInternalAsync();
        }
        catch (PlexException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { Fail("Invite failed", ex); }
        finally { IsBusy = false; }
    }

    private async Task RevokeAsync(PlexSharedUser? user)
    {
        user ??= SelectedSharedUser;
        if (user == null || IsBusy) return;
        var who = !string.IsNullOrEmpty(user.Username) ? user.Username : user.Email;
        var confirm = MessageBox.Show($"Revoke {who}'s access to {ServerName}?",
            "Revoke Plex access", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        IsBusy = true;
        StatusMessage = $"Revoking {who}…";
        try
        {
            await _service.RevokeAsync(user, CancellationToken.None);
            StatusMessage = $"Revoked {who}.";
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
        finally { IsBusy = false; }
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
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
