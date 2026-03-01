using System.Windows;
using GlDrive.Config;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    public DashboardWindow(MountService mountService, AppConfig config)
    {
        InitializeComponent();
        DataContext = new DashboardViewModel(mountService, config);
    }
}
