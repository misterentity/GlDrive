# GlDrive

A Windows 11 system tray application that mounts glftpd FTPS servers as local drive letters. Browse your FTP sites in Windows Explorer like any other drive. Supports multiple servers, each on its own drive letter.

Built with WinFsp, FluentFTP, and GnuTLS.

## Features

- **Multi-server support** — mount multiple glftpd servers simultaneously, each on its own drive letter (G:, H:, etc.)
- **Optional drive mounting** — servers can connect without a drive letter while retaining full functionality (search, downloads, notifications)
- **SOCKS5 proxy** — connect to FTP servers through a SOCKS5 proxy with optional authentication
- **Native drive letter** — use mounted servers in Explorer, cmd, or any app
- **Cross-server search** — search all connected servers in parallel from the Dashboard
- **Per-server tray menu** — connect/disconnect, open drive, and refresh cache per server from the tray icon
- **CPSV support** — works with glftpd behind a BNC (CPSV data connections with reverse TLS)
- **TOFU certificate pinning** — trust-on-first-use with SHA-256 fingerprint storage
- **Connection pooling** — bounded pool of FTPS connections per server with automatic reconnection
- **Directory caching** — TTL-based cache with LRU eviction for responsive browsing
- **Setup wizard** — first-run wizard walks through server configuration
- **New release notifications** — polls `/recent/` categories with configurable excluded categories, shows Windows toast notifications
- **Wishlist & auto-download** — track TV shows (TVMaze) and movies (OMDB), auto-download matching releases from any server
- **Category download paths** — route downloads from specific categories to custom local folders
- **Rich media dashboard** — posters, ratings, genres, and plot summaries for wishlist items
- **Download manager** — streaming FTP-to-disk downloads with progress, queue management, and auto-organization
- **Dark theme** — black/red/white UI throughout
- **System tray** — lives in the tray with per-server status and controls
- **Auto-connect** — optionally connect each server on Windows startup

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
2. **Add more servers** — open Settings > Servers tab to add, edit, or remove servers. Each gets its own drive letter.
3. **Tray icon** — GlDrive runs in the system tray. Right-click for per-server mount/unmount, open drive, refresh cache, settings, and exit.
4. **Browse** — open Explorer and navigate to any mounted drive letter.
5. **Search** — Dashboard > Search queries all connected servers in parallel and shows results with server labels.

### Configuration

All settings are stored locally on your machine:

| Data | Location |
|------|----------|
| App config | `%AppData%\GlDrive\appsettings.json` |
| Downloads (per server) | `%AppData%\GlDrive\downloads-{serverId}.json` |
| Wishlist | `%AppData%\GlDrive\wishlist.json` |
| Trusted certs | `%AppData%\GlDrive\trusted_certs.json` |
| Logs | `%AppData%\GlDrive\logs\gldrive-{date}.log` |
| Passwords | Windows Credential Manager |

## Architecture

```
App.xaml.cs (startup)
  ├── SingleInstanceGuard
  ├── ConfigManager → AppConfig { Servers[], Downloads, Logging }
  ├── SerilogSetup
  ├── WizardWindow (first-run only)
  ├── CertificateManager (TOFU, shared across servers)
  ├── ServerManager (orchestrates all servers)
  │     └── per server: MountService
  │           ├── FtpClientFactory (FluentFTP + GnuTLS)
  │           ├── FtpConnectionPool (bounded Channel<T>)
  │           ├── FtpOperations → CpsvDataHelper (for BNC)
  │           ├── DirectoryCache (TTL + LRU)
  │           ├── GlDriveFileSystem (WinFsp, unique prefix per server)
  │           ├── ConnectionMonitor (NOOP keepalive)
  │           ├── NewReleaseMonitor (polls /recent/)
  │           ├── FtpSearchService (parallel category search)
  │           ├── DownloadManager + DownloadStore (per-server)
  │           └── WishlistMatcher (global wishlist, per-server matching)
  ├── WishlistStore (global)
  ├── DashboardWindow (cross-server search / downloads / wishlist)
  └── TrayIcon (H.NotifyIcon, dynamic per-server menu)
```

### Mounting

When a server is connected, GlDrive establishes a pool of FTPS connections (default 3). If drive mounting is enabled, it registers a WinFsp virtual filesystem on the chosen drive letter with a unique prefix (`\GlDrive\{serverId}`) so multiple servers can coexist. If drive mounting is disabled, the server still connects with full functionality — search, downloads, notifications, and wishlist all work without a drive letter.

