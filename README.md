# GlDrive

A Windows 11 system tray application that mounts a glftpd FTPS server as a local drive letter. Browse your FTP site in Windows Explorer like any other drive.

Built with WinFsp, FluentFTP, and GnuTLS.

## Features

- **Native drive letter** — mount your glftpd server as G: (or any letter) and use it in Explorer, cmd, or any app
- **CPSV support** — works with glftpd behind a BNC (CPSV data connections with reverse TLS)
- **TOFU certificate pinning** — trust-on-first-use with SHA-256 fingerprint storage
- **Connection pooling** — bounded pool of FTPS connections with automatic reconnection
- **Directory caching** — TTL-based cache with LRU eviction for responsive browsing
- **Setup wizard** — first-run wizard walks through server configuration
- **New release notifications** — polls `/recent/` categories and shows a Windows toast notification when new releases appear
- **System tray** — lives in the tray with mount/unmount/settings controls
- **Auto-mount** — optionally connect on Windows startup

## Installation

### Option 1: Installer (recommended)

Download `GlDriveSetup.exe` from the [Releases](../../releases) page and run it. The installer:

1. Installs WinFsp (the virtual filesystem driver) if not already present
2. Copies GlDrive to Program Files
3. Creates Start Menu and optional Desktop shortcuts
4. Optionally registers auto-start with Windows

No .NET runtime install needed — the app is fully self-contained.

### Option 2: Build from source

**Prerequisites:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later)
- [WinFsp](https://winfsp.dev/) installed
- Windows 11 x64

```bash
git clone https://github.com/misterentity/GlDrive.git
cd GlDrive
dotnet build src/GlDrive/GlDrive.csproj
```

Run from the build output:
```bash
dotnet run --project src/GlDrive/GlDrive.csproj
```

### Building the installer

**Additional prerequisites:**
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)

```powershell
.\installer\build.ps1
```

This publishes a self-contained release build and compiles the Inno Setup installer to `installer/output/GlDriveSetup.exe`.

## Usage

1. **First run** — the setup wizard appears. Enter your glftpd server address, port, username, and password. Choose a drive letter.
2. **Tray icon** — GlDrive runs in the system tray. Right-click for mount/unmount/settings/exit.
3. **Browse** — open Explorer and navigate to your mounted drive letter.

### Configuration

All settings are stored locally on your machine:

| Data | Location |
|------|----------|
| App config | `%AppData%\GlDrive\appsettings.json` |
| Trusted certs | `%AppData%\GlDrive\trusted_certs.json` |
| Logs | `%AppData%\GlDrive\logs\gldrive-{date}.log` |
| Passwords | Windows Credential Manager |

## Architecture

```
App.xaml.cs (startup)
  ├── SingleInstanceGuard
  ├── ConfigManager → AppConfig (from %AppData%)
  ├── SerilogSetup
  ├── WizardWindow (first-run only)
  ├── CertificateManager (TOFU)
  ├── MountService
  │     ├── FtpClientFactory (FluentFTP + GnuTLS)
  │     ├── FtpConnectionPool (bounded Channel<T>)
  │     ├── FtpOperations → CpsvDataHelper (for BNC)
  │     ├── DirectoryCache (TTL + LRU)
  │     └── GlDriveFileSystem (WinFsp)
  ├── ConnectionMonitor (NOOP keepalive)
  ├── NewReleaseMonitor (polls /recent/)
  └── TrayIcon (H.NotifyIcon)
```

### CPSV data connections

glftpd behind a BNC requires CPSV instead of PASV for data connections. FluentFTP doesn't support this natively, so `CpsvDataHelper` implements it manually:

1. Sends `CPSV`, parses the backend IP:port returned by the BNC
2. Opens a raw TCP connection to the backend data address
3. Sends the data command (LIST/RETR/STOR) on the control channel
4. Negotiates TLS **as server** — glftpd calls `SSL_connect` on data channels, so we must call `AuthenticateAsServerAsync` with an in-memory self-signed certificate

## Security Assessment

### Credential handling

- **Passwords are never stored in files.** They are stored exclusively in [Windows Credential Manager](https://support.microsoft.com/en-us/windows/accessing-credential-manager-1b5c916a-6a16-889f-8581-fc16e8165ac0), a system-level encrypted credential store.
- **Passwords are never logged.** The FTP log adapter explicitly redacts `PASS` commands before they reach Serilog.
- **No credentials exist in source code.** The repository has been scanned — no hardcoded hostnames, IPs, usernames, passwords, API keys, or connection strings are present in any tracked file or git history.
- **Config files contain no secrets.** `appsettings.json` stores connection settings (host, port, username, drive letter) but never passwords. This file lives in `%AppData%`, not in the repository.

### TLS / certificate security

- **FTPS with explicit TLS** — all control and data connections are encrypted.
- **GnuTLS backend** — uses FluentFTP.GnuTLS rather than SChannel for broader cipher suite support.
- **TOFU (Trust On First Use)** — on first connection, the server's certificate SHA-256 fingerprint is stored locally. Subsequent connections reject fingerprint mismatches, protecting against MITM attacks.
- **TLS 1.2 preferred** — TLS 1.3 session tickets are disabled (`GnuAdvanced.NoTickets`) to work around a known glftpd bug.
- **Data channel TLS** — CPSV data connections use a self-signed RSA 2048 certificate generated in-memory at runtime. No private key material is stored on disk.

### Local data storage

| Data | Storage | Protection |
|------|---------|------------|
| Password | Windows Credential Manager | OS-level DPAPI encryption |
| Server host/port/username | `%AppData%\GlDrive\appsettings.json` | User-profile ACLs |
| Certificate fingerprints | `%AppData%\GlDrive\trusted_certs.json` | User-profile ACLs |
| Logs | `%AppData%\GlDrive\logs\` | Passwords redacted, auto-rotated |

### What GlDrive does NOT do

- Does not transmit credentials to any third party
- Does not phone home or collect telemetry
- Does not store passwords in config files, registry, or environment variables
- Does not bypass TLS certificate validation (uses TOFU pinning)
- Does not run any network services (the TLS server role in CPSV is outbound-only to the glftpd backend)

## Uninstalling

Use **Add or Remove Programs** in Windows Settings. The uninstaller removes all application files but preserves your configuration in `%AppData%\GlDrive` so you don't lose settings if you reinstall.

To fully remove all data:
```powershell
Remove-Item "$env:APPDATA\GlDrive" -Recurse -Force
```

Then remove the saved credential from Windows Credential Manager (search for entries starting with `GlDrive:`).

## License

This project is provided as-is for personal use.
