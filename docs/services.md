---
title: Services — startup, lifecycle, monitors, auto-update
domain: services
status: active
last-reviewed: 2026-07-27
verified-against:
  - source: read at git 161d19f on 2026-07-27
---

# Services — startup, lifecycle, monitors, auto-update

> **What's in this doc:** the app startup flow, multi-server orchestration (`ServerManager` / `MountService`), the connection/heartbeat/new-release monitors, single-instance + watchdog crash recovery, and the self-updating pipeline including the busy-gate starvation fixes.
>
> **What's NOT:** the WinFsp filesystem each mount creates (→ [[filesystem]]); the connection pool internals (→ [[ftp]]); the racing engine (→ [[spread]]); the daily LLM tuning loop (→ [[ai-agent]]).

## Startup flow

`Program.cs` → `App.xaml.cs`. Order (`App.xaml.cs`):

1. `--apply-update` mode checked first (`App.xaml.cs:43`) — an elevated relaunch that replaces install files, then exits.
2. Watchdog crash marker read; a `CRASH:<timestamp>` marker logs a watchdog-confirmed crash restart (`App.xaml.cs:62`, `:70`).
3. `SingleInstanceGuard` (`App.xaml.cs:171`, `Services/SingleInstanceGuard.cs:6`), with retry so a crash-restart doesn't lose to its dying predecessor.
4. `SerilogSetup.Configure` (`App.xaml.cs:184`); telemetry sink wired at `:234`.
5. First-run `WizardWindow` if no config (`App.xaml.cs:281`).
6. `ServerManager.MountAll` (`App.xaml.cs:377`).

Native crashes leave **no crashdump** (AVE bypasses the AppDomain handler); the only in-log signal is the watchdog `[FTL] WATCHDOG: Process N crashed`. The real signature comes from `Get-WinEvent -ProviderName '.NET Runtime' -Id 1026`.

## Multi-server orchestration

`ServerManager` (`Services/ServerManager.cs:14`) holds a `Dictionary<string, MountService>` and mounts everything via `MountAll` (`Services/ServerManager.cs:166`). Each `MountService` (`Services/MountService.cs:13`) builds its own independent chain: `FtpClientFactory` → `FtpConnectionPool` → `FtpOperations` → `DirectoryCache` → (optionally) `GlDriveFileSystem` → WinFsp `FileSystemHost`. A server can connect **without** a drive letter — search, downloads, notifications, and racing still work. WinFsp prefix per server is `\GlDrive\{serverId}`.

## Monitors

- `ConnectionMonitor` (`Services/ConnectionMonitor.cs:8`) — 30s NOOP keepalive with exponential-backoff reconnect. Uses the `ReadReplyManagedTimeout` pattern (leave the native read draining rather than cancelling it) to avoid abandoning a recv — see [[ftp#gnutls-crash-fix]].
- `HeartbeatMonitor` (`Services/HeartbeatMonitor.cs`) — writes `last-heartbeat.json` (pid, version, working set), the authoritative "what's actually running" signal.
- `NewReleaseMonitor` (`Services/NewReleaseMonitor.cs:8`) — polls `/recent/` categories and feeds auto-races (→ [[spread#auto-race-triggers]]).
- `SiteStatsCollector` / `SiteStats` (`Services/SiteStatsCollector.cs`, `Services/SiteStats.cs`) — cached `SITE STATS` probing (6h positive/negative cache to avoid hammering ACL-restricted sites).

## Auto-update

<!-- verified-against: read Services/UpdateChecker.cs at git 161d19f on 2026-07-27 -->

`UpdateChecker` (`Services/UpdateChecker.cs:33`) polls the GitHub releases API every 3h (`CheckInterval`) and, when `AutoInstall` is set, downloads + applies without a manual tray click.

**Security of the pipeline:**
- Download URLs must be on an allowlisted GitHub host set (`AllowedDownloadHosts`), HTTPS only.
- The zip is SHA-256-verified against `checksums.sha256`, whose signature is RSA-verified against the pinned `ChecksumPublicKeyPem`.
- The elevated `ApplyUpdate` handoff is authorized by an HMAC-sealed, manifest-hashed marker (`UpdateMarkerHmac`) and re-validated after the old process exits.
- `MaxFailedInstallAttempts` = 3 blocks a fail→rollback→relaunch→retry loop for a broken release.

**Busy-gate starvation (the arc worth knowing):** `CanInstallNow` defers an install while a race is in flight so a restart doesn't kill a transfer. On a box that races continuously this starved updates:
- The gate is now **relaxed** to block only on in-flight FXP transfers, not on any active job — wired in `TrayViewModel` as `ActiveJobs.Sum(j => j.ActiveTransferList.Count) == 0` (queued/scanning jobs re-scan on restart, so they're safe). See [[spread]].
- A hold is **force-installed after 12h** (`MaxInstallDeferral`, `ShouldForceDeferredInstall`), with the clock persisted to `%AppData%\GlDrive\.update-deferred` so a restart doesn't reset it.
- A stuck hold **escalates visibly** at 6h (`EscalateDeferralAfter`, `ShouldEscalateDeferral`): ERR log once + the `UpdateInstallStalled` event → a tray notification, so the user can pause racing and apply cleanly before the forced install.

The **manual tray update bypasses `CanInstallNow` entirely** — the escape hatch for a starved instance.

## Config locations

See [[config]] for the full list (`appsettings.json`, credentials, per-server download stores, race history, logs, update markers).
