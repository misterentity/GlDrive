# GlDrive

A Windows 11 system tray application for managing glftpd FTPS servers. Mount servers as native drive letters, auto-download from a wishlist, race releases between sites via FXP, chat on FiSH-encrypted IRC, stream media, browse the PreDB, and more — all from a single tray icon.

Built on .NET 10, WPF, WinFsp, FluentFTP, and GnuTLS.

> For developers and contributors: see **[docs/](docs/)** for architecture, configuration, deployment, and code-standards references.

## Features

### Drive mounting
- **Multi-server** — multiple glftpd sites mounted simultaneously, each on its own drive letter (G:, H:, …)
- **Optional mount** — a server can connect without a drive letter and still get search/downloads/notifications/IRC
- **CPSV support** — works with glftpd behind a BNC (reverse-TLS data connections)
- **SOCKS5 proxy** — optional per-server proxy with auth
- **Pooling + caching** — bounded FTPS connection pool with auto-reconnect; TTL + LRU directory cache with stale-while-revalidate

### Downloads
- Streaming FTP-to-disk with resume, real-time progress, speed limiting, and category-aware local paths
- Auto-retry with exponential backoff, scheduling windows, and SFV verification (CRC32)
- Auto-extraction for RAR (old and modern `.part01` naming) with low-priority threads so the UI stays responsive
- Duplicate detection, NFO pre-check, disk space validation, drag-and-drop from other tabs

### Notifications & wishlist
- Polls `/recent` categories with exclusions, toast notifications with debounce batching
- Wishlist with TVMaze + TMDB + OMDB metadata; quality profiles (Any/SD/720p/1080p/2160p)
- Auto-download matching releases from any connected server
- Wishlist import/export, rich posters and plot summaries

### IRC client
- TCP + TLS with TOFU certificate validation, keepalive, PING liveness, auto-reconnect with backoff
- **FiSH** Blowfish encryption in ECB and CBC modes, with DH1080 key exchange
- **SITE INVITE** integration — borrows an FTP pool connection to run `SITE INVITE`, waits for the invite, then auto-joins
- Channel sidebar, nick list with mode prefixes (`@/+/%`), slash commands (`/join /part /msg /me /topic /key /keyx …`), Tab nick-completion, clickable release names

### Spread / FXP (cbftp-style race engine)
- Four FXP modes: **PASV-PASV**, **CPSV-PASV**, **PASV-CPSV**, and **Relay** (CPSV-CPSV through local memory)
- Per-server dedicated FXP connection pool (separate from filesystem/downloads)
- Chain mode: one route per release at a time
- 0-65535 scoring (SFV first, NFO after 15 s, file size, route speed, site priority, ownership)
- Skiplist cascading rules, nuke detection, 3× completion sweep, race history persistence
- Auto-race on IRC announce or `/recent` detection

### Archive extractor
- Drag-and-drop RAR/ZIP/7z/TAR/GZ/BZ2/XZ/ISO/CAB, multi-volume RAR (old and modern naming), watch folders, folder cleaner

### Player
- Embedded VLC, direct play from mounted drives, HTTP streaming-from-FTP (with Range/seek support), on-the-fly RAR extraction for RAR-wrapped video, Chromecast/DLNA casting, TMDB browser, torrent search + streaming (MonoTorrent), resume-tracking

### Search
- Cross-server parallel search dispatched to all connected servers, tagged results with server names
- Per-server mode selector: Auto, SITE SEARCH (server-side), Cached Index, Live Crawl

### UI & system
- Dark/Light themes (runtime-swappable), system-theme detection
- System tray with per-server status and dynamic menu
- 5-step first-run wizard
- Hot-reload on settings save — new servers mount, IRC starts, tray refreshes without restart
- Auto-update from GitHub Releases with SHA-256 verification and Authenticode check
- Embedded web views: World Monitor, Discord, Streems
- Site import from FTPRush (XML/JSON) and FlashFXP (XML/DAT), with passwords, skiplists, TLS, and proxy settings

### Security
- TOFU certificate pinning (SHA-256, `trusted_certs.json`)
- Passwords and API keys in Windows Credential Manager; FiSH keys DPAPI-encrypted on disk
- FTP path sanitization (CRLF/NUL stripping) to prevent command injection
- Crash recovery via watchdog subprocess + Windows Restart Manager; native-crash reason logged from the Windows Application Event Log

See [docs/changelog.md](docs/changelog.md) for recent releases.

