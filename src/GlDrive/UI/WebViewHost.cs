using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using PresentationSource = System.Windows.PresentationSource;

namespace GlDrive.UI;

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

    public async Task<bool> InitializeAsync(string url, bool allowCrossOrigin = false)
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

            // WebView2 requires a valid HWND parent. In WPF, controls only get an HWND
            // after being loaded into the visual tree. Wait for the Loaded event if
            // the control isn't loaded yet (e.g. tab content not yet rendered).
            if (!_webView.IsLoaded)
            {
                var tcs = new TaskCompletionSource();
                _webView.Loaded += (_, _) => tcs.TrySetResult();
                // Safety timeout — if Loaded never fires, don't hang forever
                if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) != tcs.Task)
                {
                    Log.Warning("WebView2: Loaded event timed out — control may not be in visual tree");
                    ShowFallback();
                    return false;
                }
            }

            var dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _webView.EnsureCoreWebView2Async(env);
            var settings = _webView.CoreWebView2.Settings;
            settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            settings.AreDevToolsEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;

            // Restrict navigation to the initial origin (unless cross-origin is allowed for login flows)
            if (!allowCrossOrigin)
            {
                var allowedOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
                _webView.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (args.Uri != null && !args.Uri.StartsWith(allowedOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        args.Cancel = true;
                        Log.Warning("WebView2 blocked navigation to {Uri} (allowed: {Origin})", args.Uri, allowedOrigin);
                    }
                };
            }
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                    Log.Warning("WebView2 navigation failed: status={Status}, id={Id}",
                        args.WebErrorStatus, args.NavigationId);
            };
            // Fix DPI double-scaling: WPF applies its own DPI transform, and WebView2 also
            // scales for DPI, resulting in content that appears zoomed in on high-DPI displays.
            // Counteract by setting ZoomFactor to 1/dpiScale.
            try
            {
                var source = PresentationSource.FromVisual(this)
                          ?? PresentationSource.FromVisual(Application.Current.MainWindow!);
                if (source?.CompositionTarget != null)
                {
                    var dpiScale = source.CompositionTarget.TransformToDevice.M11;
                    if (dpiScale > 1.0)
                        _webView.ZoomFactor = 1.0 / dpiScale;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not adjust WebView2 DPI zoom");
            }

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

    private const string InstallScript =
        "irm https://go.microsoft.com/fwlink/p/?LinkId=2124703 -OutFile $env:TEMP\\MicrosoftEdgeWebview2Setup.exe; Start-Process $env:TEMP\\MicrosoftEdgeWebview2Setup.exe -ArgumentList '/install' -Wait; Remove-Item $env:TEMP\\MicrosoftEdgeWebview2Setup.exe";

    private void ShowFallback()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 700
        };

        panel.Children.Add(new TextBlock
        {
            Text = "WebView2 Runtime Required",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This tab requires the Microsoft Edge WebView2 Runtime. It ships with Windows 11 and Edge, but may be missing on some Windows 10 machines.",
            Foreground = Brushes.Gray,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Run this in PowerShell (as Administrator):",
            Foreground = Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var scriptBox = new TextBox
        {
            Text = InstallScript,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60))
        };
        panel.Children.Add(scriptBox);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        var copyBtn = new Button
        {
            Content = "Copy to Clipboard",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(InstallScript);
            copyBtn.Content = "Copied!";
        };
        buttonPanel.Children.Add(copyBtn);

        var runBtn = new Button
        {
            Content = "Run Installer Now",
            Padding = new Thickness(14, 6, 14, 6)
        };
        runBtn.Click += (_, _) =>
        {
            try
            {
                runBtn.Content = "Installing...";
                runBtn.IsEnabled = false;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{InstallScript}\"",
                    Verb = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebView2 install launch failed");
                runBtn.Content = "Run Installer Now";
                runBtn.IsEnabled = true;
            }
        };
        buttonPanel.Children.Add(runBtn);

        panel.Children.Add(buttonPanel);

        panel.Children.Add(new TextBlock
        {
            Text = "Restart GlDrive after installation completes.",
            Foreground = Brushes.Gray,
            FontSize = 12
        });

        Content = panel;
    }
}
