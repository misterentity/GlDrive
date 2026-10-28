# GlDrive

**Mount glftpd FTPS servers as native Windows drive letters.** One tray app covers the whole site-user workflow: browse in Explorer, auto-download from a wishlist, race releases between sites over FXP, chat on FiSH-encrypted IRC, stream media, search PreDB, and script the running app through MCP.

Built on .NET 10, WPF, WinFsp, FluentFTP and GnuTLS. Windows 11, x64. Current version: **3.11.28**.

![GlDrive dashboard in light mode](docs/screenshots/light/dashboard-overview.png)

> **For contributors:** the full architecture reference lives in [docs/](docs/). Start with [docs/project-overview-pdr.md](docs/project-overview-pdr.md) for the design rationale and [docs/system-architecture.md](docs/system-architecture.md) for Mermaid diagrams and protocol walkthroughs.

## Why this exists

glftpd sites have quirks that off-the-shelf FTP clients handle badly. Each of these had to be solved for a reliable Windows 11 client:

- **Behind a BNC**, `PASV` returns backend addresses the client can't route to. `CpsvDataHelper` implements CPSV with reverse-TLS data channels by hand
- **TLS 1.3 session tickets** crash glftpd, so TLS 1.2 + `GnuAdvanced.NoTickets` is forced
- **GnuTLS native teardown** can free a session under an in-flight read. Reads are serialized against disposal (`SerializedGnuTlsStream`), poisoned connections are never returned to a pool, `DisconnectWithQuit = false`
- **Stale BNC sessions** eat slot counts. A throttled `!username` ghost-kill runs automatically when the pool can't log in
- **FXP racing** needs SSCN, SFV-first priority, slot tracking, nuke detection, skiplist cascades and four CPSV/PASV modes. A cbftp-style engine ships all of it
- **IRC announce channels** are FiSH-encrypted behind `SITE INVITE`: Blowfish (ECB + CBC) + DH1080, with an FTP-pool-borrowed `SITE INVITE` before auto-join
- **Native crashes** bypass managed exception handlers. A watchdog subprocess, Windows Restart Manager and `.running`/`.updating` markers recover the app and pull the reason from the Event Log

## Features

### Drive mounting
- **Multi-server**: several glftpd sites mounted at once, each on its own drive letter and WinFsp prefix (`\GlDrive\{serverId}`)
- **Optional drive**: connect a server without a drive letter and still get search, downloads, notifications, IRC and racing
- **Bounded FTPS pool** per server with ghost-kill (`!username`), poisoned-connection tracking, login-cap accounting and auto-reinitialize on exhaustion
- **Authentication hold** shared by all logins for an account: rejected credentials stop automatic login attempts and produce one notice. A changed password, explicit Connect/retry, or one probe after 30 minutes can recover it. Other connection faults receive a short shared backoff.
- **TTL + LRU directory cache** with stale-while-revalidate; listings that race a delete or rename are discarded instead of resurrecting files
- **Whole-file read/write buffering** with auto-spill to `DeleteOnClose` temp files above 50 MB
- **SOCKS5 proxy** per server, credentials in Credential Manager

### Downloads
- **One persistent queue for FTP and torrent downloads** with pause/resume, cancel/retry, reorder, scheduling windows (e.g. 22:00 → 06:00) and progress
- **Multi-stream / multi-network** (plexo-style): a second FTP lane borrows the site's idle race login, and data sockets can bind to several network uplinks at once. Per-network speed limits and daily/weekly/monthly data allowances. The **stream matrix** shows which lane fetched each block
- **Resume** via `.gdpart` staging files validated by size + `MDTM`, so a changed remote file restarts cleanly
- **Auto-retry** with backoff, CRC32 SFV verification, and **auto-extraction** of RAR (old-style `.r00` and modern `.part01.rar`), ZIP and 7z
- **Category download paths**: exact site-section keys win, followed by logical `TV` or `Movies` keys inferred from the release, then `localPath`. Logical roots do not gain a duplicate `TV`/`Movies` folder; exact section mappings retain their existing layout. TV includes episodes, season packs and valid dated video episodes; a year or bare `WEB` alone does not classify music/software as Movies.
- Global and per-server speed limits plus a tray **Slow mode** toggle; the PC is kept awake while transfers run
- Duplicate detection, NFO pre-check, disk-space validation, drag-drop from Notifications or Search