## Screenshots

### Dashboard
| Wishlist | Downloads |
|---|---|
| ![Wishlist](docs/screenshots/dashboard-wishlist.png) | ![Downloads](docs/screenshots/dashboard-downloads.png) |

| Search | Upcoming |
|---|---|
| ![Search](docs/screenshots/dashboard-search.png) | ![Upcoming](docs/screenshots/dashboard-upcoming.png) |

### Settings
| Servers | Performance |
|---|---|
| ![Servers](docs/screenshots/settings-servers.png) | ![Performance](docs/screenshots/settings-performance.png) |

### Setup wizard
| Welcome | Connection | TLS |
|---|---|---|
| ![Welcome](docs/screenshots/wizard-1-welcome.png) | ![Connection](docs/screenshots/wizard-2-connection.png) | ![TLS](docs/screenshots/wizard-3-tls.png) |

## Installation

### Installer (recommended)

Download `GlDriveSetup-v{version}.exe` from the [Releases](../../releases) page and run it. The installer:

1. Installs WinFsp (the virtual filesystem driver) if not already present
2. Copies GlDrive to Program Files
3. Creates Start Menu and optional Desktop shortcuts
4. Optionally registers auto-start with Windows

No .NET runtime install is needed — the app is self-contained.

Your config, credentials, and history survive an uninstall/reinstall: the uninstaller deliberately **does not** touch `%AppData%\GlDrive\`.

### Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), [WinFsp](https://winfsp.dev/) pre-installed, Windows 11 x64.

```bash
git clone https://github.com/misterentity/GlDrive.git
cd GlDrive
dotnet build src/GlDrive/GlDrive.csproj
dotnet run   --project src/GlDrive/GlDrive.csproj
```

### Build the installer yourself

Additional prerequisite: [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# Build installer + update zip
powershell -File installer/build.ps1

# Build + publish GitHub release (requires gh CLI authenticated)
powershell -File installer/release.ps1
```

See [docs/deployment-guide.md](docs/deployment-guide.md) for the full release pipeline.

## Usage

1. **First run** — the setup wizard appears. Enter your glftpd server host, port, username, password, and choose a drive letter
2. **Add more servers** — **Settings → Servers** for per-server connection, IRC, spread, pool, and cache settings. New servers mount live; no restart
3. **Tray icon** — right-click for per-server connect/disconnect, open drive, refresh cache, settings, and exit. Left-click opens the Dashboard
4. **Browse** — any mounted drive shows up in Explorer as a normal drive letter
5. **Search** — Dashboard → Search dispatches a query to every connected server in parallel
6. **Wishlist** — Dashboard → Wishlist. Add a movie (OMDB/TMDB) or TV show (TVMaze/TMDB) with a quality profile; matching releases auto-download
7. **IRC** — configure per-server. Join channels, run `/keyx <nick>` for FiSH key exchange, get auto-detected announce patterns
8. **Spread** — enable spreading on each site, configure sections/priority/slots, then race from the Spread tab, right-click Notifications/Search, or let auto-race handle it

### Keyboard shortcuts

| Key | Context | Action |
|---|---|---|
| `Delete` | Downloads tab | Cancel selected |
| `R` | Downloads tab | Retry failed |
| `Enter` | Notifications / Search / PreDB | Download selected |
| `Ctrl+R` | Spread tab | Start new race |
| `Escape` | Spread tab | Stop selected race |
| `Tab` | IRC input | Cycle nick completion |
| `Space` | Player tab | Play/pause |
| `F` / `F11` | Player tab | Fullscreen |

### Configuration data

