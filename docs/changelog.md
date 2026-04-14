# Changelog

Generated from `git log --oneline --no-merges` at v1.44.55 (2026-04-13). This file lists notable versions; see `git log` for the full history.

Commit messages in this project do not follow conventional-commit syntax — versions are split below into **Features**, **Fixes**, **Security**, and **Refactors / infrastructure** by reading the message text.

## v1.44 — GnuTLS stabilization, spread engine maturity, security fixes

### Security
- **v1.44.55** — ghost-kill TLS validation, media server FTP path-injection fix
- **v1.44.7** — TOFU cert-change rejection, FTP path sanitization, loopback-only torrent ports
- **v1.36.0** — efficiency pass, path sanitization, security hardening
- **v1.32.0** — 12 findings from a security review

### Features
- **v1.44.52** — spread auto-detects glftpd dated directories for 0DAY/MP3 sections
- **v1.44.49** — watchdog logs the crash reason from Windows Event Log before restart
- **v1.44.40** — completion sweep for races + IRC announce trace logging
- **v1.44.35** — chain mode for spread (one route per release at a time)
- **v1.44.33** — auto-reinitialize dead spread pools before race start
- **v1.44.30** — SSCN encryption for FXP control channel
- **v1.44.23** — watchdog subprocess replaces unreliable `RegisterApplicationRestart`
- **v1.44.20** — race history tab with skiplist evaluation trace popup
- **v1.44.0**  — AI-powered rule setup via OpenRouter API (auto-infer sections + skiplist)

### Fixes (highlights)
- **v1.44.53 / v1.44.46 / v1.44.12 / v1.44.11 / v1.44.5** — GnuTLS native-crash family. `NeutralizeGnuTls` before disposal, try/catch around disposal, fix stale temp-zip lock during update
- **v1.44.51** — watchdog crash: avoid `ConfigManager` in the watchdog path (it depends on System.Text.Json)
- **v1.44.45** — pool exhaustion: reduce main pool to 2 when spread is active
- **v1.44.44** — enforce SFV-first transfer order (block data files until SFV is delivered)
- **v1.44.43** — races never completing: reinitialize pools on completion sweep, count borrow timeouts
- **v1.44.41** — IRC announces not detected: built-in verbose glftpd pattern
- **v1.44.39** — torrent streaming stuck on metadata: bind DHT/listen to 0.0.0.0
- **v1.44.32** — duplicate FXP transfers, TVMaze null episode deserialization
- **v1.44.31** — fail fast when pool fully exhausted instead of hanging 30 s
- **v1.44.28** — Streems tab locking up app: serialize WebView2 initialization
- **v1.44.24** — poison FTP connections after *all* failed FXP transfers, not just unhandled exceptions
- **v1.44.13** — discard poisoned GnuTLS connections instead of returning to pool
- **v1.44.8**  — DH1080 key exchange crash that disconnects from IRC

## v1.43 — IRC FiSH hardening, site rules parser

