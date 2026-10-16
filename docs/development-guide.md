# Development and test workflow

Use the smallest test graph that covers the change, then run the full Windows suite before release. Release validation still includes all three suites, signed-package verification, and native smoke checks.

## Fast Core checks

`src/GlDrive.Core` targets plain `net10.0` and has no NuGet dependencies, WPF, VLC, WinFsp or FTP runtime dependency. It contains scene-name parsing and its small DTOs, executable-file and release-artifact name predicates, source-availability policy, and shared download capacity limits. Existing namespaces and serialized enum values are preserved; the desktop assembly forwards the public types moved out of it.

From the repository root, with the .NET 10 SDK:

```powershell
dotnet restore src/GlDrive.Core.Tests/GlDrive.Core.Tests.csproj --locked-mode -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet test src/GlDrive.Core.Tests/GlDrive.Core.Tests.csproj -c Release --no-restore
```

These commands build only Core and its tests. They need no running app, credentials, mount driver or desktop session. CI runs this graph separately on Linux for early feedback; the complete solution gate also runs it on Windows.

The initial v3.11.18 Core test output measured **4.44 MiB** versus the prior **225.71 MiB** Windows test output. This is the output footprint of a smaller test graph, not a measured overall application speedup, startup/RSS improvement or replacement of the full test suite. The Windows integration output still contains its native dependencies.

## Full Windows checks

UI bindings/rendering, queue lifecycle and persistence, API integration, native-library guards, and consumer-specific artifact rules remain in `GlDrive.Tests`. `GlDrive.Mcp.Tests` covers the standalone bridge. Pure predicate coverage does not replace tests proving that callers use those predicates.

On Windows, from the repository root:

```powershell
dotnet restore GlDrive.sln --locked-mode -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build GlDrive.sln -c Release --no-restore
dotnet test GlDrive.sln -c Release --no-build --no-restore
powershell -NoProfile -File installer/tests/Test-PublishManifest.ps1
```

Native protocol, mounted-filesystem, sharing and dual-adapter checks remain separate opt-in fixtures under [tools/ReleaseCheck](../tools/ReleaseCheck/README.md). Follow that guide's isolation and platform prerequisites. A passing Core run does not replace those checks, WPF smoke, signed payload verification or deployment monitoring. Keep native connection-drain and quarantine assertions when refactoring.

## Portable and publish lockfiles

Core and MCP have two restore graphs:

- `packages.lock.json` records the ordinary portable graph used by tests.
- `packages.win-x64.lock.json` records an explicit Windows x64 publish restore. Their project files select it for either the singular `RuntimeIdentifier` used by publish or plural `RuntimeIdentifiers` used by `dotnet restore -r`.

The desktop app already targets `win-x64` and retains its existing `packages.lock.json`. The release pipeline explicitly restores app and MCP with `-r win-x64 -p:SelfContained=true --locked-mode` before publishing with `--no-restore`. The separate files prevent a publish restore from rewriting the portable lock and breaking the next locked development restore.

When dependencies change intentionally, regenerate and review both applicable graphs, then verify that the ordinary locked solution restore and explicit locked publish restores all succeed. Do not use `--force-evaluate` or discard a lockfile merely to bypass a release mismatch. Publish and test build properties differ; only reuse outputs whose provenance and properties have been verified by the standard pipeline.
