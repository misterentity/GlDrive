using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
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

    public ObservableCollection<BrowseItemVm> LeftItems { get; } = new();
    public ObservableCollection<BrowseItemVm> RightItems { get; } = new();
    public ObservableCollection<string> ServerList { get; } = new();

    public string LeftServer
    {
        get => _leftServer;
        set { _leftServer = value; OnPropertyChanged(); _ = LoadLeft(); }
    }

    public string LeftPath
    {
        get => _leftPath;
        set { _leftPath = value; OnPropertyChanged(); }
    }

    public string RightServer
    {
        get => _rightServer;
        set { _rightServer = value; OnPropertyChanged(); _ = LoadRight(); }
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

        LeftGoCommand = new RelayCommand(async () => await LoadLeft());
        RightGoCommand = new RelayCommand(async () => await LoadRight());
        FxpRightCommand = new RelayCommand(async () => await FxpToRight());
        FxpLeftCommand = new RelayCommand(async () => await FxpToLeft());
        LeftDoubleClickCommand = new RelayCommand<BrowseItemVm>(async item => await NavigateLeft(item));
        RightDoubleClickCommand = new RelayCommand<BrowseItemVm>(async item => await NavigateRight(item));

        RefreshServerList();
    }

    private void RefreshServerList()
    {
        ServerList.Clear();
        foreach (var server in _serverManager.GetMountedServers())
            ServerList.Add(server.ServerName);

        if (ServerList.Count > 0)
        {
            LeftServer = ServerList[0];
            if (ServerList.Count > 1) RightServer = ServerList[1];
            else RightServer = ServerList[0];
        }
    }

    private MountService? FindServer(string name)
    {
        return _serverManager.GetMountedServers().FirstOrDefault(s => s.ServerName == name);
    }

    private async Task LoadPane(string serverName, string path,
        ObservableCollection<BrowseItemVm> items)
    {
        var server = FindServer(serverName);
        if (server?.Ftp == null) return;

        IsBusy = true;
        try
        {
            var listing = await server.Ftp.ListDirectory(path);

            items.Clear();

            // Add parent entry
            if (path != "/")
            {
                items.Add(new BrowseItemVm
                {
                    Name = "..",
                    IsDirectory = true,
                    FullPath = GetParentPath(path)
                });
            }

            foreach (var item in listing.OrderByDescending(i => i.Type == FtpObjectType.Directory).ThenBy(i => i.Name))
            {
                items.Add(new BrowseItemVm
                {
                    Name = item.Name,
                    Size = item.Size,
                    Modified = item.Modified,
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    FullPath = item.FullName
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Browse failed: {Server} {Path}", serverName, path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLeft()
    {
        if (string.IsNullOrEmpty(LeftServer)) return;
        await LoadPane(LeftServer, LeftPath, LeftItems);
    }

    private async Task LoadRight()
    {
        if (string.IsNullOrEmpty(RightServer)) return;
        await LoadPane(RightServer, RightPath, RightItems);
    }

    private async Task NavigateLeft(BrowseItemVm? item)
    {
        if (item == null || !item.IsDirectory) return;
        LeftPath = item.FullPath;
        await LoadLeft();
    }

    private async Task NavigateRight(BrowseItemVm? item)
    {
        if (item == null || !item.IsDirectory) return;
        RightPath = item.FullPath;
        await LoadRight();
    }

    private async Task FxpToRight()
    {
        var selected = LeftItems.Where(i => i.Name != "..").ToList();
        await DoFxp(LeftServer, RightServer, selected, RightPath);
        await LoadRight();
    }

    private async Task FxpToLeft()
    {
        var selected = RightItems.Where(i => i.Name != "..").ToList();
        await DoFxp(RightServer, LeftServer, selected, LeftPath);
        await LoadLeft();
    }

    private async Task DoFxp(string srcName, string dstName,
        IReadOnlyList<BrowseItemVm> items, string dstPath)
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;

        var srcServer = FindServer(srcName);
        var dstServer = FindServer(dstName);
        if (srcServer == null || dstServer == null) return;

        IsBusy = true;
        try
        {
            foreach (var item in items)
            {
                if (item.IsDirectory) continue; // TODO: recursive FXP
                var destFile = dstPath.TrimEnd('/') + "/" + item.Name;
                await spread.StartFxp(srcServer.ServerId, item.FullPath,
                    dstServer.ServerId, destFile, CancellationToken.None);
            }
        }
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
        GC.SuppressFinalize(this);
    }
}
