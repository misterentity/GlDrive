using System.Windows;
using GlDrive.Config;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    public DashboardWindow(ServerManager serverManager, AppConfig config)
    {
        InitializeComponent();
        DataContext = new DashboardViewModel(serverManager, config);
    }
}
