# GlDrive

A Windows 11 system tray application that mounts glftpd FTPS servers as local drive letters. Browse your FTP sites in Windows Explorer like any other drive. Supports multiple servers, each on its own drive letter.

Built with WinFsp, FluentFTP, GnuTLS, and .NET 10.

## Features

### Drive Mounting
- **Multi-server support** — mount multiple glftpd servers simultaneously, each on its own drive letter (G:, H:, etc.)
- **Optional drive mounting** — servers can connect without a drive letter while retaining full functionality (search, downloads, notifications, IRC)
- **Native drive letter** — use mounted servers in Explorer, cmd, or any app
- **CPSV support** — works with glftpd behind a BNC (CPSV data connections with reverse TLS)
- **SOCKS5 proxy** — connect to FTP servers through a SOCKS5 proxy with optional authentication
- **Connection pooling** — bounded pool of FTPS connections per server with automatic reconnection
- **Directory caching** — TTL-based cache with LRU eviction for responsive browsing

### Downloads
- **Download manager** — streaming FTP-to-disk downloads with real-time progress, speed display, and queue management
- **Download resume** — interrupted downloads resume from where they left off
- **Auto-retry** — failed downloads retry automatically with exponential backoff (configurable max retries)
- **Speed limiting** — global and per-server bandwidth limits
- **Auto-extraction** — RAR archives are automatically extracted after download with SFV verification, then archive files deleted
- **Low-priority extraction** — extraction runs on dedicated below-normal priority threads so the UI stays responsive
- **Category download paths** — route downloads from specific categories to custom local folders
- **Download scheduling** — restrict downloads to specific hours (e.g., overnight only)
- **Download history** — completed and failed downloads are recorded for review
- **Drag-and-drop** — drag releases from notifications/search onto the download queue

### Notifications & Wishlist
- **New release notifications** — polls categories with configurable exclusions, shows Windows toast notifications with debounce batching
- **Right-click actions** — download, race/spread, search on servers, or copy release name from the notifications context menu
- **Wishlist & auto-download** — track TV shows (TVMaze/TMDB) and movies (OMDB/TMDB), auto-download matching releases from any server with quality profiles (Any/SD/720p/1080p/2160p)
- **Rich media dashboard** — posters, ratings, genres, and plot summaries for wishlist items
- **Wishlist import/export** — share wishlists as JSON files
- **Completion notifications** — toast notifications and optional sound when downloads finish
- **Skip incomplete releases** — optionally skip downloads missing an NFO file

### IRC Client
- **Built-in IRC client** — connect to IRC servers with TLS support directly from the Dashboard
- **FiSH encryption** — Blowfish ECB/CBC message encryption with DH1080 key exchange
- **SITE INVITE integration** — automatically runs SITE INVITE via FTP, waits for invite, then joins channels with retry on invite-only errors
- **Stable connections** — TCP keepalive, periodic PING liveness detection, smart reconnect with exponential backoff that avoids BNC rate limiting
- **Channel management** — join, part, private messages, nick list with mode prefixes (@/+/%), Tab nick-completion
- **Slash commands** — /join /part /msg /me /topic /notice /key /keyx /quit /help and raw IRC passthrough
- **Per-server IRC** — each server has its own IRC connection, channels, and FiSH key store

### Spread / FXP (cbftp-style Race Engine)
- **FXP transfers** — site-to-site file transfers between any two connected servers
- **Four FXP modes** — PASV-PASV, CPSV-PASV, PASV-CPSV, and Relay (CPSV-CPSV pipes through local memory with double-buffered I/O)
- **Race engine** — spread releases across multiple servers with intelligent file selection scoring based on SFV priority, file size, route speed, site priority, and ownership percentage
- **Recursive directory support** — handles releases with subdirectories (Sample/, Subs/, CD1/CD2/) up to 3 levels deep
- **Auto-race** — optionally starts races automatically when new releases are detected via /recent polling
- **Race queue** — configurable max concurrent races with automatic queuing and dequeue
- **Scoring algorithm** — cbftp-style 0-65535 scoring: SFV files prioritized first, then NFO after 15s, weighted by file size, average route speed, site priority, and ownership distribution
- **Per-server config** — sections mapping (e.g. MP3=/site/mp3), priority levels, upload/download slot limits, download-only mode, affiliated groups
- **Skiplist** — cascading allow/deny rules at site and global level with glob/regex patterns, section scope, and file/directory filtering
- **Nuke detection** — configurable markers (`.nuke`, `NUKED-`) abort races immediately
- **Speed tracking** — rolling 10-sample average per site pair for scoring
- **Dedicated connection pool** — separate FXP pool per server prevents spread from starving filesystem/downloads
- **Race history** — completed races persisted to JSON with release, sites, bytes, duration, and result
- **Dual-pane file browser** — Browse tab with left/right server panes, double-click navigation, multi-file selection, and recursive directory FXP
- **Tray notifications** — balloon notification on race completion
- **Keyboard shortcuts** — Ctrl+R to start race, Escape to stop
- **Section auto-detection** — derives section names from existing search paths

