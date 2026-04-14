# Design Guidelines

UI, theme, and UX conventions for the WPF surface. If you're building a new window, tab, or dialog, start here.

## Windows in the app

| Window | Location | Purpose |
|---|---|---|
| `TrayIconSetup` | `UI/TrayIconSetup.cs` | H.NotifyIcon configuration, right-click menu, left-click → Dashboard |
| `DashboardWindow` | `UI/DashboardWindow.xaml` | Main activity hub: Notifications, Wishlist, Downloads, Search, Upcoming, PreDB, Player, Spread, Browse, World Monitor, Discord, Streems, IRC |
| `SettingsWindow` | `UI/SettingsWindow.xaml` | Modal settings (Servers, Performance, Downloads, Notifications, Spread, Diagnostics) |
| `ServerEditDialog` | `UI/ServerEditDialog.xaml` | Modal per-server config (Basic, Proxy, TLS, Cache, Pool, Notifications, Speed, Search, IRC, Spread) |
| `WizardWindow` | `UI/WizardWindow.xaml.cs` | 5-step first-run wizard (Welcome → Connection → TLS → Mount → Confirm) |
| `ExtractorWindow` | `UI/ExtractorWindow.xaml` | Standalone archive extractor with watch folders and folder cleanup |
| `GlftpdInstallerWindow` | `UI/GlftpdInstallerWindow.xaml` | SSH remote glftpd installer |
| `MetadataSearchDialog` | `UI/MetadataSearchDialog.xaml` | Modal OMDB / TVMaze search for adding wishlist items |
| `CleanupWindow` | `UI/CleanupWindow.xaml` | Orphaned archive cleanup assistant |

The tray icon is the only always-present element. All other windows are launched from it or from the Dashboard.

## Themes

Two themes ship out of the box: `UI/Themes/DarkTheme.xaml` and `UI/Themes/LightTheme.xaml`. `ThemeManager.ApplyTheme("Dark" | "Light" | "System")` swaps the merged dictionary at runtime. `System` reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` and maps to Dark or Light accordingly.

Every brush in both themes is a `DynamicResource`. **This is non-negotiable** — `StaticResource` would resolve at load time and the runtime theme swap wouldn't propagate. When you add a new XAML file, use `DynamicResource`:

```xml
<Border Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource BorderBrush}">
```

### Palette

Both theme files define the same set of brushes so you can switch between them without XAML changes:

| Brush | Dark | Light | Use |
|---|---|---|---|
| `BackgroundBrush` | `#111111` | `#F5F5F5` | Main window background |
| `SurfaceBrush` | `#1C1C1C` | `#FFFFFF` | Cards, panels, dialog surfaces |
| `SurfaceLightBrush` | `#282828` | `#EEEEEE` | Hover, selection, header rows |
| `BorderBrush` | `#333333` | `#CCCCCC` | Dividers, frame edges |
| `ForegroundBrush` | `#E8E8E8` | `#1A1A1A` | Primary text |
| `ForegroundDimBrush` | `#888888` | `#666666` | Secondary text, disabled labels |
| `AccentBrush` | `#E53935` | `#E53935` | Primary action, branding red |
| `AccentHoverBrush` | `#FF5252` | `#FF5252` | Button hover |
| `AccentDimBrush` | `#4E1A1A` | `#FFCDD2` | Selected-row background |
| `AccentSubtleBrush` | `#2A1515` | `#FFEBEE` | Barely-visible accent wash |

The accent color is constant across themes so that branding stays consistent. If you need a third color for status (e.g., warning yellow), add it to **both** theme files — a brush defined in only one is a runtime crash waiting to happen when the user switches themes.

## Controls and styles

`App.xaml` is the global style sheet. It merges the current theme dictionary as its first `MergedDictionary` entry and then defines implicit styles for all the WPF primitives the app uses: `Window`, `Label`, `CheckBox`, `TextBox`, `PasswordBox`, `ComboBox`, `TabControl`, `TabItem`, `ListBox`, `DataGrid`, `DataGridRow`, `DataGridCell`, `ProgressBar`, `Separator`, `ScrollBar`, `ContextMenu`, and two named `Button` styles: `PrimaryButton` and `SecondaryButton`.

**Use the existing styles.** If your new button doesn't feel right with `PrimaryButton`, the answer is usually that you need `SecondaryButton`, not a new style. If you genuinely need a third button shape, add it to `App.xaml` so it's available app-wide.

## Commands (MVVM)

Commands use `RelayCommand` / `RelayCommand<T>` defined in `UI/TrayViewModel.cs`. Example:

```csharp
public ICommand DownloadCommand => new RelayCommand<DownloadItem>(item =>
{
    _downloads.Enqueue(item);
});
```

Parameterless commands use `RelayCommand(Action)` or `RelayCommand(Func<Task>)`. `CanExecute` is wired up via `CommandManager.RequerySuggested` so it refreshes automatically on focus/selection changes.

Do **not**:
- Create a new ICommand implementation
- Call back into a view from a view model (use events, or bind a property the view can observe)
- Hold a `Window` reference in a view model (inject a callback instead, the way `SpreadViewModel.SetOpenSettingsAction` does)

## Tabs and lazy init

