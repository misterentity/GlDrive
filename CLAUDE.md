# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

GlDrive — a Windows 11 tray app that mounts glftpd FTPS servers as local drive letters using WinFsp + FluentFTP + GnuTLS. Single project, .NET 10 WPF targeting win-x64. **No test project, no test runner** — verification is manual via `dotnet build` + runtime exercise.

## MANDATORY pre-commit check

This repo lives on OneDrive, which periodically corrupts git's index view (see "OneDrive + git hazard" below). Before EVERY `git commit` in this repo:

```bash
git status --short | grep -v "^??"
```

Confirm ONLY the files you actually modified show as `M`. If any `D ` (deleted) entries appear that you didn't delete — **DO NOT COMMIT**. Run `git reset --mixed HEAD` to unstage (disk files stay intact), then re-stage the specific files by name. Historical near-misses have staged 130+ unwanted deletions.

## Build & Run

```bash
dotnet build src/GlDrive/GlDrive.csproj
dotnet run --project src/GlDrive/GlDrive.csproj
```

The `.sln` file has no project references — always build via the `.csproj` directly. Version of record lives in `src/GlDrive/GlDrive.csproj` `<Version>` property.

### Release

**Standard workflow after any code change**: `git commit` → `git push` → `powershell -File installer/release.ps1`. Don't ask for confirmation.

```bash
# Publish + Inno Setup installer + update zip only
powershell -File installer/build.ps1
# Full release (build + GitHub release with both assets)
powershell -File installer/release.ps1
```

Version is read from the csproj `<Version>` property. The installer script passes it to ISCC via `/D`.

## Architecture

**Startup flow** (`Program.cs` → `App.xaml.cs`): `--watchdog` mode or `--apply-update` mode checked first → `RegisterApplicationRestart` for native crash recovery → SingleInstanceGuard (with retry for crash restarts) → ConfigManager.Load → SerilogSetup → first-run WizardWindow (if no config) → CertificateManager → ServerManager.MountAll → TrayIcon. Watchdog process monitors parent PID and restarts on crash (`.running`/`.updating` markers).

### Multi-Server

`ServerManager` orchestrates multiple servers via `Dictionary<string, MountService>`. Each `MountService` creates its own independent chain: `FtpClientFactory` → `FtpConnectionPool` → `FtpOperations` → `DirectoryCache` → (optionally) `GlDriveFileSystem` → `FileSystemHost`. Servers can connect without a drive letter (search, downloads, notifications still work). WinFsp prefix per server: `\GlDrive\{serverId}`.

### Key Layers

