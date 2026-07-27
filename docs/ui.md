---
title: UI — tray, dashboard, settings, theming
domain: ui
status: active
last-reviewed: 2026-07-27
---

# UI — tray, dashboard, settings, theming

> **What's in this doc:** *(planned)* the system-tray host, the cross-server dashboard, settings/wizard/extractor windows, and runtime theming.
>
> **What's NOT:** the subsystems the UI drives — racing (→ [[spread]]), downloads (→ [[downloads]]), IRC (→ [[irc]]), config (→ [[config]]).

## Status: planned

This seam is **mapped but not yet authored**. It owns `src/GlDrive/UI/**` (30 files) — see `_meta/doc-ownership.yml` and [[_backlog#undocumented-behavior]].

Known pieces to cover when authored (verify against code before writing):

- `TrayViewModel` — tray icon (H.NotifyIcon needs `ForceCreate(false)` + `GeneratedIconSource`), `RelayCommand`/`RelayCommand<T>`, and the `UpdateChecker` wiring incl. `CanInstallNow` and the `UpdateInstallStalled` tray notification (→ [[services#auto-update]]).
- `DashboardWindow` — cross-server search/downloads/wishlist/IRC/notifications/spread/browse.
- `SettingsWindow` (MVVM), `WizardWindow` (5-step, code-behind), `ExtractorWindow`, `ServerEditDialog` (incl. the "AI Setup" button → [[spread#ai-assisted-rule-setup]]).
- `ThemeManager` — swaps `ResourceDictionary`s at runtime; all XAML uses `DynamicResource`.
- `WebViewHost` — serialized WebView2 init (`Content = _webView` before `EnsureCoreWebView2Async`).
