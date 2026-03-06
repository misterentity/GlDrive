# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

GlDrive — a Windows 11 tray app that mounts glftpd FTPS servers as local drive letters using WinFsp + FluentFTP + GnuTLS. Single project, no tests, .NET 10 WPF targeting win-x64.

## Build & Run

```bash
dotnet build src/GlDrive/GlDrive.csproj
dotnet run --project src/GlDrive/GlDrive.csproj
```

The `.sln` file has no project references — always build via the `.csproj` directly.

### Release

```bash
# Publish + Inno Setup installer + update zip
powershell -File installer/build.ps1
# Full release (build + GitHub release with both assets)
powershell -File installer/release.ps1
```

Version is read from the csproj `<Version>` property. The installer script passes it to ISCC via `/D`.

## Architecture

**Startup flow** (`App.xaml.cs`): SingleInstanceGuard → ConfigManager.Load → SerilogSetup → first-run WizardWindow (if no config) → CertificateManager → ServerManager.MountAll → TrayIcon.

### Multi-Server

`ServerManager` orchestrates multiple servers via `Dictionary<string, MountService>`. Each `MountService` creates its own independent chain: `FtpClientFactory` → `FtpConnectionPool` → `FtpOperations` → `DirectoryCache` → (optionally) `GlDriveFileSystem` → `FileSystemHost`. Servers can connect without a drive letter (search, downloads, notifications still work). WinFsp prefix per server: `\GlDrive\{serverId}`.

### Key Layers

- **Config** — `AppConfig` has `List<ServerConfig> Servers` + global `DownloadConfig` + `LoggingConfig`. `ServerConfig` holds per-server connection, mount, TLS, cache, pool, and notification settings. Passwords in Windows Credential Manager via `CredentialStore`. Config at `%AppData%\GlDrive\appsettings.json` (camelCase JSON). `ConfigManager` auto-migrates old single-server format.
- **Ftp** — `FtpClientFactory` creates FTPS clients with GnuTLS (or `AsyncFtpClientSocks5Proxy` when proxy enabled). `FtpConnectionPool` is a bounded `Channel<AsyncFtpClient>` pool (default 3). `FtpOperations` routes through either standard FluentFTP or `CpsvDataHelper` based on server capability. `StreamingDownloader` handles FTP-to-disk streaming with resume support.
- **Filesystem** — `GlDriveFileSystem : FileSystemBase` is the WinFsp implementation. Whole-file read/write buffering (`ReadBuffer`/`WriteBuffer` on `FileNode`). `DirectoryCache` is a TTL-based `ConcurrentDictionary` with LRU eviction. `NtStatusMapper` translates FTP exceptions to NTSTATUS.
- **Services** — `ServerManager` lifecycle, `MountService` per-server orchestration, `ConnectionMonitor` NOOP keepalive (30s) with exponential backoff reconnect, `NewReleaseMonitor` polls `/recent/` categories, `UpdateChecker` for app updates.
- **Downloads** — `DownloadManager` + `DownloadStore` per server (`downloads-{serverId}.json`). `DownloadHistoryStore` is global. Queue uses `List<DownloadItem>` + `SemaphoreSlim`. Features: resume, speed limiting, auto-retry with exponential backoff, scheduling, SFV verification, category download paths. `WishlistMatcher` auto-downloads from `WishlistStore` (global `wishlist.json`). `FtpSearchService` searches categories in parallel.
- **IRC** — `IrcClient` (TcpClient + SslStream), `IrcService` wraps client + FiSH encryption + DH1080 key exchange + auto-reconnect. `FishCipher` (Blowfish ECB/CBC via BouncyCastle), `FishKeyStore` per server. `SITE INVITE` integration borrows FTP pool connections.
- **UI** — System tray via H.NotifyIcon, `DashboardWindow` (cross-server search/downloads/wishlist/IRC/notifications), `SettingsWindow` (MVVM), `WizardWindow` (5-step, code-behind). `ThemeManager` swaps ResourceDictionaries at runtime — all XAML uses `DynamicResource`. `RelayCommand`/`RelayCommand<T>` in `TrayViewModel.cs`.
- **Tls** — `CertificateManager` implements TOFU with SHA-256 fingerprints in `trusted_certs.json`. Global, keyed by `host:port`.

## CPSV Data Connections

This is the most complex part of the codebase. glftpd behind a BNC requires CPSV instead of PASV for data connections. FluentFTP doesn't support CPSV natively, so `CpsvDataHelper` implements it manually:

1. Sends `CPSV` command, parses the returned backend IP:port
2. Opens raw TCP to the backend data address (different from control host)
3. Sends the data command (LIST/RETR/STOR) on the control channel
4. Negotiates TLS **as server** (`AuthenticateAsServerAsync`) — glftpd does `SSL_connect` on data channels
5. Uses a lazy self-signed X509Certificate2 (RSA 2048) for the data TLS server role

## Critical API Gotchas

- **WinFsp**: namespace is `Fsp` / `Fsp.Interop`. Must alias `using FileInfo = Fsp.Interop.FileInfo;` to avoid conflict with `System.IO.FileInfo`.
- **FluentFTP.GnuTLS**: `GnuAdvanced` enum values go directly in the `AdvancedOptions` list. Use `GnuAdvanced.NoTickets` with `PreferTls12` to work around glftpd's TLS 1.3 session ticket bug.
- **FluentFTP v53**: `OpenRead` resume param is positional (`path, FtpDataType.Binary, restart`), NOT named `restartPosition`.
- **H.NotifyIcon**: tray icon requires `ForceCreate(false)` + `GeneratedIconSource` to appear.
- **BNC rate limiting**: rapid reconnects trigger a ~2 hour cooldown on the BNC side.

## Config Locations

- App config: `%AppData%\GlDrive\appsettings.json`
- Trusted certs: `%AppData%\GlDrive\trusted_certs.json`
- Downloads (per server): `%AppData%\GlDrive\downloads-{serverId}.json`
- Wishlist: `%AppData%\GlDrive\wishlist.json`
- FiSH keys: `%AppData%\GlDrive\fish-keys-{serverId}.json`
- Logs: `%AppData%\GlDrive\logs\gldrive-{date}.log`
- Credentials: Windows Credential Manager, key format `GlDrive:{host}:{port}:{username}`
