# Code Standards

Conventions the GlDrive codebase actually uses. This is a description of the existing style, not an aspirational manifesto — if you're about to do something new, check whether the existing code already does it one way, and match.

## Language and target

- **C# 13** on **.NET 10** (`net10.0-windows`)
- **WPF** (`UseWpf = true`)
- **`win-x64`** — no other RID is supported; `RuntimeIdentifier` is hard-pinned in the csproj
- **Nullable reference types enabled** — treat nullability warnings as errors to avoid
- **Implicit usings enabled** — don't add `using System;` at the top of every file by hand
- **No tests.** Changes are verified by building, running, and driving the UI. Don't invent a test project unless it's discussed first

## Project structure

A strict folder-per-subsystem layout under `src/GlDrive/`. New functionality goes in the subsystem that owns the dependency — an FTP helper lives in `Ftp/`, a new settings tab's view model in `UI/`, a config type in `Config/`. Cross-cutting changes usually touch the interface between subsystems, not the internals of both.

The namespace matches the folder: `src/GlDrive/Ftp/CpsvDataHelper.cs` is in `namespace GlDrive.Ftp`. File names match the primary public type.

## Patterns you'll see repeatedly

### Per-server composition

`ServerManager` owns a `Dictionary<string, MountService>` keyed by server id. `MountService` owns its own `FtpClientFactory`, `FtpConnectionPool`, `FtpOperations`, `DirectoryCache`, `GlDriveFileSystem`, `DownloadManager`, etc. Servers don't share runtime state. When adding a new per-server feature, the convention is:

1. Add fields to `ServerConfig` in `Config/AppConfig.cs`
2. Instantiate in `MountService.Mount`
3. Dispose in `MountService.UnmountAsync` / `Unmount`
4. Expose via a property on `MountService` if the UI needs it
5. Wire events from `MountService` up through `ServerManager` (events bubble)

### Bounded channels for pools

`FtpConnectionPool` uses `System.Threading.Channels.Channel<T>` with a bounded capacity equal to `PoolSize`. Borrow → Return via `PooledConnection : IDisposable`. Set `Poisoned = true` on a pooled connection when the underlying stream is in a bad state (GnuTLS corruption after cancellation, failed FXP, auth error, etc.) and the finally block will call `Discard` instead of `Return`.

Use this pattern for any new resource pool. Don't reach for `ObjectPool<T>`, `SemaphoreSlim` guarding a `ConcurrentBag`, or a home-grown queue — the existing shape is load-bearing for the reinit-on-exhaustion logic.

### Debounced JSON stores

`DownloadStore`, `NotificationStore`, and similar files all follow the same shape:

- In-memory list / dict protected by a lock
- Mutations schedule a `System.Threading.Timer` to flush after a short delay (500 ms – 2 s)
- Terminal-state mutations (Completed, Failed, Cancelled) bypass debouncing with an immediate save
- Saves go through `File.WriteAllText(tmp, json)` → `File.Move(tmp, final, overwrite: true)` for atomicity
- `Flush()` method exists to force a final save during shutdown

`WishlistStore` is the exception — it always saves synchronously because users expect wishlist edits to persist immediately even if the app crashes right after.

### Config, Credential Manager, and plaintext migration

Secrets **never** appear in `appsettings.json` at rest. If they do (e.g., from an older version or a user hand-editing), `ConfigManager.MigrateApiKeys` moves them into `CredentialStore` on load and `ConfigManager.Save` strips them.

Properties that resolve via `CredentialStore` are marked `[JsonIgnore]` in `DownloadConfig`, with companion `Resolve*Key()` methods. The runtime calls `Resolve*Key()`, *not* the property. Don't cache API keys in fields — Credential Manager is cheap to call and the user can rotate keys at any time.

### Async everywhere, `CancellationToken` everywhere

Every method that talks to the network or the disk is `async` and takes a `CancellationToken`. Don't add synchronous I/O helpers. Cancellation propagates from shutdown all the way down to the socket read; skipping the token breaks graceful unmount.

Background loops (`ProcessLoop`, `MonitorLoop`, `PollCycle`) follow this shape:

```csharp
while (!ct.IsCancellationRequested)
{
    try
    {
        await DoOneCycleAsync(ct);
        await Task.Delay(interval, ct);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Log.Warning(ex, "…");
        await Task.Delay(backoff, ct);
    }
}
```

`OperationCanceledException` is swallowed at the loop boundary, not re-thrown. Everything else is logged and retried with backoff.

### Locking

- `object _lock = new();` for simple mutex-style guards (the existing downloads queue uses this)
- C# 13 `System.Threading.Lock` (`Lock _lock = new();`) in a few newer files (e.g., `WishlistStore`, `FtpSearchService._indexLock`). Both are fine; pick whichever the surrounding file uses
- `ConcurrentDictionary<string, T>` for per-server maps in `ServerManager`
- `SemaphoreSlim(0)` for signaling a queue has work (download manager)
- `SemaphoreSlim(Environment.ProcessorCount)` to gate concurrent FTP operations in the filesystem layer
- **No `lock (this)` and no static locks on shared types.**