Everything per-user lives under `%AppData%\GlDrive\`. See [docs/configuration-guide.md](docs/configuration-guide.md) for the full schema.

| Data | Location |
|---|---|
| App config | `appsettings.json` |
| Downloads (per server) | `downloads-{serverId}.json` |
| Race history | `race-history.json` |
| Wishlist | `wishlist.json` |
| Notifications | `notifications.json` |
| Trusted certs | `trusted_certs.json` |
| FiSH keys (per server) | `fish-keys-{serverId}.json` (DPAPI-encrypted) |
| Logs | `logs/gldrive-{date}.log` |
| Passwords and API keys | Windows Credential Manager |

## Architecture

```
Program.cs (watchdog + update applier)
  └─ App.xaml.cs
       ├─ SingleInstanceGuard
       ├─ ConfigManager → AppConfig { Servers[], Downloads, Logging, Spread }
       ├─ SerilogSetup
       ├─ WizardWindow (first run)
       ├─ CertificateManager (TOFU, shared)
       ├─ ServerManager
       │    ├─ per server: MountService
       │    │    ├─ FtpClientFactory → FtpConnectionPool → FtpOperations → CpsvDataHelper
       │    │    ├─ DirectoryCache → GlDriveFileSystem (WinFsp, unique prefix per server)
       │    │    ├─ ConnectionMonitor, NewReleaseMonitor
       │    │    ├─ DownloadManager + DownloadStore, FtpSearchService
       │    │    └─ WishlistMatcher
       │    ├─ per server: IrcService (IrcClient + FishCipher + Dh1080 + FishKeyStore)
       │    └─ SpreadManager (per-server FXP pools, SpreadJob, FxpTransfer, RaceHistoryStore)
       ├─ WishlistStore, NotificationStore (global)
       ├─ UpdateChecker
       ├─ TrayIcon + DashboardWindow + ExtractorWindow
       └─ ThemeManager (runtime-swappable Dark/Light)
```

Full subsystem diagrams, CPSV protocol walkthrough, and the FXP scoring formula live in [docs/system-architecture.md](docs/system-architecture.md).

## CPSV (glftpd behind a BNC)

Most FTP clients can't talk to glftpd behind a BNC because `PASV` returns backend addresses the client can't reach. GlDrive implements `CpsvDataHelper` by hand:

1. `CPSV` on the control channel → backend `(ip,port)` returned
2. Raw TCP to that address (no TLS yet)
3. Data command (`LIST`/`RETR`/`STOR`) sent on the control channel
4. TLS **as server** on the raw socket (`AuthenticateAsServerAsync` with a cached self-signed RSA-2048 cert) — glftpd calls `SSL_connect` on the data side
5. Stream data; read `226` on control

Resume for `RETR` sends explicit `REST <offset>` before the RETR. See [docs/system-architecture.md#cpsv-data-connections](docs/system-architecture.md#cpsv-data-connections) for the full sequence.

## Security summary

| Concern | Measure |
|---|---|
| Credentials | Windows Credential Manager (DPAPI); never in config files |
| Log redaction | FTP `PASS` and IRC server password both redacted by adapters |
| FiSH keys | Stored DPAPI-encrypted in `fish-keys-{serverId}.json` |
| TOFU pinning | SHA-256 fingerprints in `trusted_certs.json` (user-only ACL), rotation prompt required |
| FTP injection | `CpsvDataHelper.SanitizeFtpPath` strips CR/LF/NUL |
| GnuTLS crashes | `NeutralizeGnuTls` + `DisconnectWithQuit = false` + `StaleDataCheck = false` |
| DH1080 | Public-key range check to prevent trivial shared-secret recovery |
| Updates | SHA-256 verification against `checksums.sha256`, Authenticode check against same issuer when signed |
| Crash recovery | Watchdog subprocess + Windows Restart Manager, crash reason pulled from Event Log |
| Telemetry | None — update checks go directly to the GitHub API |

GlDrive does not run any inbound network services. The TLS-server role during CPSV is outbound-only to the glftpd backend.

## Uninstalling

Use **Add or Remove Programs**. The uninstaller intentionally leaves `%AppData%\GlDrive\` in place so a reinstall picks up where you left off. To fully remove:

```powershell
Remove-Item "$env:APPDATA\GlDrive" -Recurse -Force
```

Then remove saved credentials from **Control Panel → User Accounts → Credential Manager**, searching for entries starting with `GlDrive:`.

## Documentation

- [docs/project-overview-pdr.md](docs/project-overview-pdr.md) — what the project is and why
- [docs/codebase-summary.md](docs/codebase-summary.md) — subsystem inventory and dependencies
- [docs/system-architecture.md](docs/system-architecture.md) — Mermaid diagrams, CPSV, FXP, IRC, update flow
- [docs/configuration-guide.md](docs/configuration-guide.md) — `appsettings.json` schema and all data files
- [docs/deployment-guide.md](docs/deployment-guide.md) — build, installer, release, in-app update
- [docs/design-guidelines.md](docs/design-guidelines.md) — WPF themes, controls, commands
- [docs/code-standards.md](docs/code-standards.md) — conventions the codebase actually uses
- [docs/changelog.md](docs/changelog.md) — recent versions

## License

Provided as-is for personal use.
