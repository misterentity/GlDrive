using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ServerManager _serverManager;
    private readonly SettingsViewModel _vm;
    private readonly ObservableCollection<ServerListItem> _serverItems = new();
    private readonly ObservableCollection<CategoryPathItem> _categoryPaths = new();

    public SettingsWindow(AppConfig config, ServerManager serverManager)
    {
        InitializeComponent();
        _config = config;
        _serverManager = serverManager;
        _vm = new SettingsViewModel(config);
        DataContext = _vm;

        RefreshServerList();
        ServerGrid.ItemsSource = _serverItems;

        // Load category paths
        foreach (var kvp in config.Downloads.CategoryPaths)
            _categoryPaths.Add(new CategoryPathItem { Category = kvp.Key, Path = kvp.Value });
        CategoryPathGrid.ItemsSource = _categoryPaths;
    }

    private void RefreshServerList()
    {
        _serverItems.Clear();
        foreach (var server in _config.Servers)
        {
            _serverItems.Add(new ServerListItem
            {
                Id = server.Id,
                Enabled = server.Enabled,
                Name = server.Name,
                Host = $"{server.Connection.Host}:{server.Connection.Port}",
                DriveLetter = $"{server.Mount.DriveLetter}:"
            });
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerEditDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config.Servers.Add(dialog.Result);
            RefreshServerList();
        }
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is not ServerListItem selected) return;

        var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == selected.Id);
        if (serverConfig == null) return;

        var dialog = new ServerEditDialog(serverConfig) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshServerList();
        }
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is not ServerListItem selected) return;

        var result = MessageBox.Show(
            $"Remove server \"{selected.Name}\"? This will unmount the drive.",
            "Remove Server", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        // Unmount if mounted
        _serverManager.UnmountServer(selected.Id);

        _config.Servers.RemoveAll(s => s.Id == selected.Id);
        RefreshServerList();
    }

    private void ClearCerts_Click(object sender, RoutedEventArgs e)
    {
        var certMgr = new CertificateManager();
        certMgr.ClearTrustedCertificates();
        _vm.RefreshCertsInfo();
        MessageBox.Show("Trusted certificates cleared.", "GlDrive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseDownloadPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Download Folder",
            InitialDirectory = _vm.DownloadLocalPath
        };

        if (dialog.ShowDialog() == true)
            _vm.DownloadLocalPath = dialog.FolderName;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logPath);
        Process.Start("explorer.exe", logPath);
    }

    private void AddCategoryPath_Click(object sender, RoutedEventArgs e)
    {
        _categoryPaths.Add(new CategoryPathItem { Category = "", Path = "" });
    }

    private void RemoveCategoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryPathGrid.SelectedItem is CategoryPathItem selected)
            _categoryPaths.Remove(selected);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplyTo(_config);

        // Save category paths
        _config.Downloads.CategoryPaths.Clear();
        foreach (var item in _categoryPaths)
        {
            if (!string.IsNullOrWhiteSpace(item.Category) && !string.IsNullOrWhiteSpace(item.Path))
                _config.Downloads.CategoryPaths[item.Category.Trim()] = item.Path.Trim();
        }

        ConfigManager.Save(_config);
        Log.Information("Settings saved");
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public class ServerListItem
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string DriveLetter { get; set; } = "";
}

public class CategoryPathItem
{
    public string Category { get; set; } = "";
    public string Path { get; set; } = "";
}
