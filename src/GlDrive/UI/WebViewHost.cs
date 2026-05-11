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
    private static readonly SemaphoreSlim _initLock = new(1, 1);

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
            var dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", "WebView2");

            _webView = new WebView2();
            _webView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = dataDir
            };

            Log.Information("WebView2 init starting for {Url}", url);

            // Serialize initialization AND visual tree attachment — adding a WebView2
            // to the visual tree triggers auto-init; if multiple instances auto-init
            // concurrently they deadlock on the browser process lock.
            //
            // Content = _webView MUST happen before EnsureCoreWebView2Async: a detached
            // WebView2 has no parent HWND and msedgewebview2.exe can't start without
            // one, so init hangs forever. (v1.62.0 moved attachment after init while
            // adding the timeout below; that re-broke what v1.44.83 fixed and caused
            // every panel to hit the 60s timeout instead of loading.) Attachment also
            // kicks off auto-init using the CreationProperties set above; the
            // EnsureCoreWebView2Async call below just awaits its completion.
            //
            // EnsureCoreWebView2Async doesn't accept a cancellation token, so we race
            // it against a 60s Task.Delay via Task.WhenAny. On timeout we abandon the
            // lock and surface the fallback UI so subsequent tabs aren't blocked.
            await _initLock.WaitAsync();
            try
            {
                Content = _webView;
                var initTask = _webView.EnsureCoreWebView2Async();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                var winner = await Task.WhenAny(initTask, timeoutTask);
                if (winner == timeoutTask)
                {
                    Log.Warning("WebView2 initialization timed out for {Url} after 60s", url);
                    _webView = null;
                    ShowFallback();
                    return false;
                }
                await initTask; // surface any exception
            }
            finally
            {
                _initLock.Release();
            }

            Log.Information("WebView2 initialized for {Url}", url);

            var settings = _webView.CoreWebView2.Settings;
            settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            settings.AreDevToolsEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;

            // Suppress JS alert/confirm/prompt — they create native modals that block the WPF window
            _webView.CoreWebView2.ScriptDialogOpening += (_, args) =>
            {
                Log.Debug("WebView2 suppressed {Kind} dialog", args.Kind);
                args.Accept();
            };

            // Block popup windows
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                Log.Debug("WebView2 blocked new window: {Uri}", args.Uri);
                args.Handled = true;
            };

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
        catch (OperationCanceledException)
        {
            Log.Warning("WebView2 initialization timed out for {Url}", url);
            _webView = null;
            ShowFallback();
            return false;
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

        var titleBlock = new TextBlock
        {
            Text = "WebView2 Runtime Required",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
        panel.Children.Add(titleBlock);

        var descBlock = new TextBlock
        {
            Text = "This tab requires the Microsoft Edge WebView2 Runtime. It ships with Windows 11 and Edge, but may be missing on some Windows 10 machines.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        descBlock.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundDimBrush");
        panel.Children.Add(descBlock);

        var instrBlock = new TextBlock
        {
            Text = "Run this in PowerShell (as Administrator):",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        instrBlock.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundDimBrush");
        panel.Children.Add(instrBlock);

        var scriptBox = new TextBox
        {
            Text = InstallScript,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        scriptBox.SetResourceReference(TextBox.BackgroundProperty, "FallbackCodeBackgroundBrush");
        scriptBox.SetResourceReference(TextBox.ForegroundProperty, "FallbackCodeForegroundBrush");
        scriptBox.SetResourceReference(TextBox.BorderBrushProperty, "BorderBrush");
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

        var restartBlock = new TextBlock
        {
            Text = "Restart GlDrive after installation completes.",
            FontSize = 12
        };
        restartBlock.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundDimBrush");
        panel.Children.Add(restartBlock);

        Content = panel;
    }
}
