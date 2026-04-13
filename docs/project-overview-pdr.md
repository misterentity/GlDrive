# GlDrive — Project Overview & PDR

## What this is

GlDrive is a Windows 11 system tray application that mounts glftpd FTPS servers as native Windows drive letters. It combines a WinFsp virtual filesystem with a FluentFTP/GnuTLS client stack and layers on a full site-management suite: downloads, IRC, FXP racing, media playback, archive extraction, wishlist auto-download, and update distribution.

- **Runtime:** single-user desktop app (.NET 10, WPF, `win-x64`, self-contained)
- **Distribution:** Inno Setup installer + portable zip, delivered via GitHub Releases
- **Project layout:** single project — `src/GlDrive/GlDrive.csproj` (see [codebase-summary.md](codebase-summary.md))
- **Current version:** 1.44.55

## Who it is for

Users of **glftpd** private FTPS sites — an environment that has several properties most off-the-shelf FTP clients handle badly:

1. **Behind a BNC (bouncer)** — the control connection lands on one host, but data channels (PASV) would route back to a non-routable backend. glftpd's answer is **CPSV** (Clear Passive) with a reverse TLS data handshake.
2. **Stale sessions** — abrupt disconnects leave ghost logins that count against the per-user slot cap; the BNC convention `!<username>` kills them, but only if the client knows to do it.
3. **FXP racing** — site-to-site transfers with slot limits, SSCN encryption, and scene-specific conventions (SFV first, NFO-after-15s, nuke markers).
4. **TLS quirks** — glftpd has a TLS 1.3 session-ticket bug; GnuTLS crashes during stream teardown on corrupted sessions; ReadStaleDataAsync can segfault.
5. **IRC-driven workflows** — new releases are announced in encrypted (FiSH) channels, and `SITE INVITE` is required before joining.

None of these are off-the-shelf problems. The project exists to handle all of them in a single tray app that behaves like a normal Windows drive.

## Problem statement

> *"I want to browse my glftpd sites in Explorer, get toast notifications for new releases, auto-download things from my wishlist, race releases between sites, and chat on the site's IRC channel — all from one tray icon, without crashing when GnuTLS gets upset."*

## What it solves

| Problem | Solution |
|---|---|
| glftpd behind a BNC doesn't work with PASV | `CpsvDataHelper` implements CPSV with reverse TLS (server mode) — see [system-architecture.md#cpsv-data-connections](system-architecture.md#cpsv-data-connections) |
| TLS 1.3 session tickets crash glftpd | `FtpClientFactory` forces TLS 1.2 + `GnuAdvanced.NoTickets` |
| GnuTLS native crashes on disposal | `NeutralizeGnuTls()` closes raw sockets before disposal; `DisconnectWithQuit = false`; `StaleDataCheck = false` |
| Poisoned connections reused from pool | `PooledConnection.Poisoned` flag routes to `Discard` instead of `Return` |
| BNC ghost sessions | `FtpClientFactory.KillGhosts()` connects as `!username`, throttled to 30 s |
| Filesystem calls blocking on network | `DirectoryCache` is TTL + LRU + stale-while-revalidate |
| Large writes exhausting memory | `FileNode` spills writes to `DeleteOnClose` temp files above 50 MB |
| Release tracking and auto-download | `NewReleaseMonitor` + `WishlistMatcher` + `SceneNameParser` |
| Site-to-site racing | `SpreadManager` + `SpreadJob` + `FxpTransfer` — four FXP modes, 0-65535 scoring, skiplist cascade |
| Encrypted IRC announce channels | `IrcService` with BouncyCastle Blowfish (FiSH) + DH1080 |
| Native crash recovery | Watchdog subprocess + Windows Restart Manager + `.running` / `.updating` marker files |
| Self-updates without crashing on DLL teardown | `--apply-update` mode force-kills the updater process after launching the child |

## Non-goals

- **Cross-platform.** Windows 11 only. WPF + WinFsp + GnuTLS all assume Windows.
- **Multi-user / server.** Single-user desktop app. No daemon, no API, no UI for remote control.
- **Public FTP servers.** The design assumes glftpd conventions (CPSV, SITE SEARCH, SITE INVITE, scene release naming, BNCs).
- **Mobile.** No.
- **Public API / SDK.** Everything is internal; class names are a contract between the codebase and itself, not between the app and third parties.
- **Automated tests.** There are none. Changes are verified by building and running. See [code-standards.md](code-standards.md).

## Success criteria

A build is considered good when:

1. `dotnet build src/GlDrive/GlDrive.csproj` succeeds with no errors
2. `dotnet run --project src/GlDrive/GlDrive.csproj` launches into the tray, mounts configured servers, and the drive letter appears in Explorer
3. GnuTLS does not native-crash on disposal or stream teardown during normal use
4. `powershell -File installer/release.ps1` produces both a signed-layout installer exe and update zip, and the new release on GitHub contains `GlDriveSetup-v{version}.exe`, `GlDrive-v{version}-win-x64.zip`, and `checksums.sha256`
5. In-app update flow round-trips: user clicks Update → new version is downloaded, SHA-256 verified, extracted, applied via `--apply-update`, and the new process starts

See [deployment-guide.md](deployment-guide.md) for the release pipeline and [configuration-guide.md](configuration-guide.md) for per-server configuration.

## Architecture at a glance

```
Program.cs ──► App.xaml.cs ──► ServerManager ──► per-server MountService
                                                   ├── FtpClientFactory + FtpConnectionPool
                                                   ├── FtpOperations (routes CPSV vs PASV)
                                                   ├── GlDriveFileSystem (WinFsp)
                                                   ├── DirectoryCache
                                                   ├── DownloadManager + WishlistMatcher
                                                   ├── NewReleaseMonitor
                                                   └── FtpSearchService
                                                 ┌── IrcService (per server)
                                                 └── SpreadManager (global, per-server FXP pools)
```

Full subsystem map, Mermaid diagrams, and the CPSV protocol walkthrough live in [system-architecture.md](system-architecture.md).

## See also

- [codebase-summary.md](codebase-summary.md) — subsystem inventory, key dependencies, file layout
- [system-architecture.md](system-architecture.md) — Mermaid diagrams, data flow, CPSV details
- [configuration-guide.md](configuration-guide.md) — `%AppData%\GlDrive\appsettings.json` schema
- [deployment-guide.md](deployment-guide.md) — build, installer, release, update flows
- [design-guidelines.md](design-guidelines.md) — WPF themes, dashboard tabs, UX conventions
- [code-standards.md](code-standards.md) — conventions and patterns used across the codebase
- [changelog.md](changelog.md) — recent versions and notable changes