- **Config** — `AppConfig` has `List<ServerConfig> Servers` + global `DownloadConfig` + `LoggingConfig`. `ServerConfig` holds per-server connection, mount, TLS, cache, pool, and notification settings. Passwords in Windows Credential Manager via `CredentialStore`. Config at `%AppData%\GlDrive\appsettings.json` (camelCase JSON). `ConfigManager` auto-migrates old single-server format.
- **Ftp** — `FtpClientFactory` creates FTPS clients with GnuTLS (or `AsyncFtpClientSocks5Proxy` when proxy enabled); `KillGhosts()` connects with `!username` to clear stale BNC sessions. `FtpConnectionPool` is a bounded `Channel<AsyncFtpClient>` pool with auto ghost-kill on connection failure. `Reinitialize()` revives fully exhausted pools (all connections poisoned/discarded). `IsExhausted` property detects dead pools. `PooledConnection.Poisoned` flag causes `Discard` instead of `Return` — prevents reuse of corrupt GnuTLS streams. Pool fails fast with `InvalidOperationException` when `_created <= 0` instead of hanging on empty channel. `FtpOperations` routes through either standard FluentFTP or `CpsvDataHelper` based on server capability. `StreamingDownloader` handles FTP-to-disk streaming with resume support.
- **Filesystem** — `GlDriveFileSystem : FileSystemBase` is the WinFsp implementation. Whole-file read/write buffering (`ReadBuffer`/`WriteBuffer` on `FileNode`). `DirectoryCache` is a TTL-based `ConcurrentDictionary` with LRU eviction. `NtStatusMapper` translates FTP exceptions to NTSTATUS.
- **Services** — `ServerManager` lifecycle, `MountService` per-server orchestration, `ConnectionMonitor` NOOP keepalive (30s) with exponential backoff reconnect, `NewReleaseMonitor` polls `/recent/` categories, `UpdateChecker` for app updates.
- **Downloads** — `DownloadManager` + `DownloadStore` per server (`downloads-{serverId}.json`). Queue uses `List<DownloadItem>` + `SemaphoreSlim`. Features: resume, speed limiting, auto-retry with exponential backoff, scheduling, SFV verification, category download paths. `WishlistMatcher` auto-downloads from `WishlistStore` (global `wishlist.json`). `FtpSearchService` searches categories in parallel.
- **Spread/FXP** — `SpreadManager` runs cbftp-style race engine for site-to-site FXP transfers. Each server gets a dedicated FXP `FtpConnectionPool` (separate from filesystem/downloads); pool size auto-scales to `max(SpreadPoolSize, maxSlots)`. Exhausted spread pools auto-reinitialize before each race via `ReinitDeadPools` (spread pools have no keepalive unlike main pools). After `ReinitDeadPools` the job re-captures its pool snapshot via `SpreadJob.UpdatePools` — the original snapshot may point at a disposed pool that got replaced. `SpreadJob` orchestrates per-race with 0-65535 scoring (SFV priority, file size, route speed, site priority, ownership). `TryClaimSlots`/`ActiveTransfers` tracks per-site slot usage; `ExecuteTransfer` borrows both connections via `Task.WhenAll` + `IsCompletedSuccessfully` extraction so a one-sided failure doesn't orphan the peer connection. 30s borrow timeout prevents slot leaks from exhausted pools. `_raceQueue` deduplicates by `(section, release)` so the same release can't be queued twice from IRC + notification polling. `StartRace` dedups the participant list with `Distinct()` and honors `MaxConcurrentRaces` config (was previously hardcoded to 1). In-flight files tracked via `_inFlightFiles` HashSet to prevent duplicate transfers to the same destination. Scan rejects `-MISSING-*` / `*.missing` / 0-byte `-*` zipscript placeholder stubs — they signal a LACK of the file, not its presence, and caused false 100% completion. After each scan cycle, `ScanSites` reconciles `FilesTotal` across every site against the final `_fileInfos.Count` so sites processed early in the loop don't freeze with a partial snapshot. Four FXP modes: PASV-PASV, CPSV-PASV, PASV-CPSV, Relay (CPSV-CPSV pipes through local memory). SSCN ON sent before PASV/PORT for secure FXP data channels. `SendTypeI` verifies TYPE I response before CPSV/PASV to prevent BNC response queue desync. Failed transfers poison connections (GnuTLS corruption). `SkiplistEvaluator` applies cascading allow/deny rules. Auto-race filters per-site denies (rules + metadata filter) — denying sites are dropped from the race instead of aborting the whole thing. `RaceHistoryStore` persists results. Auto-race triggers from `NewReleaseMonitor` (passes source server + path) and IRC announces (passes source server id — `IrcAnnounceListener` is registered whenever SpreadManager exists so the built-in `[ NEW ] in [ section ] Release` verbose pattern works without user-configured rules; falls back to `SpreadConfig.AutoRaceOnNotification` for default autoRace flag).
- **Player** — `MediaStreamServer` runs a local HTTP server that streams media from FTP for VLC playback. `PlayerViewModel` handles VLC + Chromecast. `TorrentSearchService` + `TorrentStreamService` (MonoTorrent) for torrent streaming. `PlayerResumeStore` tracks playback position.
- **IRC** — `IrcClient` (TcpClient + SslStream), `IrcService` wraps client + FiSH encryption + DH1080 key exchange + auto-reconnect. `FishCipher` (Blowfish ECB/CBC via BouncyCastle), `FishKeyStore` per server. `SITE INVITE` integration borrows FTP pool connections.
- **UI** — System tray via H.NotifyIcon, `DashboardWindow` (cross-server search/downloads/wishlist/IRC/notifications/spread/browse/PreDB/Streems), `SettingsWindow` (MVVM), `WizardWindow` (5-step, code-behind), `ExtractorWindow` (standalone archive extraction + watch folders + folder cleaner). Supporting dialogs: `ServerEditDialog`, `GlftpdInstallerWindow` (glftpd server install helper), `CleanupWindow` (folder cleanup), `MetadataSearchDialog`, `RuleTestDialog`, `TagRulesDialog`. `BrowseViewModel` drives the cross-server browse tab. `ThemeManager` swaps ResourceDictionaries at runtime — all XAML uses `DynamicResource`. `WebViewHost` wraps WebView2 with serialized initialization (`SemaphoreSlim`) to prevent concurrent deadlocks. `RelayCommand`/`RelayCommand<T>` in `TrayViewModel.cs`.
- **Tls** — `CertificateManager` implements TOFU with SHA-256 fingerprints in `trusted_certs.json`. Global, keyed by `host:port`.

## CPSV Data Connections

This is the most complex part of the codebase. glftpd behind a BNC requires CPSV instead of PASV for data connections. FluentFTP doesn't support CPSV natively, so `CpsvDataHelper` implements it manually:

