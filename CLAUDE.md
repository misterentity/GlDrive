# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

GlDrive — a Windows 11 tray app that mounts a glftpd FTPS server as a local drive letter using WinFsp + FluentFTP + GnuTLS. Single project, no tests, .NET 10 WPF targeting win-x64.

## Build

```bash
dotnet build src/GlDrive/GlDrive.csproj
```

The `.sln` file has no project references — always build via the `.csproj` directly.

## Architecture

**Startup flow** (`App.xaml.cs`): SingleInstanceGuard → ConfigManager.Load → SerilogSetup → first-run WizardWindow (if no config) → CertificateManager + MountService → TrayIcon with auto-mount.

**Key layers:**

- **Config** — `AppConfig` POCO loaded from `%AppData%\GlDrive\appsettings.json` (camelCase JSON via System.Text.Json). Passwords stored separately in Windows Credential Manager via `CredentialStore`.
- **Ftp** — `FtpClientFactory` creates FTPS clients with GnuTLS. `FtpConnectionPool` is a bounded `Channel<AsyncFtpClient>` pool (default 3). `FtpOperations` routes all operations through either standard FluentFTP or `CpsvDataHelper` based on server capability.
- **Filesystem** — `GlDriveFileSystem : FileSystemBase` is the WinFsp implementation. Whole-file read/write buffering (`ReadBuffer`/`WriteBuffer` on `FileNode`). `DirectoryCache` is a TTL-based `ConcurrentDictionary` with LRU eviction. `NtStatusMapper` translates FTP exceptions to NTSTATUS.
- **Services** — `MountService` orchestrates the full lifecycle (factory → pool → ops → cache → filesystem → host → mount). `ConnectionMonitor` sends NOOP every 30s with exponential backoff reconnect.
- **UI** — System tray via H.NotifyIcon (`GeneratedIconSource { Text = "G" }`), `SettingsWindow` (5-tab MVVM), `WizardWindow` (5-step, code-behind).
- **Tls** — `CertificateManager` implements TOFU (Trust On First Use) with SHA-256 fingerprints stored in `trusted_certs.json`.

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
- **H.NotifyIcon**: tray icon requires `ForceCreate(false)` + `GeneratedIconSource` to appear.
- **BNC rate limiting**: rapid reconnects trigger a ~2 hour cooldown on the BNC side.

## Config Location

- App config: `%AppData%\GlDrive\appsettings.json`
- Trusted certs: `%AppData%\GlDrive\trusted_certs.json`
- Logs: `%AppData%\GlDrive\logs\gldrive-{date}.log`
- Credentials: Windows Credential Manager, key format `GlDrive:{host}:{port}:{username}`
