using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GlDrive.UI;

public partial class McpSettingsPanel : UserControl
{
    private CancellationTokenSource? _lifetime;
    public McpSettingsPanel() => InitializeComponent();

    private async void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        if (DataContext is McpSettingsViewModel vm) await vm.CheckAsync(_lifetime.Token);
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is McpSettingsViewModel vm) await vm.ApplyAsync(_lifetime?.Token ?? default);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is McpSettingsViewModel vm) await vm.CheckAsync(_lifetime?.Token ?? default);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not McpSettingsViewModel vm) return;
        try
        {
            Clipboard.SetDataObject(vm.ClientSnippet, true);
            vm.Feedback = vm.SelectedClient == "Claude Desktop" ? "JSON copied." : "Command copied. Run it in PowerShell.";
        }
        catch (Exception) { vm.Feedback = "Clipboard unavailable. Select the text above and press Ctrl+C."; }
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        var localGuide = Path.Combine(AppContext.BaseDirectory, "mcp", "README.md");
        try
        {
            Process.Start(new ProcessStartInfo(File.Exists(localGuide)
                ? localGuide : "https://github.com/misterentity/GlDrive/blob/master/docs/mcp.md") { UseShellExecute = true });
        }
        catch (Exception)
        {
            if (DataContext is McpSettingsViewModel vm) vm.Feedback = "Open docs/mcp.md in the GlDrive repository for the setup guide.";
        }
    }
}