### PreDB
- **Live scene database** — real-time feed from predb.net with auto-refresh, showing latest scene releases as they're pre'd
- **Section filtering** — filter by TV, Movies, Music, Games, Apps, Sports, Anime, Books, or XXX
- **Relative timestamps** — "2m ago", "1h 15m ago" instead of absolute dates, updated every refresh
- **Nuke detection** — nuked releases shown with strikethrough and red text, hover for nuke reason
- **Right-click actions** — search release on connected servers, start race/spread, or copy release name
- **Search** — full-text search across the entire predb.net database

### Search
- **Cross-server search** — search all connected servers in parallel from the Dashboard with server-tagged results
- **Per-category parallel search** — searches all categories within each server concurrently, throttled by the connection pool

### Archive Extractor
- **Standalone extractor** — drag-and-drop archive extraction tool supporting RAR, ZIP, 7z, TAR, GZ, BZ2, XZ, ISO, CAB
- **Multi-volume RAR** — automatically handles old-style (.rar/.r00/.r01) and modern (.part01.rar/.part02.rar) multi-volume sets
- **Watch folders** — monitor directories for new archives and auto-extract on arrival with file-readiness detection
- **Folder cleaner** — scan a root directory (e.g. Movies/) for release folders with leftover archives alongside extracted media, review reclaimable space, and clean up in bulk
- **Persistent settings** — output mode, overwrite behavior, watch folders, and delete-after-extract preferences saved between sessions

### UI & System
- **Dark/Light theme** — switchable theme with live preview
- **System tray** — per-server status and controls (connect/disconnect, open drive, refresh cache)
- **Setup wizard** — 5-step first-run wizard walks through server configuration
- **Live settings** — new servers are mounted, IRC started, and tray menu refreshed immediately on save (no restart needed)
- **Auto-connect** — optionally connect each server on Windows startup
- **Auto-update** — checks GitHub releases daily, SHA-256 integrity verification, in-app update with UAC elevation
- **Embedded web views** — World Monitor web client in Dashboard tab
- **Site import** — import servers from FTPRush (XML and JSON), FlashFXP (XML and DAT), including passwords, skiplists, TLS settings, and proxy config

### Security
- **TOFU certificate pinning** — trust-on-first-use with SHA-256 fingerprint storage
- **Encrypted credential storage** — passwords stored in Windows Credential Manager (DPAPI)
- **Encrypted key storage** — FiSH encryption keys encrypted at rest using DPAPI
- **Log redaction** — FTP and IRC passwords are redacted before logging
- **Atomic config writes** — write-to-temp-then-rename prevents config corruption on crash
- **DH1080 key validation** — rejects weak public keys to prevent trivial secret recovery
- **Archive path traversal protection** — validates archive entry paths before extraction

## Screenshots

### Dashboard

| Wishlist | Downloads |
|----------|-----------|
| ![Wishlist](docs/screenshots/dashboard-wishlist.png) | ![Downloads](docs/screenshots/dashboard-downloads.png) |

| Search | Upcoming |
|--------|----------|
| ![Search](docs/screenshots/dashboard-search.png) | ![Upcoming](docs/screenshots/dashboard-upcoming.png) |

### Settings

| Servers | Performance |
|---------|-------------|
| ![Servers](docs/screenshots/settings-servers.png) | ![Performance](docs/screenshots/settings-performance.png) |

| Downloads | Diagnostics |
|-----------|-------------|
| ![Downloads](docs/screenshots/settings-downloads.png) | ![Diagnostics](docs/screenshots/settings-diagnostics.png) |

### Setup Wizard

| Welcome | Connection | TLS Certificate |
|---------|------------|-----------------|
| ![Welcome](docs/screenshots/wizard-1-welcome.png) | ![Connection](docs/screenshots/wizard-2-connection.png) | ![TLS](docs/screenshots/wizard-3-tls.png) |

| Mount Options | Confirm |
|---------------|---------|
| ![Mount](docs/screenshots/wizard-4-mount.png) | ![Confirm](docs/screenshots/wizard-5-confirm.png) |

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
# Build installer + update zip
.\installer\build.ps1