1. Sends `CPSV` command, parses the returned backend IP:port
2. Opens raw TCP to the backend data address (different from control host)
3. Sends the data command (LIST/RETR/STOR) on the control channel
4. Negotiates TLS **as server** (`AuthenticateAsServerAsync`) — glftpd does `SSL_connect` on data channels
5. Uses a lazy self-signed X509Certificate2 (RSA 2048) for the data TLS server role

## AI Setup

`ServerEditDialog.AiSetup_Click` → `OpenRouterClient.AnalyzeSiteRules` — sends SITE RULES text + current sections + IRC message buffer (from `IrcPatternDetector.GetRecentMessages`) + detected patterns to an OpenRouter model, expects JSON back with `skiplist`, `sections`, `section_mappings`, `announce_rules`, `affils`, `priority`, etc.

**Critical prompt gotcha**: The system prompt must teach the model what `section_mappings.trigger` is. Without explicit natural-language instructions and non-`.*` example rows, the model copies `.*` verbatim for every mapping (v1.44.77 fixed this with realistic multi-target examples like `(?i).*\.1080p\..*` + a FIELD RULES paragraph demanding multiple discriminating rows when IRC sections mix release types).

**Merge semantics** (v1.44.76+): new section_mappings rows are added; existing `(IrcSection, RemoteSection)` matches get their `TriggerRegex` patched only if the existing value is still the default (`.*` or empty). User-edited triggers are preserved. `SectionMapping` is a POCO without `INotifyPropertyChanged`, so mutations require `SectionMappingsGrid.Items.Refresh()`.

**Diagnostics**: `AiSetup_Click` logs the invocation (model + input counts), AI response summary (counts per field), every returned `section_mapping` (irc/remote/trigger), and per-row merge decisions (`updating` / `preserving user-edited` / `AI suggested '.*'`). Grep logs for `AI Setup` to debug.

## Critical API Gotchas

- **WinFsp**: namespace is `Fsp` / `Fsp.Interop`. Must alias `using FileInfo = Fsp.Interop.FileInfo;` to avoid conflict with `System.IO.FileInfo`.
- **FluentFTP.GnuTLS**: `GnuAdvanced` enum values go directly in the `AdvancedOptions` list. Use `GnuAdvanced.NoTickets` with `PreferTls12` to work around glftpd's TLS 1.3 session ticket bug.
- **FluentFTP v53**: `OpenRead` resume param is positional (`path, FtpDataType.Binary, restart`), NOT named `restartPosition`.
- **H.NotifyIcon**: tray icon requires `ForceCreate(false)` + `GeneratedIconSource` to appear.
- **BNC rate limiting**: rapid reconnects trigger a ~2 hour cooldown on the BNC side.
- **BNC ghost connections**: `!username` login kills stale sessions. `FtpClientFactory.KillGhosts()` does this automatically when pool can't create new connections.
- **glftpd -MISSING placeholders**: when zipscript validates an SFV and a declared file is absent, it drops a 0-byte `-MISSING-<name>` stub. These are the INVERSE signal — never count them as owned files in the spread scanner. `SpreadJob.IsMissingPlaceholder()` filters them.
- **WPF DataGrid + POCO**: `SectionMapping` and similar POCOs don't fire `INotifyPropertyChanged`. Mutating their properties from code won't refresh the grid — call `SomeGrid.Items.Refresh()` after bulk mutations.

## OneDrive + git hazard

This project lives in `OneDrive\Documents\CursorProjects\fmountr\`. OneDrive sync periodically marks source files as "offline/removed" from git's perspective, even though they still exist on disk. When this happens, a subsequent `git add <specific-file>` can silently stage 130+ file deletions alongside the intended change. Two historical commits (`fd31872` "Restore 133 files", and the recovered v1.44.76 first attempt) were caused by this. See the **MANDATORY pre-commit check** section at the top of this file for the required workflow.

## Config Locations

- App config: `%AppData%\GlDrive\appsettings.json`
- Trusted certs: `%AppData%\GlDrive\trusted_certs.json`
- Downloads (per server): `%AppData%\GlDrive\downloads-{serverId}.json`
- Race history: `%AppData%\GlDrive\race-history.json`
- Extractor settings: `%AppData%\GlDrive\extractor-settings.json`
- Wishlist: `%AppData%\GlDrive\wishlist.json`
- FiSH keys: `%AppData%\GlDrive\fish-keys-{serverId}.json` (DPAPI encrypted)
- Logs: `%AppData%\GlDrive\logs\gldrive-{date}.log`
- Credentials: Windows Credential Manager, key format `GlDrive:{host}:{port}:{username}`