- **v1.43.8** — PreDB refresh always runs (don't require tab active)
- **v1.43.6** — SITE RULES parser: handle Nuke rules and Disallowed Groups
- **v1.43.4** — fix FiSH PM mode mismatch: DH1080 uses CBC, auto-detect peer mode
- **v1.43.2** — Enable/Disable buttons for servers in Settings
- **v1.43.1** — FXP tries CpsvPasv first, falls back to Relay for BNC-to-direct
- **v1.43.0** — auto-race: pass all servers with known source, auto-discover paths

## v1.42 — Spread engine correctness + parallel startup

- **v1.42.9** — force CpsvPasv mode, discard poisoned connections after failure
- **v1.42.7** — remove duplicate TYPE I causing FXP response desync
- **v1.42.6** — spread scan uses main server pools instead of dead spread pools
- **v1.42.5** — 15 s borrow timeout, detailed scan logging
- **v1.42.1** — parallel server mounting off UI thread (fix startup lag/lockup)
- **v1.42.0** — auto-discover release paths across servers for spread races

## v1.41 — Auto-race activity, fuzzy section matching

- **v1.41.4** — case-insensitive sections, catch crashed jobs, logging
- **v1.41.3** — fuzzy section matching for notifications/PreDB in right-click Race
- **v1.41.2** — multi-select server deletion in Settings with confirmation
- **v1.41.0** — Auto-Race Activity log to Spread tab

## v1.40 — Player RAR + WebView2 stability

- **v1.40.9** — group IRC channels by server in sidebar
- **v1.40.8** — RAR playback: download with progress UI, extract locally, play file
- **v1.40.7** — player RAR streaming for BNC servers uses CPSV for data connections
- **v1.40.5** — Streems/Discord login: allow cross-origin navigation for OAuth
- **v1.40.3** — library playback: FromPath for VLC, RAR extraction, file validation
- **v1.40.1** — VLC init error handling, FTP stream timeouts, better errors
- **v1.40.0** — multi-download status display, search multi-select

## v1.39 — Dashboard UX, threading overhaul, IRC robustness

- **v1.39.9** — Downloads UI overhaul: multi-select, clear buttons, context menu, status colors
- **v1.39.8** — total used/free disk across all servers in status bar
- **v1.39.6** — threading and I/O performance overhaul
- **v1.39.5** — FiSH for channels with bare `[key]` format (no `cbc:/ecb:` prefix)
- **v1.39.4** — decouple IRC from FTP: start IRC even when FTP mount fails
- **v1.39.0** — IRC stability overhaul, PreDB improvements, FTPRush XML import fixes

## v1.38 — Folder cleaner

- **v1.38.0** — Folder Cleaner added to Extractor (scan and delete leftover archives)

## v1.37 — Extractor persistence, TOFU auto-accept, site imports

- **v1.37.5** — archive deletion: filter multi-part volumes, add retry
- **v1.37.4/3/2** — FTPRush XML + JSON import with skiplists, TLS, auto-detect
- **v1.37.1** — overhaul extractor: persist all settings, auto-start watchers, fix delete
- **v1.37.0** — auto-accept TLS certs (true TOFU), per-server "Clear Certificate" button

## v1.36 — Efficiency / security pass, site importers

- **v1.36.9** — fix startup deadlock: cert prompt blocking UI thread via Dispatcher.Invoke
- **v1.36.3** — Import Sites from FTPRush and FlashFXP
- **v1.36.2** — Torrents-CSV search backend
- **v1.36.0** — efficiency overhaul, path sanitization, security hardening

## v1.35 — IRC announce detection, auto-updater fixes

- **v1.35.10** — SITE DISKFREE lockup: 5 s timeout, delay first query
- **v1.35.3** — auto-update hash verification failing on filename mismatch
- **v1.35.2** — detect site rules via SITE RULES for auto-configuring spreader
- **v1.35.1** — auto-detect IRC announce patterns from channel logs
- **v1.35.0** — IRC announce detection for auto-racing

## v1.34 — Spread engine feature expansion

- **v1.34.2** — auto-detect sections from server + default skiplist rules
- **v1.34.0** — major spread engine feature expansion

## v1.33 — Spread engine performance

- **v1.33.0** — spread engine performance overhaul

## v1.32 — Update resilience + security review

- **v1.32.3** — 5 s unmount timeout + force-exit fallback
- **v1.32.2** — update loop: stop re-downloading same version repeatedly
- **v1.32.1** — updater not restarting app after update
- **v1.32.0** — all 12 findings from security review

## v1.31 — FXP spread engine introduction

- **v1.31.7** — persist extraction watch folders to disk
- **v1.31.6** — Spread tab lockup on tab switching, resilient refresh
- **v1.31.5** — app lockup during update download and extraction
- **v1.31.3** — live transfers, race from notifications, affils
- **v1.31.2** — Browse tab perf, per-site skiplist, Spread setup guide
- **v1.31.0** — FXP spread engine with race jobs, dual-pane browser, skiplist

## v1.30 — Watch-folder auto-extract

- **v1.30.1** — delete-after-extract cleanup for RAR volume sets
- **v1.30.0** — watch folder auto-extract for drives and network paths

## v1.29 — Multi-format extractor

- **v1.29.2** — RAR multi-volume: total set size, modern `.partNN` naming
- **v1.29.0** — multi-format archive extractor with drag-drop and queue

## v1.28 — Remote glftpd installer

- **v1.28.0** — glftpd remote installer panel via SSH

## v1.27 — Torrent backends

- **v1.27.0** — replace broken 1337x scraper with apibay + SolidTorrents APIs

## v1.26 — Cast + DHT + torrent plumbing

- **v1.26.8** — UI freeze on pause/stop: move VLC calls off UI thread
- **v1.26.3** — Cast To context menu for Chromecast / DLNA / UPnP devices
- **v1.26.0** — enable DHT, listen endpoints, port forwarding for torrent peer discovery

## v1.25 — Upcoming tab, torrent search, IRC hyperlinks

- **v1.25.5** — memory leaks, deadlocks, hot-path inefficiencies
- **v1.25.1** — Upcoming tab includes streaming releases (Netflix, Amazon, etc.)
- **v1.25.0** — clickable release names in IRC, search FTP and download on click

## v1.24 — PreDB polish, parallel RAR playback

- **v1.24.3** — PreDB auto-refresh always runs, merge new releases, countdown bar
- **v1.24.2/1** — play first `.rar` immediately, download remaining volumes in background
- **v1.24.0** — parallel download + extract, library shows RAR files

## v1.23 — First streaming-from-RAR wins

- **v1.23.2** — start VLC playback as soon as first RAR volume downloads
- **v1.23.0** — fix data connection refused: CPSV for RAR downloads, throttle monitor

## v1.22 — Connection pool resilience

- **v1.22.4** — full download retry with connection resilience
- **v1.22.2** — pool: wait for existing conn when server refuses new ones
- **v1.22.1** — show active FTP connections per server in dashboard status bar
- **v1.22.0** — background RAR download + extract with live progress

## v1.21 — Threading + security

- **v1.21.6** — UI freeze during player loading: move FTP calls to background thread
- **v1.21.2** — dashboard crash fix (missing `ModeTabStyle`), security hardening

## See also

- [project-overview-pdr.md](project-overview-pdr.md) — what the app is, why it exists
- [system-architecture.md](system-architecture.md) — the subsystems most of these fixes touched
- `git log --oneline` from the repo root — full history