# Build + publish GitHub release
.\installer\release.ps1
```

## Usage

1. **First run** — the setup wizard appears. Enter your glftpd server address, port, username, and password. Choose a drive letter.
2. **Add more servers** — open Settings > Servers tab to add, edit, or remove servers. Each gets its own drive letter and optional IRC connection.
3. **Tray icon** — GlDrive runs in the system tray. Right-click for per-server connect/disconnect, open drive, refresh cache, settings, and exit.
4. **Browse** — open Explorer and navigate to any mounted drive letter.
5. **Search** — Dashboard > Search queries all connected servers in parallel and shows results with server labels.
6. **Downloads** — right-click search results or drag releases to the download queue. Monitor progress in Dashboard > Downloads.
7. **Wishlist** — add movies/TV shows in Dashboard > Wishlist. Matching releases are auto-downloaded from any connected server.
8. **IRC** — configure IRC in server settings. Chat, FiSH-encrypted channels, and DH1080 key exchange from Dashboard > IRC.

### Configuration

All settings are stored locally on your machine:

| Data | Location |
|------|----------|
| App config | `%AppData%\GlDrive\appsettings.json` |
| Downloads (per server) | `%AppData%\GlDrive\downloads-{serverId}.json` |
| Download history | `%AppData%\GlDrive\download-history.json` |
| Race history | `%AppData%\GlDrive\race-history.json` |
| Extractor settings | `%AppData%\GlDrive\extractor-settings.json` |
| Wishlist | `%AppData%\GlDrive\wishlist.json` |
| Trusted certs | `%AppData%\GlDrive\trusted_certs.json` |
| FiSH keys (per server) | `%AppData%\GlDrive\fish-keys-{serverId}.json` (DPAPI encrypted) |
| Logs | `%AppData%\GlDrive\logs\gldrive-{date}.log` |
| Passwords | Windows Credential Manager |

### Keyboard shortcuts

| Key | Context | Action |
|-----|---------|--------|
| Delete | Downloads tab | Cancel selected download |
| R | Downloads tab | Retry failed download |
| Enter | Notifications/Search tab | Download selected release |
| Ctrl+R | Spread tab | Start new race |
| Escape | Spread tab | Stop selected race |
| Tab | IRC input | Cycle nick completion |

## Architecture

```
App.xaml.cs (startup)
  +-- SingleInstanceGuard
  +-- ConfigManager -> AppConfig { Servers[], Downloads, Logging }
  +-- SerilogSetup
  +-- WizardWindow (first-run only)
  +-- CertificateManager (TOFU, shared across servers)
  +-- ServerManager (orchestrates all servers)
  |     +-- per server: MountService
  |     |     +-- FtpClientFactory (FluentFTP + GnuTLS / SOCKS5 proxy)
  |     |     +-- FtpConnectionPool (bounded Channel<T>)
  |     |     +-- FtpOperations -> CpsvDataHelper (for BNC)
  |     |     +-- DirectoryCache (TTL + LRU)
  |     |     +-- GlDriveFileSystem (WinFsp, unique prefix per server)
  |     |     +-- ConnectionMonitor (NOOP keepalive)
  |     |     +-- NewReleaseMonitor (polls /recent/)
  |     |     +-- FtpSearchService (parallel category search)
  |     |     +-- DownloadManager + DownloadStore (per-server queue)
  |     |     +-- WishlistMatcher (global wishlist, per-server matching)
  |     +-- per server: IrcService
  |           +-- IrcClient (TcpClient + SslStream)
  |           +-- FishCipher (Blowfish ECB/CBC via BouncyCastle)
  |           +-- FishKeyStore (DPAPI-encrypted, per-server)
  |           +-- Dh1080 (key exchange)
  |     +-- SpreadManager (FXP race engine)
  |           +-- per server: dedicated FtpConnectionPool (spread pool)
  |           +-- SpreadJob (race orchestrator per active race)
  |           +-- SpeedTracker (rolling avg per site pair)
  |           +-- SkiplistEvaluator (cached regex, cascading rules)
  |           +-- RaceHistoryStore (persisted race log)
  +-- WishlistStore (global)
  +-- DownloadHistoryStore (global)
  +-- UpdateChecker (periodic GitHub release polling, SHA-256 verified)
  +-- PreDbClient (predb.net API, section filtering, nuke detection)
  +-- DashboardWindow (search / downloads / wishlist / IRC / spread / browse / notifications / PreDB)
  +-- ExtractorWindow (standalone archive extraction + folder cleaner)
  +-- TrayIcon (H.NotifyIcon, dynamic per-server menu)
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

### IRC

