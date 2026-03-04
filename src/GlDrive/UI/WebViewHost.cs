using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Hosts a WebView2 control with graceful fallback when the runtime isn't installed.
/// </summary>
public class WebViewHost : ContentControl
{
    private WebView2? _webView;

    public static bool IsRuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> InitializeAsync(string url)
    {
        if (!IsRuntimeAvailable())
        {
            ShowFallback();
            return false;
        }

        try
        {
            _webView = new WebView2();
            Content = _webView;
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            _webView.CoreWebView2.Navigate(url);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebView2 initialization failed");
            _webView = null;
            ShowFallback();
            return false;
        }
    }

    private void ShowFallback()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Microsoft Edge WebView2 Runtime is required for this tab.",
            Foreground = Brushes.Gray,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "It ships with Windows 11 and Microsoft Edge. If you don't have it:",
            Foreground = Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var link = new Button
        {
            Content = "Download WebView2 Runtime",
            Padding = new Thickness(16, 8, 16, 8),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        link.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download",
                    UseShellExecute = true
                });
            }
            catch { }
        };
        panel.Children.Add(link);

        panel.Children.Add(new TextBlock
        {
            Text = "Restart GlDrive after installing.",
            Foreground = Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0)
        });

        Content = panel;
    }
}
