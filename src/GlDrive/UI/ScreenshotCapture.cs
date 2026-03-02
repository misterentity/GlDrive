using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Tls;

namespace GlDrive.UI;

internal static class ScreenshotCapture
{
    private static readonly string OutputDir = Path.Combine(ConfigManager.AppDataPath, "screenshots");

    internal static void CaptureAll(AppConfig config)
    {
        Directory.CreateDirectory(OutputDir);

        var demoConfig = BuildDemoConfig();
        CaptureWizard();
        CaptureDashboard(demoConfig);
        CaptureSettings(demoConfig);
    }

    private static AppConfig BuildDemoConfig()
    {
        var config = new AppConfig();
        config.Servers.Add(new ServerConfig
        {
            Name = "My Server",
            Enabled = true,
            Connection = { Host = "ftp.example.com", Port = 21, Username = "user" },
            Mount = { DriveLetter = "G", VolumeLabel = "glFTPd" }
        });
        config.Logging.Level = "Information";
        return config;
    }

    private static void CaptureWizard()
    {
        var names = new[] { "welcome", "connection", "tls", "mount", "confirm" };
        var wizard = new WizardWindow { DemoMode = true };
        wizard.Show();
        wizard.PreFillDemo();

        for (var i = 0; i < 5; i++)
        {
            wizard.ShowStep(i);
            wizard.UpdateLayout();
            DoEvents();
            RenderToFile(wizard, $"wizard-{i + 1}-{names[i]}.png");
        }

        wizard.Close();
    }

    private static void CaptureDashboard(AppConfig config)
    {
        var certManager = new CertificateManager();
        var serverManager = new ServerManager(config, certManager);
        var dashboard = new DashboardWindow(serverManager, config);
        dashboard.Show();
        DoEvents();

        var tabNames = new[] { "wishlist", "downloads", "search", "upcoming" };
        var tabControl = FindTabControl(dashboard);

        if (tabControl != null)
        {
            for (var i = 0; i < tabNames.Length; i++)
            {
                tabControl.SelectedIndex = i;
                dashboard.UpdateLayout();
                DoEvents();
                RenderToFile(dashboard, $"dashboard-{tabNames[i]}.png");
            }
        }

        dashboard.Close();
        serverManager.Dispose();
    }

    private static void CaptureSettings(AppConfig config)
    {
        var certManager = new CertificateManager();
        var serverManager = new ServerManager(config, certManager);
        var settings = new SettingsWindow(config, serverManager);
        settings.Show();
        DoEvents();

        var tabNames = new[] { "servers", "performance", "downloads", "diagnostics" };
        var tabControl = FindTabControl(settings);

        if (tabControl != null)
        {
            for (var i = 0; i < tabNames.Length; i++)
            {
                tabControl.SelectedIndex = i;
                settings.UpdateLayout();
                DoEvents();
                RenderToFile(settings, $"settings-{tabNames[i]}.png");
            }
        }

        settings.Close();
        serverManager.Dispose();
    }

    private static void RenderToFile(Window window, string filename)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)(window.ActualHeight * dpi.DpiScaleY);

        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var path = Path.Combine(OutputDir, filename);
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }

    private static TabControl? FindTabControl(Window window)
    {
        return FindVisualChild<TabControl>(window);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new System.Windows.Threading.DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }), null);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