Each server can have an associated IRC connection configured in the server edit dialog. The IRC client supports TLS, TCP keepalive, and periodic PING liveness detection to catch dead connections fast. Auto-reconnect uses exponential backoff that only resets after 60+ seconds of stable connection, preventing rapid reconnect loops that trigger BNC rate limiting.

SITE INVITE integration borrows an FTP pool connection to run `SITE INVITE {nick}`, waits for the IRC INVITE to arrive, then joins channels. If a channel returns 473 (invite-only), the client retries up to 3 times with increasing delays. JOINs are spaced 500ms apart to avoid flood protection.

FiSH encryption is supported in both ECB and CBC modes. DH1080 key exchange can be initiated from the nick context menu. Keys are stored per-server in DPAPI-encrypted files.

The IRC tab in the Dashboard provides a channel sidebar (with PM and server windows), message area, nick list with mode prefixes, and an input box with Tab nick-completion and slash command support.

### CPSV data connections

glftpd behind a BNC requires CPSV instead of PASV for data connections. FluentFTP doesn't support this natively, so `CpsvDataHelper` implements it manually:

1. Sends `CPSV`, parses the backend IP:port returned by the BNC
2. Opens a raw TCP connection to the backend data address
3. Sends the data command (LIST/RETR/STOR) on the control channel
4. Negotiates TLS **as server** — glftpd calls `SSL_connect` on data channels, so we must call `AuthenticateAsServerAsync` with an in-memory self-signed certificate

### Auto-update

GlDrive checks for new releases on GitHub every 24 hours. When an update is available, a notification is shown. The update downloads the release ZIP, verifies its SHA-256 hash against a `checksums.sha256` asset published with each release, extracts it to a temp directory, and launches the new binary with UAC elevation to overwrite the install directory. The old process exits, old files are renamed to `.old`, new files are copied in, and the updated app is launched.

## Security

### Credential handling

- **Passwords are never stored in files.** They are stored exclusively in [Windows Credential Manager](https://support.microsoft.com/en-us/windows/accessing-credential-manager-1b5c916a-6a16-889f-8581-fc16e8165ac0), a system-level encrypted credential store.
- **Passwords are never logged.** Both the FTP log adapter and IRC client explicitly redact `PASS` commands before they reach Serilog.
- **No credentials exist in source code.** The repository has been scanned — no hardcoded hostnames, IPs, usernames, passwords, API keys, or connection strings are present in any tracked file or git history.
- **Config files contain no secrets.** `appsettings.json` stores connection settings (host, port, username, drive letter) but never passwords. This file lives in `%AppData%`, not in the repository.
- **FiSH keys are encrypted at rest.** Per-server key stores are encrypted using Windows DPAPI (CurrentUser scope). Legacy plaintext stores are automatically migrated on first load.

### TLS / certificate security

- **FTPS with explicit TLS** — all FTP control and data connections are encrypted.
- **IRC TLS** — IRC connections support TLS with the same TOFU certificate validation as FTP.
- **GnuTLS backend** — uses FluentFTP.GnuTLS rather than SChannel for broader cipher suite support.
- **TOFU (Trust On First Use)** — on first connection, the server’s certificate SHA-256 fingerprint is stored locally. Subsequent connections reject fingerprint mismatches, protecting against MITM attacks.
- **TLS 1.2 preferred** — TLS 1.3 session tickets are disabled (`GnuAdvanced.NoTickets`) to work around a known glftpd bug.
- **Data channel TLS** — CPSV data connections use a self-signed RSA 2048 certificate generated in-memory at runtime. No private key material is stored on disk.
- **DH1080 key validation** — public keys are bounds-checked to prevent trivial shared secret recovery attacks.

### Local data storage

| Data | Storage | Protection |
|------|---------|------------|
| Passwords | Windows Credential Manager | OS-level DPAPI encryption |
| Proxy passwords | Windows Credential Manager | OS-level DPAPI encryption |
| IRC passwords | Windows Credential Manager | OS-level DPAPI encryption |
| API keys (OMDB/TMDB) | Windows Credential Manager | OS-level DPAPI encryption |
| FiSH encryption keys | `fish-keys-{serverId}.json` | DPAPI encryption (CurrentUser) |
| Server configs (host/port/username) | `appsettings.json` | User-profile ACLs |
| Certificate fingerprints | `trusted_certs.json` | User-profile ACLs, atomic writes |
| Logs | `logs/gldrive-{date}.log` | Passwords redacted, auto-rotated |

### What GlDrive does NOT do

- Does not transmit credentials to any third party
- Does not phone home or collect telemetry (update checks go directly to the GitHub API)
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
