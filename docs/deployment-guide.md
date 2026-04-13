# Deployment Guide

Everything needed to go from a local checkout to a published GitHub Release, and from an older installed version to a new one via the in-app updater.

## Prerequisites

- **.NET 10 SDK** with the `win-x64` runtime identifier support
- **Windows 11** (WinFsp is Windows-only)
- **WinFsp driver** installed system-wide — users of the installer get it silently bundled, but developers installing from `dotnet run` need it pre-installed
- **Inno Setup 6** — `ISCC.exe` must be discoverable (see [`installer/build.ps1`](../installer/build.ps1) for the paths it checks)
- **`gh` CLI** authenticated against the target repo (for `release.ps1`)
- **PowerShell** — `build.ps1` and `release.ps1` are PowerShell scripts, run them from `powershell -File …`

The solution file `GlDrive.sln` has no embedded project references. Always drive builds against `src/GlDrive/GlDrive.csproj` directly.

## Local dev loop

```bash
dotnet build src/GlDrive/GlDrive.csproj
dotnet run   --project src/GlDrive/GlDrive.csproj
```

`dotnet run` spawns the watchdog the same way a packaged build does. If you want to debug without the watchdog supervising, launch the debugger from Visual Studio / Rider attached to the main `GlDrive.exe` process — the child watchdog will still be spawned but the debugger stays attached to the parent.

Useful argv during development:

| Argv | What it does |
|---|---|
| *(none)* | Normal startup, spawns watchdog |
| `--watchdog <pid>` | Runs the watchdog loop. Not invoked by hand |
| `--apply-update <pid> <extractDir> <installDir>` | In-place update applier, elevated. Invoked by `UpdateChecker.DownloadAndInstallAsync` |
| `--screenshots` | Renders wizard / dashboard / settings to `%AppData%\GlDrive\screenshots\*.png` and exits. Used to regenerate README screenshots |

## Release pipeline

Two scripts under `installer/`. `release.ps1` calls `build.ps1` internally, so day-to-day you only run `release.ps1`.

```
┌───────────────────────────┐
│ Bump <Version> in csproj  │
│ e.g. 1.44.55 → 1.44.56    │
└─────────────┬─────────────┘
              │
              v
┌───────────────────────────┐
│ powershell -File          │
│   installer/release.ps1   │
└─────────────┬─────────────┘
              │ reads csproj Version
              │ calls build.ps1
              v
┌───────────────────────────┐
│  installer/build.ps1      │
│  ─ dotnet publish         │
│    --self-contained       │
│    -c Release             │
│    -o installer/publish   │
│  ─ ISCC.exe /DMyAppVersion│
│  ─ Compress-Archive → zip │
└─────────────┬─────────────┘
              │ artifacts in installer/output/
              v
┌───────────────────────────┐
│  release.ps1 resumes:     │
│  ─ Verify no existing tag │
│  ─ Write checksums.sha256 │
│  ─ gh release create v…   │
└───────────────────────────┘
```

### `installer/build.ps1`

What it does in order:

