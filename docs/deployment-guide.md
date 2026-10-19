---
vault-doctor: skip
---

# Deployment Guide

Everything needed to go from a local checkout to a published GitHub Release, and from an older installed version to a new one via the in-app updater.

## Prerequisites

- **.NET 10 SDK** with the `win-x64` runtime identifier support
- **Windows 11** (WinFsp is Windows-only)
- **WinFsp driver** installed system-wide — users of the installer get it silently bundled, but developers installing from `dotnet run` need it pre-installed
- **Inno Setup 6** — `ISCC.exe` must be discoverable (see [`installer/build.ps1`](../installer/build.ps1) for the paths it checks)
- **`gh` CLI** authenticated against the target repo (for `release.ps1`)
- **PowerShell** — `build.ps1` and `release.ps1` are PowerShell scripts, run them from `powershell -File …`

`GlDrive.sln` includes the application and test projects. `dotnet build GlDrive.sln -c Release` builds both.

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
| `--apply-update <pid> <packageDir> <installDir>` | Elevated update applier for the interactive UAC fallback |
| `--apply-update-task` | Fixed entry point for the SYSTEM update task; reads the staged-package handoff |
| `--cleanup-old-updates` | Fixed entry point for the SYSTEM task that removes old update backups |
| `--screenshots` | Renders demo windows to a unique run directory under `%AppData%\GlDrive\screenshots` and exits. Each completed run includes a manifest of image hashes and the application binary version/hash |
| `--screenshot-output=<directory>` | With `--screenshots`, uses an explicitly owned new or empty output directory. Existing evidence is never overwritten |

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
3. Unless `-SkipPublish`: removes `installer/publish/`, restores locked Release dependencies, and publishes the application plus MCP adapter as self-contained win-x64 binaries. Records source/build provenance and published-file hashes in `installer/output/publish-manifest.json`
4. Warns if `installer/deps/winfsp.msi` is missing (optional — the installer bundles it only if present)
5. Unless `-SkipInstaller`: runs `ISCC.exe /DMyAppVersion=<version> installer/GlDrive.iss`, producing `installer/output/GlDriveSetup-v<version>.exe`
6. Always: `Compress-Archive installer/publish/* installer/output/GlDrive-v<version>-win-x64.zip` with `Optimal` compression

Flags:
- `-SkipPublish` — reuse `installer/publish/` only after its manifest matches the current source/build context and file hashes
- `-SkipInstaller` — skip Inno Setup, still produce the zip

Artifact sizes are logged so you can eyeball bloat regressions.

### `installer/release.ps1`

On top of `build.ps1`, the release script:

1. Reads the version the same way
2. Fails fast if `gh release view v<version>` finds an existing tag (bump the version)
3. Verifies the bundled WinFsp MSI pin, runs the Release tests with NuGet vulnerability warnings treated as errors, then invokes `build.ps1`
4. Requires `GlDrive-v<version>-win-x64.zip` and includes `GlDriveSetup-v<version>.exe` when present (otherwise warns that the release will contain only the zip)
5. Computes SHA-256 for both files using `System.Security.Cryptography.SHA256` (*not* `Get-FileHash` — this avoids an encoding gotcha the updater hits)
6. Writes `installer/output/checksums.sha256` in the format `<hash> *<filename>` (GNU coreutils style with the `*` binary-mode marker — `UpdateChecker` depends on this)
7. Signs the checksum file with the gitignored RSA private key and includes `checksums.sha256.sig`
8. Runs `gh release create` targeting the current commit, using `docs/releases/v<version>.md` as release notes when present (otherwise generated notes)

Standard order: commit and push the reviewed version, verify GitHub CI, then run `powershell -File installer/release.ps1`. Verify the published asset hashes/signature, installed version, control API health, and application logs. Record remaining external failures and the production observation window in the release report.

If the working checkout is under OneDrive, use a clean release worktree outside OneDrive
at the exact pushed commit. OneDrive marks source directories as reparse points, which
the publish-provenance guard rejects. Do not disable that guard. Verify the worktree's
commit and status, provision the ignored checksum signing key and pinned WinFsp MSI,
then run the standard release script from PowerShell in that worktree. Remove the
temporary signing-key copy after the release. Keep build/test and package-verification
evidence with the release report.

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
- Registers the fixed SYSTEM tasks `GlDrive Update Installer` and `GlDrive Update Cleanup` using the bundled `register-update-task.ps1`
- `{app}\GlDrive.exe` with `postinstall skipifsilent` — launches the app after a successful interactive install
- Uninstall first runs `taskkill /F /IM GlDrive.exe` so the uninstaller can delete `{app}`
- Uninstall removes both scheduled tasks

`[Code] IsWinFspInstalled()` checks `{sys}\winfsp-x64.dll` and `HKLM\SOFTWARE\WinFsp` and returns true if either is present.