The `ServerManager` holds a dictionary of `MountService` instances. On startup, it calls `MountAll()` which iterates all enabled servers with auto-connect and connects them in sequence. Each `MountService` creates its own `FtpClientFactory` → `FtpConnectionPool` → `FtpOperations` → `DirectoryCache` → (optionally) `GlDriveFileSystem` → `FileSystemHost` chain. A `ConnectionMonitor` sends NOOP keepalives every 30 seconds and reconnects with exponential backoff if the connection drops.

Stale mounts from a previous crash are cleaned up automatically using a cascade of `launchctl`, `net use /delete`, and `mountvol /P` before re-mounting.

### SOCKS5 proxy

Each server can optionally route all FTP traffic through a SOCKS5 proxy. When enabled, the `FtpClientFactory` creates an `AsyncFtpClientSocks5Proxy` (from FluentFTP) instead of a direct `AsyncFtpClient`. Proxy credentials are stored separately in Windows Credential Manager with a `GlDrive:proxy:` prefix. All features (CPSV, TLS, search, downloads) work transparently through the proxy.

### Searching

Search operates at two levels of parallelism:

1. **Cross-server** — the Dashboard dispatches search queries to all connected servers simultaneously using `Task.WhenAll`. Each server's search runs independently with its own FTP connection pool.

2. **Within a server** — `FtpSearchService` first lists the category directories under the watch path (e.g. `/recent/`), then searches all categories in parallel using `Task.WhenAll`. The pool's bounded size (default 3 connections) naturally throttles concurrency so the server isn't overwhelmed.

Search bypasses the `DirectoryCache` entirely — it borrows connections directly from the `FtpConnectionPool` and issues fresh `LIST` commands to get up-to-date results. Results are tagged with `ServerId` and `ServerName` so the Dashboard can display which server each result came from and route downloads to the correct server.

### Watching & notifications

Each server runs a `NewReleaseMonitor` that polls the configured watch path (default `/recent/`) on a configurable interval (default 60s). Categories listed in the per-server "Excluded Categories" setting (e.g. `xxx-paysite, 0day`) are skipped entirely. On the first poll it seeds a snapshot of all category/release pairs. On subsequent polls it diffs against the snapshot and fires `NewReleaseDetected` events for any new entries.

The `TrayViewModel` batches release notifications with a 3-second debounce timer — if multiple releases appear in quick succession, they're combined into a single "X new releases" toast notification instead of spamming one per release.

### Wishlist & auto-download

The wishlist is a global store (`wishlist.json`) shared across all servers. You add movies (searched via OMDB) or TV shows (searched via TVMaze) with a quality profile (Any/SD/720p/1080p/2160p).

Each server runs its own `WishlistMatcher` that listens to that server's `NewReleaseDetected` events. When a new release appears, the matcher:

1. Parses the release name using `SceneNameParser` (extracts title, year, season/episode, quality, group)
2. Compares against all non-paused wishlist items
3. On match, builds a local path (`Movies/Title (Year)/release` or `TV/Show/Season XX/release`)
4. Enqueues a `DownloadItem` tagged with the server's ID and name
5. Records the grab so the same release isn't downloaded twice

### Downloads

Each server has its own `DownloadManager` with a `DownloadStore` persisted to `downloads-{serverId}.json`. The manager runs a processing loop that reads from an unbounded channel, bounded by a concurrency semaphore (default 1 concurrent download).

For each download, the `StreamingDownloader` borrows a connection from the pool and streams data directly to disk in configurable buffer sizes (default 256KB). For directory releases, it downloads each file sequentially, reporting aggregate progress. Progress events are forwarded to the Dashboard UI which shows a real-time progress bar with speed.

Downloads that were in-progress when the app closed are automatically reset to queued on next launch.

#### Category download paths

In Settings > Downloads, you can map specific categories to custom local folders. For example, map `x265` to `D:\Movies\x265` and `tv-hd` to `T:\TV`. When a download is initiated (manually or via wishlist), GlDrive checks the category against the mapping table and uses the custom path if one is set. Unmatched categories fall back to the default download folder.

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
| Passwords | Windows Credential Manager | OS-level DPAPI encryption |
| Proxy passwords | Windows Credential Manager | OS-level DPAPI encryption |
| Server configs (host/port/username) | `%AppData%\GlDrive\appsettings.json` | User-profile ACLs |
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