`DashboardWindow` has 12+ tabs. Some of them are expensive to initialize — the Player tab spins up LibVLC, the Spread tab starts a 2-second refresh timer, the WebView2 tabs go through serialized initialization. **Do not instantiate tab content in the constructor.** Follow the pattern:

```csharp
private void TabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
{
    if (TabControl.SelectedItem == PlayerTab && !_playerInitialized)
    {
        InitializePlayer();
        _playerInitialized = true;
    }
}
```

The Spread tab goes further and uses `Activate()` / `Deactivate()` to start and stop its 2-second refresh timer when the tab becomes visible / hidden. Match that pattern for anything with a periodic background task.

## WebView2 tabs

`UI/WebViewHost.cs` is a `ContentControl` wrapper around `WebView2` with several guarantees:

- **Serialized initialization.** A static `SemaphoreSlim` ensures `EnsureCoreWebView2Async` is never called concurrently. Nested `WebViewHost` instances queue up. Do not bypass this — WebView2 deadlocks if two instances initialize at the same time
- **User data dir** pinned to `%AppData%\GlDrive\WebView2`
- **DPI counter-scaling.** WPF + WebView2 double-scale on high-DPI displays; `WebViewHost` reads `CompositionTarget.TransformToDevice` and sets `ZoomFactor = 1 / dpiScale`
- **Security posture.** DevTools off, context menus off, status bar off, browser message channels off
- **Fallback UI** when the WebView2 runtime isn't installed: instructional text + "Copy to Clipboard" + "Run Installer"
- **`allowCrossOrigin` parameter.** Default is off. OAuth flows (Discord, Streems) need to pass `true`

Add new embedded-web tabs by instantiating `WebViewHost` and calling `InitializeAsync(url, allowCrossOrigin)`. Don't drop raw `WebView2` controls into XAML.

## Drag and drop

The Dashboard supports drag-and-drop from Notifications and Search grids onto the Downloads grid. The pattern:

- Drag source records the mouse-down point, starts a `DoDragDrop` only when the threshold is exceeded (`SystemParameters.MinimumHorizontalDragDistance`)
- `DashboardDragItem` is the data carried on the clipboard (union of `NotificationItemVm` and `SearchResultVm`)
- The drop target's `PreviewDragOver` sets `DragDropEffects.Copy` if the data format matches
- The drop handler dispatches to the corresponding command (`DownloadNotificationCommand` or `DownloadSearchResultCommand`)

If you're adding a new drag source or target, mirror the existing code in `DashboardWindow.xaml.cs` rather than inventing a new data format.

## Notifications and toasts

The tray balloon notifications flow from several places:

- `TrayViewModel.ShowNotification(title, message)` → invokes `ShowNotificationRequested` → `TrayIconSetup` calls `taskbarIcon.ShowNotification(...)`
- Download completion, IRC connect/disconnect, race complete, update available, BNC rate limit detected, and new release detection all funnel through this same path
- Sound: if `config.Downloads.PlaySoundOnComplete` is true, `SystemSounds.Asterisk.Play()` is invoked from `TrayViewModel` on download completion. No custom sound files

Write-rate-wise, `TrayViewModel` throttles status text updates to 1 Hz so a flood of download progress events doesn't beat up the UI thread.

## Keyboard conventions

- **Dashboard Downloads tab:** `Delete` = cancel, `R` = retry
- **Dashboard Notifications and Search tabs:** `Enter` = download
- **Dashboard IRC tab input:** `Tab` = nick-completion (cycles), `Enter` = send, `Ctrl+Enter` = newline
- **Dashboard Player tab:** `Space` = play/pause, `F`/`F11` = fullscreen, `Esc` = exit fullscreen, arrow keys = seek/volume

These are enforced in `DashboardWindow.xaml.cs` via `PreviewKeyDown` handlers. Add new shortcuts there, not in the view models.

## First-run wizard

`WizardWindow` is built entirely in C# (no XAML) because it runs before theme resources are loaded in certain code paths. Five steps, each with its own panel: Welcome, Connection, TLS, Mount, Confirm. The `DemoMode` flag skips TLS testing so `--screenshots` can render the wizard without a real server.

When extending the wizard, keep the no-XAML constraint in mind — use `StackPanel`s built in code-behind and match the existing brush references (`Application.Current.Resources["SurfaceBrush"]`, etc.).

## Custom icon

The tray icon is rendered on the fly by `CyberpunkIconGenerator.Generate(MountState)`. Cyan when connected, yellow when connecting, orange when reconnecting, red when errored, gray when unmounted. It's a 256×256 PNG drawn with `System.Drawing` — if you change the look, keep it square, keep it alpha-transparent, and keep the color semantics because users read them.

## Copy rules

- **Terse.** The app is dense; don't pad UI labels with explanation
- **Use scene/site terminology.** Users know what "pre", "nuke", "race", "SFV", "FXP", "BNC", "SITE INVITE" mean. Don't soften it
- **Log messages are the verbose surface.** The Settings → Diagnostics log level toggle exists specifically so users can turn the firehose on and off
- **Error toasts are short and actionable.** "BNC rate limit detected on X" is better than "The server at X returned status 421, which may indicate a temporary rate limit. Please wait before reconnecting."

## See also

- [system-architecture.md](system-architecture.md) — where each view model hooks into the runtime
- [code-standards.md](code-standards.md) — MVVM and locking conventions
- [codebase-summary.md](codebase-summary.md) — UI file inventory
