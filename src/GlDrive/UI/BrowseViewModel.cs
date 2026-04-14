using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.UI;

public class BrowseViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private string _leftServer = "";
    private string _leftPath = "/";
    private string _rightServer = "";
    private string _rightPath = "/";
    private bool _isBusy;
    private bool _initializing = true;
    private CancellationTokenSource? _leftCts;
    private CancellationTokenSource? _rightCts;
    private CancellationTokenSource? _fxpCts;

    public ObservableCollection<BrowseItemVm> LeftItems { get; } = new();
    public ObservableCollection<BrowseItemVm> RightItems { get; } = new();
    public ObservableCollection<string> ServerList { get; } = new();

    // Selected items for FXP (bound from DataGrid — supports multi-select via code-behind)
    public BrowseItemVm? SelectedLeftItem { get; set; }
    public BrowseItemVm? SelectedRightItem { get; set; }
    public List<BrowseItemVm> SelectedLeftItems { get; set; } = new();
    public List<BrowseItemVm> SelectedRightItems { get; set; } = new();

    public string LeftServer
    {
        get => _leftServer;
        set
        {
            if (_leftServer == value) return;
            _leftServer = value;
            OnPropertyChanged();
            if (!_initializing) _ = LoadLeftAsync();
        }
    }

    public string LeftPath
    {
        get => _leftPath;
        set { _leftPath = value; OnPropertyChanged(); }
    }

    public string RightServer
    {
        get => _rightServer;
        set
        {
            if (_rightServer == value) return;
            _rightServer = value;
            OnPropertyChanged();
            if (!_initializing) _ = LoadRightAsync();
        }
    }

    public string RightPath
    {
        get => _rightPath;
        set { _rightPath = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public ICommand LeftGoCommand { get; }
    public ICommand RightGoCommand { get; }
    public ICommand FxpRightCommand { get; }
    public ICommand FxpLeftCommand { get; }
    public ICommand LeftDoubleClickCommand { get; }
    public ICommand RightDoubleClickCommand { get; }

    public BrowseViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        LeftGoCommand = new RelayCommand(async () => await LoadLeftAsync());
        RightGoCommand = new RelayCommand(async () => await LoadRightAsync());
        FxpRightCommand = new RelayCommand(async () => await FxpToRight());
        FxpLeftCommand = new RelayCommand(async () => await FxpToLeft());
        LeftDoubleClickCommand = new RelayCommand<BrowseItemVm>(async item => await NavigateLeft(item));
        RightDoubleClickCommand = new RelayCommand<BrowseItemVm>(async item => await NavigateRight(item));

        RefreshServerList();

        _initializing = false;

        // Update server list when servers connect/disconnect
        _serverManager.ServerStateChanged += (_, _, _) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshServerList);

        // Fire both initial loads in parallel
        _ = LoadBothAsync();
    }

    private void RefreshServerList()
    {
        var mounted = _serverManager.GetMountedServers().Select(s => s.ServerName).ToList();
        var currentLeft = _leftServer;
        var currentRight = _rightServer;

        // Only update if the list changed
        var existing = ServerList.ToList();
        if (existing.SequenceEqual(mounted)) return;

        _initializing = true;
        ServerList.Clear();
        foreach (var name in mounted)
            ServerList.Add(name);

        // Preserve current selections if still valid
        if (ServerList.Count > 0)
        {
            _leftServer = ServerList.Contains(currentLeft) ? currentLeft : ServerList[0];
            _rightServer = ServerList.Contains(currentRight) ? currentRight :
                (ServerList.Count > 1 ? ServerList[1] : ServerList[0]);
            OnPropertyChanged(nameof(LeftServer));
            OnPropertyChanged(nameof(RightServer));
        }
        _initializing = false;
    }

    private async Task LoadBothAsync()
    {
        await Task.WhenAll(LoadLeftAsync(), LoadRightAsync());
    }

    private MountService? FindServer(string name)
    {
        return _serverManager.GetMountedServers().FirstOrDefault(s => s.ServerName == name);
    }

    private CancellationTokenSource SwapCts(bool isLeft)
    {
        if (isLeft)
        {
            _leftCts?.Cancel();
            _leftCts = new CancellationTokenSource();
            return _leftCts;
        }
        _rightCts?.Cancel();
        _rightCts = new CancellationTokenSource();
        return _rightCts;
    }

    private async Task LoadPaneAsync(string serverName, string path,
        ObservableCollection<BrowseItemVm> items, bool isLeft)
    {
        var newCts = SwapCts(isLeft);
        var ct = newCts.Token;

        var server = FindServer(serverName);
        if (server?.Ftp == null) return;

        IsBusy = true;
        try
        {
            // Do the FTP listing off the UI thread
            var listing = await Task.Run(() => server.Ftp.ListDirectory(path, ct), ct);
            ct.ThrowIfCancellationRequested();

            // Build the full list in memory first, then swap into the collection in one batch
            var newItems = new List<BrowseItemVm>();

            if (path != "/")
            {
                newItems.Add(new BrowseItemVm
                {
                    Name = "..",
                    IsDirectory = true,
                    FullPath = GetParentPath(path)
                });
            }

            foreach (var item in listing
                .OrderByDescending(i => i.Type == FtpObjectType.Directory)
                .ThenBy(i => i.Name))
            {
                newItems.Add(new BrowseItemVm
                {
                    Name = item.Name,
                    Size = item.Size,
                    Modified = item.Modified,
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    FullPath = item.FullName
                });
            }

            ct.ThrowIfCancellationRequested();

            // Single UI update: clear + bulk add
            items.Clear();
            foreach (var vm in newItems)
                items.Add(vm);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Browse failed: {Server} {Path}", serverName, path);
        }
        finally
        {
            // Only clear busy if this is still the latest request
            if ((isLeft ? _leftCts : _rightCts) == newCts)
                IsBusy = false;
        }
    }

    private Task LoadLeftAsync()
    {
        if (string.IsNullOrEmpty(LeftServer)) return Task.CompletedTask;
        return LoadPaneAsync(LeftServer, LeftPath, LeftItems, isLeft: true);
    }

    private Task LoadRightAsync()
    {
        if (string.IsNullOrEmpty(RightServer)) return Task.CompletedTask;
        return LoadPaneAsync(RightServer, RightPath, RightItems, isLeft: false);
    }

    private async Task NavigateLeft(BrowseItemVm? item)
    {
        if (item == null || !item.IsDirectory) return;
        LeftPath = item.FullPath;
        await LoadLeftAsync();
    }

    private async Task NavigateRight(BrowseItemVm? item)
    {
        if (item == null || !item.IsDirectory) return;
        RightPath = item.FullPath;
        await LoadRightAsync();
    }

    private async Task FxpToRight()
    {
        var items = SelectedLeftItems.Where(i => i.Name != "..").ToList();
        if (items.Count == 0 && SelectedLeftItem != null && SelectedLeftItem.Name != "..")
            items.Add(SelectedLeftItem);
        if (items.Count == 0) return;
        await DoFxpMulti(LeftServer, RightServer, items, RightPath);
        await LoadRightAsync();
    }

    private async Task FxpToLeft()
    {
        var items = SelectedRightItems.Where(i => i.Name != "..").ToList();
        if (items.Count == 0 && SelectedRightItem != null && SelectedRightItem.Name != "..")
            items.Add(SelectedRightItem);
        if (items.Count == 0) return;
        await DoFxpMulti(RightServer, LeftServer, items, LeftPath);
        await LoadLeftAsync();
    }

    private async Task DoFxpMulti(string srcName, string dstName,
        List<BrowseItemVm> items, string dstPath)
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;

        var srcServer = FindServer(srcName);
        var dstServer = FindServer(dstName);
        if (srcServer == null || dstServer == null) return;

        _fxpCts?.Cancel();
        _fxpCts = new CancellationTokenSource();
        var ct = _fxpCts.Token;

        IsBusy = true;
        try
        {
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    // Recursive directory FXP
                    var destDir = dstPath.TrimEnd('/') + "/" + item.Name;
                    await Task.Run(() => spread.StartFxpDirectory(
                        srcServer.ServerId, item.FullPath,
                        dstServer.ServerId, destDir, ct));
                }
                else
                {
                    // Single file FXP
                    if (dstServer.Pool != null)
                    {
                        await using var conn = await dstServer.Pool.Borrow(ct);
                        await conn.Client.Execute($"MKD {dstPath}", ct);
                    }

                    var destFile = dstPath.TrimEnd('/') + "/" + item.Name;
                    await Task.Run(() => spread.StartFxp(srcServer.ServerId, item.FullPath,
                        dstServer.ServerId, destFile, ct));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "FXP failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? "/" : trimmed[..idx];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _leftCts?.Cancel();
        _rightCts?.Cancel();
        _fxpCts?.Cancel();
        _leftCts?.Dispose();
        _rightCts?.Dispose();
        _fxpCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