**What the uninstaller deliberately keeps:** `%AppData%\GlDrive` is **not** touched on uninstall. User config, trusted certs, credentials, wishlists, race history, and FiSH keys all survive an uninstall/reinstall cycle. If you want a truly clean removal, delete `%AppData%\GlDrive` manually after uninstall.

## In-app update flow

Implemented by `Services/UpdateChecker.cs`, `Services/UpdateTaskHandoff.cs`, the updater entry points in `Program.cs` / `App.xaml.cs`, and the watchdog.

Round-trip for an end-user pressing "Update":

1. `UpdateChecker.CheckForUpdateAsync()` → GET `https://api.github.com/repos/misterentity/GlDrive/releases/latest`, parses `tag_name` into a `Version`, compares component-wise against `CurrentVersion`
2. `DownloadAndInstallAsync(release)` downloads the `*win-x64*.zip` asset to `%TEMP%\GlDrive-<tag>.zip`, using a segmented download with a single-stream fallback.
3. Downloads `checksums.sha256` and `checksums.sha256.sig`, verifies the RSA signature with the embedded public key, then verifies the zip's SHA-256 against the signed manifest. Missing or invalid evidence rejects the update and deletes the downloaded zip.
4. Stages the unopened zip and verification files under `%TEMP%\gldrive-update-<guid>`. Writes authenticated update/authorization markers before launching an updater.
5. Prefers the installed `GlDrive Update Installer` SYSTEM task, passing a handoff through `%ProgramData%\GlDrive\pending-update.json`. If that task is unavailable or cannot start, launches the **currently installed** executable with `--apply-update <pid> <packageDir> <installDir>` and UAC elevation.
6. After launch succeeds, `RestartRequested` deletes `.running`, unmounts servers, and terminates the main process. The watchdog waits for update completion.
7. The elevated updater validates the handoff and package location, pins the install destination to its own directory, and rechecks the publisher signature, zip hash, and strictly newer package version. Interactive elevation also requires the authenticated authorization marker. It waits up to 60 seconds for the original process to exit, pauses for file handles, then revalidates before installation.
8. Extracts the locked, verified archive fully before renaming installed files to `*.old`, then copies the staged files into the installation. A failed installation restores backups and attempts to relaunch the previous version.
9. On success, refreshes the update tasks and requests a desktop relaunch. On success or rollback, signals the original interactive watchdog through `%ProgramData%\GlDrive\update-complete-<pid>.signal`, then terminates the updater. The watchdog can relaunch the app in the user's session when SYSTEM cannot.
10. Next startup removes `*.old` files; the `GlDrive Update Cleanup` SYSTEM task handles backups requiring elevation and retries while old runtime files remain locked.

Download/handoff messages appear in the application log. Installation and rollback are logged to `%ProgramData%\GlDrive\update-install.log` and the updater account's `%TEMP%\gldrive-update.log`. Task dispatch and cleanup have separate `%ProgramData%\GlDrive\update-task.log` and `update-cleanup.log` files.

## Troubleshooting releases

| Symptom | Likely cause | Fix |
|---|---|---|
| `build.ps1` can't find ISCC | Inno Setup 6 not installed, or in a nonstandard path | Install Inno Setup 6; add `ISCC.exe` to `PATH`; or edit the search list in `build.ps1` |
| `release.ps1` fails with "tag already exists" | Version wasn't bumped | Bump `<Version>` in `src/GlDrive/GlDrive.csproj`, rerun |
| `gh` asks to authenticate | `gh auth status` returns "not logged in" | `gh auth login` |
| Users report update downloads but never applies | Missing/invalid signed checksums, failed task handoff, or failed elevation | Check the application and shared update logs; rebuild invalid release assets with `release.ps1`. Re-run the installer if the update task is missing |
| Installer leaves WinFsp not installed | `installer/deps/winfsp.msi` missing at build time | Restore the MSI matching `$WinFspMsiPin` in `release.ps1` and rebuild. Change the pin only for an intentional, verified WinFsp upgrade |
| Update completes but app doesn't restart | Failed desktop relaunch or watchdog completion handoff | Check `update-install.log`, the completion signal, and watchdog log. Confirm no updater is active before removing a stale `%AppData%\GlDrive\.updating` marker and relaunching |
| Update applies but old files remain | SYSTEM-owned or still-locked `*.old` backups | Check `update-cleanup.log` and the `GlDrive Update Cleanup` task; startup requests privileged cleanup with a retry window |

## See also

- [configuration-guide.md](configuration-guide.md) — what survives an update (all of `%AppData%\GlDrive\`)
- [system-architecture.md#update--crash-recovery](system-architecture.md#update--crash-recovery) — sequence diagram for the update path
- [codebase-summary.md#entry-points--modes](codebase-summary.md#entry-points--modes) — the argv dispatch table `Program.Main` uses