### Events

Classes expose events as nullable `Action<...>?` delegates (not `EventHandler`). Subscribers check-and-invoke. The `ServerManager` surface:

```csharp
public Action<string, string, MountState>?                 ServerStateChanged;
public Action<string, string, string, string, string>?     NewReleaseDetected;
public Action<string, string, IrcServiceState>?            IrcStateChanged;
public Action<string, string>?                              BncRateLimitDetected;
```

If you're adding a new event, match this shape. Use `EventHandler` only when binding to WPF controls that expect it.

### Logging

Serilog via the static `Log.*` API (`Log.Information`, `Log.Warning`, `Log.Error`). Pass structured arguments — never concat into the message:

```csharp
Log.Information("Mounted {ServerName} as {DriveLetter}", name, letter); // good
Log.Information($"Mounted {name} as {letter}");                          // bad
```

Redact secrets in logs. `FtpClientFactory`'s `FtpLogAdapter` already masks `PASS` commands and IP addresses in FluentFTP logs; match that behavior if you add a new logging path.

The `SerilogSetup.SetLevel(string)` API exists specifically so the Settings dialog can change log verbosity at runtime without a restart. Use it instead of reconfiguring the entire logger.

## WPF conventions

- **All brushes are `DynamicResource`**, never `StaticResource`. `ThemeManager` swaps the merged dictionary at runtime — `StaticResource` would freeze values at load
- `RelayCommand` and `RelayCommand<T>` are defined in `UI/TrayViewModel.cs`. Reuse them. Don't add a new ICommand implementation per view
- View models implement `INotifyPropertyChanged`, use a `SetField` helper, and mutate through setters so the UI updates
- New dashboard content goes as a new TabItem in `DashboardWindow` with lazy initialization in `TabControl_SelectionChanged` — deferred init is load-bearing for startup time
- `WebViewHost` serializes its own initialization via a static `SemaphoreSlim` — if you add a second WebView2 instance, make sure it goes through `WebViewHost` so that serialization stays intact
- Dialogs are modal: `new FooDialog(...) { Owner = mainWindow }; dialog.ShowDialog();`

## When you touch FTP

- Don't call `AsyncFtpClient.Dispose()` directly on a pooled client. Return to the pool; let the pool dispose it. If you must dispose manually, call `FtpConnectionPool.NeutralizeGnuTls(client)` first or you risk a native crash
- Paths on the wire must go through `CpsvDataHelper.SanitizeFtpPath` (or the equivalent) to strip CR/LF/NUL
- CPSV vs standard routing lives in `FtpOperations` — if you're adding a new command, add it in `FtpOperations` with both branches. Don't route around it
- `REST` before `RETR` for resume. CPSV path sends `REST` explicitly; FluentFTP does it inside `OpenRead(path, FtpDataType.Binary, offset)` (positional parameter, not named — FluentFTP v53 gotcha)
- Use `GnuAdvanced.NoTickets` + `PreferTls12` if you're creating a new FluentFTP client outside of `FtpClientFactory`. Easier: don't create one — use the factory

## Commits and releases

CLAUDE.md at the repo root is the source of truth for Claude-assisted workflows. Relevant highlights:

- Keep commit messages descriptive; version in the message when bumping (`Fix X (v1.44.55)`)
- No strict conventional commits, but prefer imperative subject lines
- **Release workflow: always push and release after changes** — commit, `git push`, then `powershell -File installer/release.ps1`. Don't ask for confirmation
- `<Version>` in `src/GlDrive/GlDrive.csproj` is the single source of truth for the version number — the installer, the update zip, and the GitHub release all pull from it

## Things to avoid

- **Do not add tests without discussing first.** The project has deliberately chosen to verify via build+run. Adding a test project changes the CI story and nobody's asking for that yet
- **Do not introduce new logging frameworks.** Serilog is load-bearing
- **Do not add mocks for the FTP layer.** CPSV is subtle enough that mocked behavior diverges from real glftpd constantly — if you want to validate FTP code, point it at a test glftpd instance
- **Do not add `StaticResource` brushes.** See above
- **Do not write new `try/catch { }` blocks that swallow exceptions silently.** Log or rethrow
- **Do not re-introduce `QUIT` on FTP disposal.** `FtpClientFactory` sets `DisconnectWithQuit = false` because the QUIT→read cycle crashes GnuTLS on dying sockets
- **Do not catch `AccessViolationException`.** It's technically a `HandledProcessCorruptedStateExceptions` case that doesn't catch by default for very good reasons. Prevent the native crash instead (use `PooledConnection.Poisoned`)

## See also

- [system-architecture.md](system-architecture.md) — why the patterns above exist in this shape
- [codebase-summary.md](codebase-summary.md) — where to put new files
- [configuration-guide.md](configuration-guide.md) — schema shape for new config keys