1. Reads `<Version>` from `src/GlDrive/GlDrive.csproj` (e.g., `1.44.55`)
2. Locates `ISCC.exe` in: `C:\Program Files (x86)\Inno Setup 6\`, `C:\Program Files\Inno Setup 6\`, `%LOCALAPPDATA%\Programs\Inno Setup 6\`, or `Get-Command` fallback
3. Unless `-SkipPublish`: removes `installer/publish/` and runs `dotnet publish src/GlDrive/GlDrive.csproj -c Release --self-contained -o installer/publish` (self-contained, win-x64)
4. Warns if `installer/deps/winfsp.msi` is missing (optional — the installer bundles it only if present)
5. Unless `-SkipInstaller`: runs `ISCC.exe /DMyAppVersion=<version> installer/GlDrive.iss`, producing `installer/output/GlDriveSetup-v<version>.exe`
6. Always: `Compress-Archive installer/publish/* installer/output/GlDrive-v<version>-win-x64.zip` with `Optimal` compression

Flags:
- `-SkipPublish` — reuse `installer/publish/` from a previous run
- `-SkipInstaller` — skip Inno Setup, still produce the zip

Artifact sizes are logged so you can eyeball bloat regressions.

### `installer/release.ps1`

On top of `build.ps1`, the release script:

1. Reads the version the same way
2. Fails fast if `gh release view v<version>` finds an existing tag (bump the version)
3. Invokes `build.ps1`
4. Verifies both `GlDriveSetup-v<version>.exe` and `GlDrive-v<version>-win-x64.zip` exist
5. Computes SHA-256 for both files using `System.Security.Cryptography.SHA256` (*not* `Get-FileHash` — this avoids an encoding gotcha the updater hits)
6. Writes `installer/output/checksums.sha256` in the format `<hash> *<filename>` (GNU coreutils style with the `*` binary-mode marker — `UpdateChecker` depends on this)
7. Runs `gh release create v<version> <exe> <zip> <checksums.sha256> --title v<version> --generate-notes`

The `--generate-notes` flag pulls the commit log since the previous tag into the GitHub release body; there's no separate release-notes file.

### `installer/GlDrive.iss`

Inno Setup script compiled by `ISCC.exe` with `/D` defines from `build.ps1`.

Key parts:

```pascal
#define MyAppVersion  "0.0.0"       ; overridden by ISCC /DMyAppVersion=...
#define MyAppId       "{B8F3A1D2-7C4E-4F5A-9B6D-1E2F3A4B5C6D}"

[Setup]
AppId                 ={#MyAppId}
AppVersion            ={#MyAppVersion}
DefaultDirName        ={autopf}\GlDrive
OutputBaseFilename    =GlDriveSetup-v{#MyAppVersion}
Compression           =lzma2/ultra64
SolidCompression      =yes
PrivilegesRequired    =admin
WizardStyle           =modern
ArchitecturesAllowed  =x64compatible
MinVersion            =10.0.17763        ; Windows 10 1809+
```

Tasks:

- `desktopicon` (off by default) — creates a desktop shortcut
- `autostart` — adds `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\GlDrive`

Files section:

- All of `installer\publish\*` → `{app}` recursively
- `installer\deps\winfsp.msi` → `{tmp}\winfsp.msi` only if `Check: not IsWinFspInstalled`

Run / UninstallRun:

- `msiexec /i "{tmp}\winfsp.msi" /qn /norestart` — silent WinFsp install if it wasn't already present
- `{app}\GlDrive.exe` with `postinstall skipifsilent` — launches the app after a successful interactive install
- Uninstall first runs `taskkill /F /IM GlDrive.exe` so the uninstaller can delete `{app}`

`[Code] IsWinFspInstalled()` checks `{sys}\winfsp-x64.dll` and `HKLM\SOFTWARE\WinFsp` and returns true if either is present.

**What the uninstaller deliberately keeps:** `%AppData%\GlDrive` is **not** touched on uninstall. User config, trusted certs, credentials, wishlists, race history, and FiSH keys all survive an uninstall/reinstall cycle. If you want a truly clean removal, delete `%AppData%\GlDrive` manually after uninstall.

## In-app update flow

Implemented by `Services/UpdateChecker.cs` + the `--apply-update` branch of `App.OnStartup` in `App.xaml.cs`.

Round-trip for an end-user pressing "Update":

1. `UpdateChecker.CheckForUpdateAsync()` → GET `https://api.github.com/repos/misterentity/GlDrive/releases/latest`, parses `tag_name` into a `Version`, compares component-wise against `CurrentVersion`
2. `DownloadAndInstallAsync(release)` finds the `*win-x64*.zip` asset, streams it to `%TEMP%\GlDrive-v<tag>.zip`
3. Downloads `checksums.sha256`, verifies the SHA-256 against the zip (mandatory — missing or mismatched → abort and delete zip)
4. Extracts to `%TEMP%\gldrive-update-<guid>`, detecting and unwrapping the nested folder GitHub sometimes adds
5. **Authenticode check** — if the currently running binary has a signature, the update must be signed by the same issuer. Unsigned dev builds skip this step
6. Launches `GlDrive.exe --apply-update <pid> <extractDir> <installDir>` with `Verb = "runas"` (UAC elevation)
7. Fires `RestartRequested` — `TrayViewModel` responds by writing `.updating`, deleting `.running`, and force-killing the main process via `Process.Kill()`
8. The elevated updater process picks up `--apply-update` in `App.OnStartup`, validates that `extractDir` is under `%TEMP%` and `installDir` contains "GlDrive", waits up to 60 s for the parent PID to exit (kills it if stuck), pauses 2 s for file-handle release
9. Renames every existing `{app}\*` file to `*.old` (in-place backup)
10. Copies every extracted file into `{app}\`
11. Deletes `.updating`, launches `{app}\GlDrive.exe`, and **`Process.GetCurrentProcess().Kill()`s itself** — graceful exit goes through GnuTLS disposal which can segfault in this state
12. On the next startup, `App.xaml.cs` cleans up `*.old` files

All of these steps log to `%TEMP%\gldrive-update.log`.

## Troubleshooting releases

| Symptom | Likely cause | Fix |
|---|---|---|
| `build.ps1` can't find ISCC | Inno Setup 6 not installed, or in a nonstandard path | Install Inno Setup 6; add `ISCC.exe` to `PATH`; or edit the search list in `build.ps1` |
| `release.ps1` fails with "tag already exists" | Version wasn't bumped | Bump `<Version>` in `src/GlDrive/GlDrive.csproj`, rerun |
| `gh` asks to authenticate | `gh auth status` returns "not logged in" | `gh auth login` |
| Users report update downloads but never applies | `checksums.sha256` missing from release, or hash mismatch | Rebuild with `release.ps1` so checksums are regenerated together with the assets |
| Installer leaves WinFsp not installed | `installer/deps/winfsp.msi` missing at build time | Drop the latest WinFsp MSI into `installer/deps/` and rebuild |
| Update succeeds but app doesn't restart | `.updating` marker left over from a failed run interfering with the watchdog | Delete `%AppData%\GlDrive\.updating` manually |
| Update applies but old files remain | `*.old` files from the in-place backup | They're cleaned up on next normal startup; safe to delete by hand if needed |

## See also

- [configuration-guide.md](configuration-guide.md) — what survives an update (all of `%AppData%\GlDrive\`)
- [system-architecture.md#update--crash-recovery](system-architecture.md#update--crash-recovery) — sequence diagram for the update path
- [codebase-summary.md#entry-points--modes](codebase-summary.md#entry-points--modes) — the argv dispatch table `Program.Main` uses