### Notifications & wishlist
- `/recent` polling with excluded-category filters and debounced toast notifications. Exclusions save category LISTs; IRC announces still feed wishlist matching in excluded categories, with shared deduplication across both sources.
- Downloads recover release folders moved after an announce by checking the watch path, section paths and matching category mappings through the existing connection pool. Each item permits at most three relocation lookups, spaced at least five minutes apart; the budget survives restarts.
- **Wishlist** with TVMaze/TMDB/OMDB metadata and quality profiles (Any/SD/720p/1080p/2160p)
- **Auto-download** of matching releases across *all* connected servers via `SceneNameParser` + `WishlistMatcher`
- Wishlist import/export as JSON; completion toasts with optional sound
- New wishlist entries from Upcoming, metadata search, import and MCP use `downloads.qualityDefault` when no quality is specified. Invalid defaults fall back to 1080p; explicit qualities and existing entries are preserved.
- Optional scored wishlist profiles add source/resolution/HDR/edition preferences, size limits, PROPER/REPACK upgrades, pending-copy dedupe and a successful-download cutoff. Enable `downloads.scoredQualityEnabled` and select a wishlist `profileId`; existing resolution-only profiles remain compatible. Superseded local copies are retained. See [quality profile configuration](docs/configuration-guide.md#scored-wishlist-profiles).
- **Typed sitebot observations** from explicitly trusted channel bots capture completion sizes, progress, metadata and nuke hints without FTP traffic. Explicit final winners are recorded per destination only after normal listing confirmation; unknown outcomes remain unknown. MCP `spread.report` provides a daily/section/site scoreboard with evidence coverage.
- **Size-aware race queue** uses announced bytes, existing route speeds and history medians to order eligible work with ageing. Duplicate announces merge source hints instead of discarding them. Both behaviors have settings; the single race slot, login budgets, partial-resume priorities and ownership checks remain in force.

### Spread / FXP (cbftp-style race engine)
- **Four FXP modes** picked automatically by `FxpModeDetector`: PASV-PASV, CPSV-PASV, PASV-CPSV and **Relay** (CPSV-CPSV piped through local memory)
- **Separate per-server FXP pool**, so races can't starve the filesystem/download pool
- **Global per-server transfer gates**: every transfer acquires src + dst `ServerGate` semaphores in sorted-id order (deadlock-free), capping concurrent logins per site
- **BNC auto-tune**: a `530 restricted to N simultaneous logins` shrinks the gate and pool to `N − 1`. Shrink-only by design
- **Persistent race queue**: queued races survive restarts (`race-queue.json`)
- Starting a race reports whether it started, queued (with position), was already active/queued, or is temporarily suppressed (with time remaining), in both the Spread tab and MCP.
- **Bounded partial-race recovery**: payload progress resets the idle timeout; an absolute cap defaults to 90 minutes. Stopped partials can resume up to three times behind fresh races. A ten-hour deadline triggers guarded cleanup of incomplete directories GlDrive created, with cleanup notices and persisted history.
- **Zipscript-aware completion**, alternate-source failover, mid-race source relocation (`/incoming` → `/recent`), and `-MISSING` placeholder / leading-dot metadata filtering
- **Scoring 0–65535**: SFV first, NFO after 15 s, then size + route speed (rolling 10-sample history) + site priority + ownership − failure penalty
- **PRET** support cached per host; `TYPE I` canary, `SSCN ON`, deferred `MKD`, completion sweep
- **Dupe-as-success**: `553` replies matching cbftp's dupe patterns count toward completion
- **Skiplist cascade** (per-site then global, glob or bounded regex, first match wins), section availability pre-filter, and race history (max 500) with the full skiplist trace
- Auto-race on IRC announce or `/recent` detection

### IRC client
- TCP + TLS via `SslStream`, TOFU cert validation shared with FTP, keepalive + PING liveness, BNC-friendly exponential-backoff reconnect
- **FiSH** Blowfish in ECB and CBC modes with peer-mode auto-detect; keys DPAPI-encrypted per server
- **DH1080** key exchange with validated public keys
- **SITE INVITE** integration that falls back to the spread pool when the main pool is busy, then auto-joins with retry on `473`
- **Announce detection**: built-in glftpd pattern + custom regex rules, LRU dedup, auto-race to `SpreadManager`
- **Pattern learning**: `IrcPatternDetector` identifies bots, infers announce regexes and surfaces them as suggestions
- Channel sidebar grouped by server, nick list with mode prefixes, Tab completion, clickable release names, slash commands (`/join /part /msg /me /topic /key /keyx …`)

### Player
- Embedded VLC (LibVLCSharp) with direct play from mounted drives
- **HTTP media server** streams from FTP with Range/seek; **RAR-wrapped video** plays as soon as the first volume is ready
- **Chromecast / DLNA** casting; **TMDB** trending/upcoming browser
- **Torrent search + streaming** (MonoTorrent): parallel indexers, DHT, HTTP streaming with seek
- **Queue download** sends torrent content to the main Downloads queue. Metadata is screened before payload transfer (executables are blocked for review), and torrents keep downloading after the player closes

### Search
- **Cross-server** parallel search, results tagged with the server name and filterable by resolution, source and size
- **Per-server modes**: Auto, SITE SEARCH (server-side), Cached Index (background crawler), Live Crawl
- The crawler indexes release directories without descending into them, so large sites build their index in minutes

### Archive extractor
- Drag-drop RAR/ZIP/7z/TAR/GZ/BZ2/XZ/ISO/CAB with multi-volume RAR detection
- **Watch folders** with debounced `FileSystemWatcher`, auto-extract, optional delete-after
- **Folder cleaner**: scan a media root for leftover archives next to extracted media and reclaim the space

### AI assistance
- **Nightly self-tuning agent**: records per-stream JSONL telemetry under `ai-data/`, then proposes config changes through an LLM (OpenRouter). Every change passes a frozen-path check, a confidence threshold, per-category budgets and a validator that owns the actual write. Dry-run by default, with a full audit trail, undo, frozen fields and a memo to steer future runs
- Unknown race winners are excluded from win rates; digests show global, server and section winner coverage. AI priority and download-only proposals conservatively require at least 50% measured winner coverage for that server, including previews and manual applies. Coverage comes from local telemetry, never the model's claims. Explicit undo and direct Settings/config edits remain available; scheduled role changes still require manual review.
- **AI Setup**: analyze a site's rules and section layout from the server editor

### Automation: MCP + control API
- **MCP server** (`GlDrive.Mcp.exe`, stdio) lets Claude Code, Claude Desktop, Codex and other MCP clients read and control the running app: config and snapshots, servers, FTP files, downloads, search, wishlist, IRC, spread, player, extractor, Plex, the AI agent and diagnostics. Setup: [docs/mcp.md](docs/mcp.md)
- **Loopback control API** on `127.0.0.1:8756`, off by default. Bearer token minted on first enable; every request re-checks the caller is loopback
- Routes: `GET /` index, `/status`, `/sections`, `/races`, `/races/active`, `/races/{id}`, `/history`; `POST /races`, `/races/{id}/stop`; `/automation/tools` and `/automation/call` back the MCP bridge
- Enable under **Settings → MCP**. For remote access, tunnel it (WireGuard / Tailscale / SSH) rather than binding the LAN

### Plex
- Plex sign-in, library sharing and invite management from the dashboard

### UI & system
- **Light, Dark and Cyberpunk** themes, swapped at runtime via `DynamicResource`, plus a **System** option that follows Windows
- Numbered sidebar navigation, an Overview with live throughput, pool usage and process stats, and a `?` keyboard-shortcut overlay
- System tray with a dynamic per-server menu and state-colored glyph
- 5-step first-run wizard; settings hot-reload (new servers mount without restart)
- Settings rebuild FTP resources only for connection, TLS, drive-mount and login-budget changes. Download options, server speed limits, cache options and spread rules apply without reconnecting; notification and search edits restart only their own workers, and IRC changes restart IRC independently. Active races retain their existing destination paths; new races use updated section routes. WinFsp's `fileInfoTimeoutMs` is a volume parameter and takes effect on the next drive mount. Removing spread sections retains the conservative main-pool size until that next connection rebuild.
- **Auto-update** from GitHub Releases: SHA-256 mandatory, Authenticode issuer check when signed, installs automatically once no race is running
- **Watchdog subprocess** + Windows Restart Manager for native crash recovery
- Embedded WebView2 tabs (World Monitor / Discord / Streems) with serialized init and a fallback UI
- Site import from FTPRush (XML/JSON) and FlashFXP (XML/DAT); remote glftpd installer over SSH

See [docs/changelog.md](docs/changelog.md) for recent releases.

## Screenshots

All screenshots use the **Light** theme and synthetic demo data.

### Dashboard
| Notifications | Wishlist |
|---|---|
| ![Notifications](docs/screenshots/light/dashboard-notifications.png) | ![Wishlist](docs/screenshots/light/dashboard-wishlist.png) |

| Downloads | Torrent downloads |
|---|---|
| ![Downloads](docs/screenshots/light/dashboard-downloads.png) | ![Torrent downloads](docs/screenshots/light/dashboard-torrents.png) |

| Search | Player |
|---|---|
| ![Search](docs/screenshots/light/dashboard-search.png) | ![Player](docs/screenshots/light/dashboard-player.png) |

| Spread | Browse (dual-pane FXP) |
|---|---|
| ![Spread](docs/screenshots/light/dashboard-spread.png) | ![Browse](docs/screenshots/light/dashboard-browse.png) |

| Mounts & trusted certificates | IRC |
|---|---|
| ![Mounts](docs/screenshots/light/dashboard-mounts.png) | ![IRC](docs/screenshots/light/dashboard-irc.png) |

### Multi-stream downloads, extractor and AI agent
| Stream matrix | Archive extractor | AI agent |
|---|---|---|
| ![Stream matrix](docs/screenshots/light/stream-matrix.png) | ![Extractor](docs/screenshots/light/extractor-populated.png) | ![AI agent](docs/screenshots/light/agent-brief.png) |

### Settings
| Servers | Performance | Streams & uplinks |
|---|---|---|
| ![Servers](docs/screenshots/light/settings-servers.png) | ![Performance](docs/screenshots/light/settings-performance.png) | ![Streams & uplinks](docs/screenshots/light/settings-streams.png) |

| Downloads | Spread & FXP | AI agent |
|---|---|---|
| ![Downloads](docs/screenshots/light/settings-downloads.png) | ![Spread](docs/screenshots/light/settings-spread.png) | ![AI agent](docs/screenshots/light/settings-agent.png) |

| Appearance & diagnostics | Server editor |
|---|---|
| ![Diagnostics](docs/screenshots/light/settings-diagnostics.png) | ![Server editor](docs/screenshots/light/dialog-server.png) |

### Setup wizard
| Welcome | Connection | TLS |
|---|---|---|
| ![Welcome](docs/screenshots/light/wizard-1-welcome.png) | ![Connection](docs/screenshots/light/wizard-2-connection.png) | ![TLS](docs/screenshots/light/wizard-3-tls.png) |

| Mount | Confirm |
|---|---|
| ![Mount](docs/screenshots/light/wizard-4-mount.png) | ![Confirm](docs/screenshots/light/wizard-5-confirm.png) |

## Installation

### Installer (recommended)

Download `GlDriveSetup-v{version}.exe` from the [Releases](../../releases) page and run it. The installer:

1. Installs **WinFsp** silently if it isn't already present (bundled)
2. Copies GlDrive (and the MCP bridge under `mcp\`) to `Program Files\GlDrive`
3. Creates Start Menu and optional Desktop shortcuts
4. Optionally registers auto-start via `HKCU\...\Run`

No .NET runtime install is needed: the app is self-contained on `net10.0-windows/win-x64`.

**Upgrades preserve data.** The uninstaller deliberately leaves `%AppData%\GlDrive\` alone, so config, trusted certs, credentials, wishlists, FiSH keys and race history survive reinstalls.

### Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), [WinFsp](https://winfsp.dev/), Windows 11 x64.

```bash
git clone https://github.com/misterentity/GlDrive.git
cd GlDrive
dotnet build GlDrive.sln
dotnet run   --project src/GlDrive/GlDrive.csproj
dotnet test  GlDrive.sln
```

| Project | Purpose |
|---|---|
| `src/GlDrive` | WPF tray app (WinFsp filesystem, FTP, spread, IRC, player, UI) |
| `src/GlDrive.Core` | Dependency-light parsers and policies, testable without Windows |
| `src/GlDrive.Mcp` | stdio MCP bridge to the app's loopback control API |
| `src/GlDrive.Tests`, `src/GlDrive.Core.Tests`, `src/GlDrive.Mcp.Tests` | ~1,700 unit, integration and isolated WPF tests |

Unit tests cover the pure logic. Native FTPS/WinFsp checks use [tools/ReleaseCheck](tools/ReleaseCheck/README.md); packaged UI smoke and real-server observation remain separate release gates. See [docs/development-guide.md](docs/development-guide.md) for the fast Core-only loop.

### Regenerating screenshots

The app has a built-in renderer that draws every window with synthetic demo data. It never saves config or reads credentials:

```powershell
GlDrive.exe --screenshots --screenshot-theme=Light --screenshot-output=C:\path\to\empty-folder
```

`--screenshot-theme` accepts `Light`, `Dark` or `Cyberpunk`. The output folder must be new or empty and receives a `manifest.json` with SHA-256 hashes of every image.

### Build the installer yourself

Additional prerequisite: [Inno Setup 6](https://jrsoftware.org/isinfo.php) with `ISCC.exe` discoverable.

```powershell
# Publish + Inno Setup installer + update zip
powershell -File installer/build.ps1

# Full release: build + GitHub release with checksums (requires gh CLI)
powershell -File installer/release.ps1
```

Version is read from `<Version>` in `src/GlDrive/GlDrive.csproj`. Run release builds from a folder outside OneDrive. Full pipeline docs: [docs/deployment-guide.md](docs/deployment-guide.md).

## Quick start

1. **Launch**: the first-run wizard asks for host, port, username, password and drive letter. The password goes to Credential Manager; the config JSON also contains sensitive values such as the local control-API token and configured FiSH channel keys
2. **TLS**: the first certificate is trusted on first use (TOFU); a later change asks for confirmation
3. **Explorer**: your drive letter appears as `glFTPd (G:)`
4. **More servers**: Settings → Servers → Add Server, or Import Sites from FTPRush/FlashFXP. Each server has its own IRC, spread, pool, cache, notification and speed settings
5. **Wishlist**: Dashboard → Wishlist. Search OMDB/TMDB/TVMaze, pick a quality profile, and matches download automatically
6. **Spread**: enable it on two or more sites, set sections, priority and slots, then race from Dashboard → Spread (`Ctrl+R`) or right-click in Notifications/Search/PreDB
7. **IRC**: configure per server; `/keyx <nick>` runs a FiSH key exchange
8. **Theme**: Settings → Diagnostics → Theme (Light, Dark, Cyberpunk or System)
9. **MCP** (optional): Settings → MCP → enable, then copy the client command. See [docs/mcp.md](docs/mcp.md)

## Dashboard tabs

| Group | Tabs |
|---|---|
| Dashboard | Overview · Notifications · Wishlist · Downloads |
| Media | Search · Player · Upcoming · Plex |
| Comms | IRC · PreDB |
| Transfers | Spread · Browse |
| System | Mounts · World Monitor · Discord · Streems · AI Agent |

Notifications and Search are drag sources for Downloads. PreDB searches release announcements and finds them on your servers. Spread shows active and queued races, a site scoreboard, the auto-race activity log and race history with skiplist traces. Browse is dual-pane with multi-select FXP. Mounts manages drives and trusted certificates.

## Keyboard shortcuts

Press `?` anywhere in the dashboard for the shortcut overlay.

| Key | Context | Action |
|---|---|---|
| `?` | Dashboard | Toggle shortcut overlay |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Dashboard | Next / previous tab |
| `Delete` | Downloads | Remove selected (cancels active ones) |
| `R` | Downloads | Retry selected failed downloads |
| `Enter` | Notifications / Search / PreDB | Download selected or run the search |
| `Ctrl+R` | Spread | Start new race |
| `Escape` | Spread | Stop selected race |
| `Tab` | IRC input | Cycle nick completion |
| `Space` | Player | Play / pause |
| `F` / `F11` | Player | Fullscreen |
| `Esc` | Player | Exit fullscreen |
| `← / →` | Player | Seek ±10 s |
| `↑ / ↓` | Player | Volume ±5 |

## Configuration data

Everything per-user lives under `%AppData%\GlDrive\`. Full schema with defaults: [docs/configuration-guide.md](docs/configuration-guide.md).

| Data | Location |
|---|---|
| App config | `appsettings.json` (camelCase JSON; includes the control-API token and configured FiSH channel keys) |
| Downloads (per server) | `downloads-{serverId}.json` |
| Race history / queue | `race-history.json` (max 500), `race-queue.json` |
| Spread route speeds | `spread-speed-history.json` (rolling 10 samples per src→dst pair) |
| Learned section exclusions | `section-blacklist.json` |
| Network data usage | `network-usage.json` (per uplink, per day/week/month) |
| Wishlist | `wishlist.json` (global) |
| Notifications | `notifications.json` (max 1000) |
| Trusted certs | `trusted_certs.json` (TOFU, user-only ACL) |
| FiSH keys (per server) | `fish-keys-{serverId}.json` (DPAPI-encrypted) |
| Extractor settings | `extractor-settings.json` |
| IRC PM history (per server) | `pm-history-{serverId}.json` (DPAPI-encrypted; channel chat stays plaintext in `irc-logs/`) |
| Agent telemetry & audit | `ai-data/` (JSONL streams, config-change audit trail, frozen paths) |
| Update state | `.update-declined`, `.update-deferred` (both time-boxed) |
| Logs | `logs/gldrive-{date}.log` (daily rolling, 10 MB cap) |
| Passwords / API keys | Windows Credential Manager (`GlDrive:{host}:{port}:{username}`) |
| Control-API token | `appsettings.json` (ACL-restricted); never pasted into MCP client config |

## Architecture

```
Program.cs (watchdog + update applier)
  └─ App.xaml.cs
       ├─ SingleInstanceGuard, ConfigManager, SerilogSetup, CertificateManager (TOFU)
       ├─ WizardWindow (first run)
       ├─ ServerManager
       │    ├─ per server: MountService
       │    │    ├─ FtpClientFactory → FtpConnectionPool → FtpOperations → CpsvDataHelper
       │    │    ├─ DirectoryCache → GlDriveFileSystem (WinFsp, prefix \GlDrive\{id})
       │    │    ├─ ConnectionMonitor, NewReleaseMonitor
       │    │    ├─ DownloadManager → SegmentedTransfer (FTP/HTTP lanes, NetworkCatalog, TransferPacer)
       │    │    └─ FtpSearchService, WishlistMatcher
       │    ├─ per server: IrcService (IrcClient + FishCipher + Dh1080 + FishKeyStore
       │    │                           + IrcAnnounceListener + IrcPatternDetector)
       │    ├─ torrent download queue: DownloadManager → TorrentDownloadSource (MonoTorrent)
       │    └─ SpreadManager (per-server FXP pools, ServerGate, RaceQueueStore,
       │                     SpreadJob, FxpTransfer, SpreadScorer, SpeedTracker,
       │                     SkiplistEvaluator, SectionBlacklistStore, RaceHistoryStore)
       ├─ WishlistStore, NotificationStore (global), UpdateChecker
       ├─ AgentRunner (daily) → LogDigester → AgentClient → Validators/* → ChangeApplier
       ├─ ControlApi (opt-in, loopback) → RouteTable → Endpoints/* ← GlDrive.Mcp (stdio)
       ├─ PlexService
       └─ TrayIcon + DashboardWindow + ExtractorWindow + ThemeManager
```

Each `MountService` builds its own composition chain. `SpreadManager` keeps a **separate** FTP pool per server so races can't starve the filesystem/download pool; downloads may borrow an idle race login but return it the moment a race needs it. Mermaid diagrams and the startup/FTP/CPSV/FXP/IRC/update flows live in [docs/system-architecture.md](docs/system-architecture.md).

## CPSV (glftpd behind a BNC)

Off-the-shelf FTP clients can't talk to glftpd behind a BNC because `PASV` returns backend addresses the client can't route to. `Ftp/CpsvDataHelper.cs` implements the workaround by hand:

1. Send `CPSV` on the control channel → backend `(a,b,c,d,p1,p2)` in PASV format
2. Raw TCP connect to that address (10 s timeout, no TLS yet)
3. Send the data command (`LIST -a`, `RETR`, `STOR`) on the **control** channel; expect `150`/`125`, do **not** wait for `226`
4. **TLS `AuthenticateAsServerAsync`** on the raw socket with a cached self-signed RSA-2048 cert. glftpd calls `SSL_connect` on the data side, so the client has to be the TLS **server**
5. Stream the data (LIST → parse Unix `ls -l`; RETR/STOR → binary)
6. Close the data socket, then read `226` on the control channel

Resume for `RETR` sends `REST <offset>` first. Before every FXP transfer, `TYPE I` acts as a canary for BNC response-queue desync and `SSCN ON` encrypts FXP data. Full walkthrough: [docs/system-architecture.md#cpsv-data-connections](docs/system-architecture.md#cpsv-data-connections).

## Security

| Concern | Measure |
|---|---|
| Credentials | Passwords and API credentials use Windows Credential Manager (DPAPI). The control-API token and configured FiSH channel keys remain in the restricted config file; treat config backups as sensitive. Outbound AI payloads redact secrets. |
| Log redaction | FTP `PASS`; IRC redaction keys on *which parameter of a command is a credential*: `PASS`, `OPER`, `JOIN` keys, services logins, `MODE`/`324` key echoes, inbound and outbound |
| Config telemetry | Credential-valued config changes record a SHA-256 digest, never the value |
| FiSH keys | DPAPI-encrypted per server in `fish-keys-{serverId}.json` |
| Cert pinning | TOFU SHA-256 in `trusted_certs.json`, user-only ACL, rotation prompt required |
| FTP command injection | `CpsvDataHelper.SanitizeFtpPath` strips CR/LF/NUL before every wire path |
| GnuTLS native crashes | Reads serialized against session disposal; poisoned connections go to `Discard`, never back to a pool |
| DH1080 | Peer public-key range check prevents trivial shared-secret recovery |
| Torrent content | Metadata screened before payload transfer; executables require explicit review |
| Updates | SHA-256 mandatory against `checksums.sha256`; Authenticode issuer match when signed |
| Crash recovery | Watchdog subprocess + Windows Restart Manager; reason pulled from the Event Log |
| Path traversal | Archive extraction validates entry paths (Zip Slip); updater validates the install path |
| Telemetry | Nothing leaves the machine except calls you configure (metadata APIs, OpenRouter for the agent) and update checks to `api.github.com` |
| Inbound network | **Off by default.** The control API binds `127.0.0.1` only and re-checks the remote address on every request, even with a valid token |
| Control API auth | Bearer token generated on first enable, compared in fixed time; the listener refuses to start without one. The MCP bridge reads it from disk; it is never placed in client config |
| Control API input | Request bodies capped at 64 KB; inputs length-bounded before they reach the race engine or the log |
| Screenshot mode | `--screenshots` runs read-only: config saves are no-ops and credential access is disabled |

## Troubleshooting

| Symptom | Fix |
|---|---|
| **Drive letter doesn't appear** | Check WinFsp is installed (`sc query WinFsp`); reinstall via the bundled MSI if missing |
| **"BNC rate limit detected" toast** | Rapid reconnects triggered a ~2-hour cooldown on the BNC. Wait it out |
| **Connection pool exhausted** | Usually transient; the pool auto-reinitializes and the BNC's login cap auto-shrinks the pool + gate from the first `530`. If it persists, check `!username` ghost-kill isn't blocked |
| **FXP transfers all fail with 530** | Look for `Pool: max size shrunk` in the log. If absent, lower the spread pool size in Settings → Spread; auto-tune is shrink-only, so a restart resets it |
| **Race sits in "Waiting for remaining release files"** | The source is still incomplete or a listing is stale; it rechecks automatically. Check the race's file list in Spread |
| **Update downloads but never applies** | Missing or mismatched `checksums.sha256`. Rebuild via `installer/release.ps1` |
| **Watchdog restart loop** | Delete `%AppData%\GlDrive\.running` and `.updating` manually |
| **WebView2 tabs blank** | Install the WebView2 Evergreen Runtime via the fallback-UI link |
| **IRC can't join an invite-only channel** | Set `inviteNick` in the server's IRC config so `SITE INVITE` runs before the join |
| **MCP client can't connect** | Settings → MCP → Check connection. Keep GlDrive running, and run the client as the same Windows user |

Logs at `%AppData%\GlDrive\logs\gldrive-{date}.log` are authoritative. Raise the log level to `Debug` in Settings → Diagnostics before reproducing.

## Uninstalling

Use **Add or Remove Programs**. The uninstaller intentionally leaves `%AppData%\GlDrive\` in place so a reinstall picks up where you left off. To remove everything: `Remove-Item "$env:APPDATA\GlDrive" -Recurse -Force`, then delete saved credentials in **Control Panel → User Accounts → Credential Manager** (search `GlDrive:`).

## Documentation

- [docs/mcp.md](docs/mcp.md): connect Claude or Codex to read and control the running app through MCP
- [docs/project-overview-pdr.md](docs/project-overview-pdr.md): what the project is, why it exists, success criteria
- [docs/codebase-summary.md](docs/codebase-summary.md): subsystem inventory, folder layout, dependencies
- [docs/system-architecture.md](docs/system-architecture.md): Mermaid diagrams, CPSV, FXP, IRC, update flow
- Subsystems: [filesystem](docs/filesystem.md) · [FTP](docs/ftp.md) · [downloads](docs/downloads.md) · [spread](docs/spread.md) · [IRC](docs/irc.md) · [AI agent](docs/ai-agent.md) · [services](docs/services.md) · [UI](docs/ui.md)
- [docs/configuration-guide.md](docs/configuration-guide.md): `appsettings.json` schema, data files, credential keys
- [docs/development-guide.md](docs/development-guide.md): fast Core checks, full Windows tests, lockfiles
- [docs/deployment-guide.md](docs/deployment-guide.md): build, installer, release, in-app update
- [docs/design-guidelines.md](docs/design-guidelines.md): WPF themes, controls, commands
- [docs/code-standards.md](docs/code-standards.md): conventions in use
- [docs/changelog.md](docs/changelog.md): recent versions

## License

Provided as-is for personal use.

Wanted acquisition is opt-in: `downloads.wantedDecisionWindowEnabled` collects variants for 15 minutes and selects the best eligible candidate with a fresh trusted sitebot completion hint; `downloads.wantedIndexSweepsEnabled` searches existing indexes on wanted-list edits and rebuilds without FTP traffic. B1 scoring is reused when enabled. See the configuration guide; `wishlist.wanted` reports pending windows and cache suggestions. Actual downloads retain completion checks and the existing pools.

Series monitoring is opt-in per TV wishlist item (`monitorMode`, default `Legacy`). `wishlist.series`, `wishlist.season_plan`, and `wishlist.season_pull` expose persisted episode state and cached missing-episode plans. Optional race-completed pulls require an explicitly selected server and show; background pulls yield to racing. Fastest-source selection uses measured local-download SpeedTracker history. See the configuration guide for defaults and native PASV yield limits.

Race controls now expose safe Reset, queued site membership edits, confirmed owned-partial cleanup and history Re-race, alongside working card Stop/selection. Exact own-account nukes from trusted IRC and the existing poll raise one persisted-deduplicated alert (`spread.ownNukeAlertsEnabled`, default true). Known nuked content is protected from new race writes and cleanup.

Site health persists outage retry backoff and account-wide new-login budgets across restarts, with one outage/recovery notification pair. Channel silence uses learned hour-of-week rates and conservative training floors. Mount cards and MCP expose health, retry times and active pool borrow purposes. Passive supervision defaults on; optional TCP probes and channel recovery default off. See the configuration guide for bounds and recovery behavior.
