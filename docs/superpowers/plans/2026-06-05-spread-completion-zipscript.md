# Spread Zipscript-Completion + Alternate-Source Failover — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a spread race run until each destination is confirmed complete via glftpd zipscript (configurable markers OR all-SFV-files-present + no `-MISSING-` stubs), with completion-driven directory refreshes and reactive alternate-source failover when the release leaves the source.

**Architecture:** Per-destination completion state machine (`Transferring → AwaitingCompletion → {Complete | TimedOut}`) layered onto the existing `SpreadJob.RunAsync` tick loop. The scanner is taught to *capture* the completion markers and `-MISSING-` stubs it currently discards (without counting them as files). A separate completion-wait timer governs the await phase independent of the global hard timeout. Source-side `RETR 550 file-not-found` triggers a confirming re-probe; if the source truly lost the release, its ownership is purged and a parallel cross-site search (reusing the RequestFiller pattern) supplies an alternate source.

**Tech Stack:** C# / .NET 10 WPF, FluentFTP + GnuTLS, xUnit (`src/GlDrive.Tests`). Build via `dotnet build src/GlDrive/GlDrive.csproj`; test via `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`.

**Spec:** `docs/superpowers/specs/2026-06-05-spread-completion-zipscript-design.md`

---

## File map

| File | Responsibility | Change |
|------|----------------|--------|
| `src/GlDrive/Config/SpreadConfig.cs` | config knobs | add 6 fields |
| `src/GlDrive/Spread/SectionBlacklistStore.cs` (`MkdFailureClassifier`) | FTP error classification | add `IsSourceFileMissing` |
| `src/GlDrive/Spread/CompletionDetector.cs` | **NEW** — pure completion/marker/stub logic | create |
| `src/GlDrive/Spread/SpreadJob.cs` | race lifecycle | scan capture, per-dest state, await phase, failover hooks, SFV parse-once |
| `src/GlDrive/Spread/SpreadManager.cs` | orchestration | `SearchReleaseOnServers`, wire `SourceSearch` + `_getSearchService` |
| `src/GlDrive/Services/ServerManager.cs` | wiring | set `_spreadManager._getSearchService` |
| `src/GlDrive/Spread/RequestFiller.cs` | request filler | refactor onto `SearchReleaseOnServers` (DRY) |
| `src/GlDrive/Spread/RaceHistoryStore.cs` | history record | add `DestinationState` |
| `src/GlDrive.Tests/CompletionDetectorTests.cs` | **NEW** tests | create |
| `src/GlDrive.Tests/MkdFailureClassifierTests.cs` | classifier tests | add cases (create if absent) |

Engine code in `SpreadJob.RunAsync`, `ScanSites`, `ScanDirectoryRecursive`, and `ExecuteTransfer` is verified manually (`dotnet build` + run the app) per CLAUDE.md — only the extracted pure helpers in `CompletionDetector` and `MkdFailureClassifier` are unit-tested.

---

## Task 1: Config knobs

**Files:**
- Modify: `src/GlDrive/Config/SpreadConfig.cs:34` (after `NukeMarkers`)

- [ ] **Step 1: Add the new fields**

In `SpreadConfig`, immediately after the `NukeMarkers` line (currently `public List<string> NukeMarkers { get; set; } = [".nuke", "NUKED-"];`), insert:

```csharp
    /// <summary>
    /// When on, a race runs until each destination is confirmed complete via
    /// zipscript (a CompletionMarkers match OR all SFV files present + no -MISSING-
    /// stubs), not merely until the source files were copied. Off = legacy
    /// file-count completion. Default on.
    /// </summary>
    public bool WaitForDestinationComplete { get; set; } = true;

    /// <summary>Max minutes to wait for a destination's zipscript to mark complete
    /// AFTER all files are delivered, before that dest is recorded as a timeout.
    /// Independent of HardTimeoutSeconds (which budgets the transfer phase).</summary>
    public int DestinationCompletionWaitMinutes { get; set; } = 10;

    /// <summary>Directory re-list cadence (seconds) while waiting for completion
    /// (no active transfers). Keeps polling for the marker without hammering.</summary>
    public int CompletionRefreshIntervalSeconds { get; set; } = 30;

    /// <summary>Substrings (case-insensitive) that mark a release dir as complete in
    /// a destination listing. Site-tunable, like NukeMarkers. Empty = heuristic only.</summary>
    public List<string> CompletionMarkers { get; set; } =
        ["[ COMPLETE ]", "[ COMPLETED ]", "(COMPLETE)", "COMPLETE", "-=COMPLETE=-"];

    /// <summary>When a release moves off its source mid-race, search other connected
    /// sites for an alternate source and continue feeding the target from it.</summary>
    public bool AlternateSourceSearch { get; set; } = true;

    /// <summary>Per-server timeout (seconds) for the mid-race alternate-source search.</summary>
    public int AlternateSourceSearchTimeoutSeconds { get; set; } = 20;
```

- [ ] **Step 2: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/GlDrive/Config/SpreadConfig.cs
git commit -m "feat(spread): config knobs for zipscript-completion racing"
```

---

## Task 2: Pure completion/marker/stub detector

Create the unit-testable heart of completion detection as a standalone static class so the engine wiring later just calls it.

**Files:**
- Create: `src/GlDrive/Spread/CompletionDetector.cs`
- Create: `src/GlDrive.Tests/CompletionDetectorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/GlDrive.Tests/CompletionDetectorTests.cs`:

```csharp
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class CompletionDetectorTests
{
    private static readonly string[] Markers =
        ["[ COMPLETE ]", "(COMPLETE)", "COMPLETE", "-=COMPLETE=-"];

    [Theory]
    [InlineData("[ COMPLETE ] - 1080p - iNTERNAL", true)]
    [InlineData("(COMPLETE)", true)]
    [InlineData("-=COMPLETE=- (TEAM)", true)]
    [InlineData("complete", true)]                 // case-insensitive
    [InlineData("Sample", false)]
    [InlineData("file.rar", false)]
    [InlineData("", false)]
    public void IsCompletionMarker_matches_configured_substrings(string name, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsCompletionMarker(name, Markers));

    [Fact]
    public void IsCompletionMarker_empty_marker_list_never_matches()
        => Assert.False(CompletionDetector.IsCompletionMarker("[ COMPLETE ]", System.Array.Empty<string>()));

    [Theory]
    [InlineData("-MISSING-file.r01", 0, true)]
    [InlineData("-missing-file.r01", 0, true)]
    [InlineData("file.rar.missing", 0, true)]
    [InlineData("file.rar-missing", 0, true)]
    [InlineData("-foo", 0, true)]                   // 0-byte dash stub
    [InlineData("file.rar", 1000, false)]
    [InlineData("file.rar", 0, false)]             // 0-byte real-ish name, no dash
    public void IsMissingStub_detects_missing_placeholders(string name, long size, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsMissingStub(name, size));

    [Theory]
    // sawMarker short-circuits regardless of counts
    [InlineData(0, 10, true, false, DestState.Complete)]
    // heuristic: all files, no stub
    [InlineData(10, 10, false, false, DestState.Complete)]
    [InlineData(11, 10, false, false, DestState.Complete)]
    // missing stub present blocks heuristic completion even with full count
    [InlineData(10, 10, false, true, DestState.AwaitingCompletion)]
    // all files but waiting on marker (no marker, has stub) -> awaiting
    // not enough files yet
    [InlineData(5, 10, false, false, DestState.Transferring)]
    // unknown total (0) and no marker -> transferring
    [InlineData(3, 0, false, false, DestState.Transferring)]
    public void Evaluate_returns_expected_state(
        int owned, int total, bool sawMarker, bool hasMissing, DestState expected)
        => Assert.Equal(expected, CompletionDetector.Evaluate(owned, total, sawMarker, hasMissing));

    [Fact]
    public void AllTerminal_true_only_when_every_dest_complete_or_timedout()
    {
        Assert.True(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.TimedOut }));
        Assert.True(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.Complete }));
        Assert.False(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.AwaitingCompletion }));
        Assert.False(CompletionDetector.AllTerminal(new[] { DestState.Transferring }));
        Assert.False(CompletionDetector.AllTerminal(System.Array.Empty<DestState>())); // no dests = not done
    }

    [Theory]
    [InlineData(0, 10, false)]   // 0 min elapsed, 10 min budget
    [InlineData(10, 10, true)]   // exactly at budget
    [InlineData(15, 10, true)]   // over budget
    public void IsAwaitExpired_uses_minutes_budget(double elapsedMin, int budgetMin, bool expected)
    {
        var allFilesAt = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var now = allFilesAt.AddMinutes(elapsedMin);
        Assert.Equal(expected, CompletionDetector.IsAwaitExpired(allFilesAt, now, budgetMin));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter CompletionDetector`
Expected: FAIL — `CompletionDetector` / `DestState` do not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/GlDrive/Spread/CompletionDetector.cs`:

```csharp
namespace GlDrive.Spread;

/// <summary>Per-destination completion lifecycle state.</summary>
public enum DestState { Transferring, AwaitingCompletion, Complete, TimedOut }

/// <summary>
/// Pure (FTP-free, side-effect-free) logic for deciding whether a destination is
/// zipscript-complete, extracted from SpreadJob so it can be unit-tested. The
/// engine feeds it counts + scan-derived signals; it returns a DestState.
/// </summary>
public static class CompletionDetector
{
    /// <summary>True if a listing entry name contains any configured completion marker
    /// (case-insensitive). Empty marker list never matches.</summary>
    public static bool IsCompletionMarker(string name, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrEmpty(name) || markers == null) return false;
        foreach (var m in markers)
        {
            if (string.IsNullOrEmpty(m)) continue;
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>True for glftpd -MISSING- placeholder stubs (the INVERSE signal — the
    /// site LACKS the real file). Mirrors the subset of SpreadJob.IsZipscriptArtifact
    /// that specifically means "file absent".</summary>
    public static bool IsMissingStub(string name, long size)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("-missing", StringComparison.OrdinalIgnoreCase)) return true;
        if (size == 0 && name.StartsWith('-')) return true;
        return false;
    }

    /// <summary>
    /// Decide a destination's completion state from delivered-file count, the expected
    /// total, and the two scan-derived signals. Marker wins outright; otherwise the
    /// heuristic requires the full file set present AND no -MISSING- stubs remaining.
    /// Returns AwaitingCompletion when all files are present but completion isn't yet
    /// confirmed (waiting on a marker / stub still present); Transferring otherwise.
    /// Never returns TimedOut — that is a time-based decision the caller layers on.
    /// </summary>
    public static DestState Evaluate(int owned, int expectedTotal, bool sawMarker, bool hasMissingStub)
    {
        if (sawMarker) return DestState.Complete;
        var haveAllFiles = expectedTotal > 0 && owned >= expectedTotal;
        if (haveAllFiles && !hasMissingStub) return DestState.Complete;
        if (haveAllFiles) return DestState.AwaitingCompletion; // full count, but stub present / unconfirmed
        return DestState.Transferring;
    }

    /// <summary>Race is done only when there is at least one dest and every dest is in
    /// a terminal state (Complete or TimedOut).</summary>
    public static bool AllTerminal(IReadOnlyList<DestState> states)
    {
        if (states == null || states.Count == 0) return false;
        foreach (var s in states)
            if (s != DestState.Complete && s != DestState.TimedOut) return false;
        return true;
    }

    /// <summary>True when a dest has been awaiting completion past its minute budget.</summary>
    public static bool IsAwaitExpired(DateTime allFilesAt, DateTime now, int waitMinutes)
        => (now - allFilesAt) >= TimeSpan.FromMinutes(waitMinutes);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter CompletionDetector`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/GlDrive/Spread/CompletionDetector.cs src/GlDrive.Tests/CompletionDetectorTests.cs
git commit -m "feat(spread): pure completion/marker/stub detector + tests"
```

---

## Task 3: Source-file-missing classifier

Add a classifier that recognizes a source-side `RETR 550 file-not-found`, distinct from credit-exhaustion and from MKD missing-parent.

**Files:**
- Modify: `src/GlDrive/Spread/SectionBlacklistStore.cs` (`MkdFailureClassifier`, after `IsCreditExhaustion` ~line 358)
- Modify/Create: `src/GlDrive.Tests/MkdFailureClassifierTests.cs`

- [ ] **Step 1: Write the failing tests**

Find the existing test file:

Run: `ls src/GlDrive.Tests/MkdFailureClassifierTests.cs`

If it exists, add the class below into it (inside the existing `MkdFailureClassifierTests` class). If it does NOT exist, create `src/GlDrive.Tests/MkdFailureClassifierTests.cs`:

```csharp
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class MkdFailureClassifier_SourceMissingTests
{
    [Theory]
    [InlineData("RETR failed: 550 No such file or directory", true)]
    [InlineData("RETR failed: 550 File not found", true)]
    [InlineData("550 file.rar: No such file or directory", true)]
    [InlineData("RETR failed: 550 Cannot find the file", true)]
    public void IsSourceFileMissing_catches_retr_not_found(string msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsSourceFileMissing(msg));

    [Theory]
    [InlineData("RETR failed: 550 Insufficient credits")]   // credit path owns this
    [InlineData("STOR failed: 553 no upload rights")]        // dest-side
    [InlineData("MKD failed: 550 No such file or directory")] // MKD missing-parent, not source RETR
    [InlineData("data transfer timeout")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSourceFileMissing_ignores_non_source_missing(string? msg)
        => Assert.False(MkdFailureClassifier.IsSourceFileMissing(msg));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter MkdFailureClassifier`
Expected: FAIL — `IsSourceFileMissing` not defined.

- [ ] **Step 3: Write the implementation**

In `src/GlDrive/Spread/SectionBlacklistStore.cs`, inside `public static class MkdFailureClassifier`, immediately after the `IsCreditExhaustion` method (ends ~line 358 with its closing `}`), add:

```csharp

    /// <summary>
    /// True when a transfer failed because the SOURCE no longer has the file —
    /// glftpd "RETR ... 550 No such file" after the release was moved/deleted/
    /// archived off the source mid-race. Distinct from IsCreditExhaustion (also a
    /// RETR 550, but a credit balance issue) and from MKD missing-parent (a DEST
    /// 550 surfaced as "MKD failed"). Drives the alternate-source failover.
    /// </summary>
    public static bool IsSourceFileMissing(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var m = errorMessage;
        // Must be a download/RETR rejection, not an MKD or STOR failure.
        if (m.Contains("MKD failed", StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Contains("STOR failed", StringComparison.OrdinalIgnoreCase)) return false;
        // Credit exhaustion is its own (already-handled) source condition.
        if (IsCreditExhaustion(m)) return false;
        var notFound =
            m.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("File not found", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Cannot find the file", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        if (!notFound) return false;
        // Bias toward RETR/download context to avoid matching unrelated 550s.
        return m.Contains("RETR", StringComparison.OrdinalIgnoreCase)
            || m.Contains("550", StringComparison.Ordinal);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter MkdFailureClassifier`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GlDrive/Spread/SectionBlacklistStore.cs src/GlDrive.Tests/MkdFailureClassifierTests.cs
git commit -m "feat(spread): classify source-file-missing (RETR 550 not-found)"
```

---

## Task 4: SFV parse-once guard (stabilize expected count)

`ParseSfvForCount` (`SpreadJob.cs:1293`) does `_expectedFileCount += lineCount + 1` and `_pendingSfv` is re-set every scan the `.sfv` is seen (`ProcessFiles` line 1272) — so the expected count grows unbounded across scans and completion can never be reached. Parse each SFV path once. This is a prerequisite for the heuristic in Task 5.

**Files:**
- Modify: `src/GlDrive/Spread/SpreadJob.cs` (new field near line 47; guard in `ParseSfvForCount` ~1293)

- [ ] **Step 1: Add the parsed-SFV tracking field**

In `SpreadJob.cs`, right after `private (string serverId, string path)? _pendingSfv;` (line 47), add:

```csharp
    // SFV paths already counted into _expectedFileCount. Without this, the SFV is
    // re-found on every scan (ProcessFiles re-arms _pendingSfv) and the count grows
    // unbounded, so completion is never reached. Guarded by _ownershipLock.
    private readonly HashSet<string> _parsedSfvs = new(StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 2: Guard the parse**

In `ParseSfvForCount` (`SpreadJob.cs:1293`), replace the accumulation block. Change:

```csharp
            lock (_ownershipLock)
            {
                _expectedFileCount += lineCount + 1; // +1 for the SFV itself; accumulates across multiple SFVs
            }
```

to:

```csharp
            lock (_ownershipLock)
            {
                // Count each distinct SFV exactly once — re-parsing on every scan
                // would inflate the expected total without bound and block completion.
                if (_parsedSfvs.Add(sfvPath))
                    _expectedFileCount += lineCount + 1; // +1 for the SFV itself; accumulates across DISTINCT SFVs
            }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/GlDrive/Spread/SpreadJob.cs
git commit -m "fix(spread): parse each SFV once so expected file count is stable"
```

---

## Task 5: Capture completion signals during the scan

Teach `ScanDirectoryRecursive` to record, per site, whether it saw a completion marker and whether `-MISSING-` stubs are present — without letting them inflate `_fileInfos`. Surface these to `ScanSites`.

**Files:**
- Modify: `src/GlDrive/Spread/SpreadJob.cs` (`ScanSites` ~979, `ScanDirectoryRecursive` ~1148, new field + nested type)

- [ ] **Step 1: Add the per-site signal type and storage**

In `SpreadJob.cs`, after the `_parsedSfvs` field added in Task 4, add:

```csharp
    // Per-destination zipscript signals captured on each scan (see CompletionDetector).
    // _destSawMarker[id] = a completion marker was visible in id's release dir this scan.
    // _destHasMissingStub[id] = a -MISSING- stub was visible this scan.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _destSawMarker = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _destHasMissingStub = new();
```

And add this private nested class at the end of the `SpreadJob` class (just before the final closing `}` of the class):

```csharp
    /// <summary>Mutable per-site scan signals threaded through ScanDirectoryRecursive.</summary>
    private sealed class ScanSignals
    {
        public bool SawCompletionMarker;
        public bool HasMissingStub;
    }
```

- [ ] **Step 2: Thread `ScanSignals` through `ScanDirectoryRecursive`**

Change the signature (`SpreadJob.cs:1148`):

```csharp
    private async Task ScanDirectoryRecursive(FtpConnectionPool pool, string basePath,
        string currentPath, List<SpreadFileInfo> files, int depth, CancellationToken ct)
```

to:

```csharp
    private async Task ScanDirectoryRecursive(FtpConnectionPool pool, string basePath,
        string currentPath, List<SpreadFileInfo> files, ScanSignals signals, int depth, CancellationToken ct)
```

Update the recursive call inside the method (currently `await ScanDirectoryRecursive(pool, basePath, item.FullName, files, depth + 1, ct);`) to pass `signals`:

```csharp
                await ScanDirectoryRecursive(pool, basePath, item.FullName, files, signals, depth + 1, ct);
```

In the **directory** branch, immediately before `if (IsZipscriptArtifact(item.Name, item.Size)) continue;` (line 1199), add the signal capture:

```csharp
                if (CompletionDetector.IsCompletionMarker(item.Name, _spreadConfig.CompletionMarkers))
                    signals.SawCompletionMarker = true;
                if (CompletionDetector.IsMissingStub(item.Name, item.Size))
                    signals.HasMissingStub = true;
```

In the **file** branch, immediately before `if (IsZipscriptArtifact(item.Name, item.Size)) continue;` (line 1220), add the same two lines:

```csharp
                if (CompletionDetector.IsCompletionMarker(item.Name, _spreadConfig.CompletionMarkers))
                    signals.SawCompletionMarker = true;
                if (CompletionDetector.IsMissingStub(item.Name, item.Size))
                    signals.HasMissingStub = true;
```

- [ ] **Step 3: Collect signals in `ScanSites` and store per-dest**

In `ScanSites` (`SpreadJob.cs:979`), change the results accumulator type. Replace:

```csharp
        var results = new List<(string serverId, List<SpreadFileInfo> files)>();
```

with:

```csharp
        var results = new List<(string serverId, List<SpreadFileInfo> files, ScanSignals signals)>();
```

Inside the per-site task, create a `signals` object and pass it to both `ScanDirectoryRecursive` calls (main pool and spread-pool fallback). Replace the line `var files = new List<SpreadFileInfo>();` with:

```csharp
            var files = new List<SpreadFileInfo>();
            var signals = new ScanSignals();
```

Update the two recursion entry points inside `ScanSites`:
- `await ScanDirectoryRecursive(mainPool, basePath, basePath, files, 0, ct);` → `await ScanDirectoryRecursive(mainPool, basePath, basePath, files, signals, 0, ct);`
- `await ScanDirectoryRecursive(spreadPool, basePath, basePath, files, 0, ct);` → `await ScanDirectoryRecursive(spreadPool, basePath, basePath, files, signals, 0, ct);`

Update the `results.Add(...)` line (currently `lock (scanLock) results.Add((serverId, files));`):

```csharp
                lock (scanLock) results.Add((serverId, files, signals));
```

Update the loop that consumes results (currently `foreach (var (serverId, files) in results)`):

```csharp
            foreach (var (serverId, files, signals) in results)
            {
                var serverName = _serverConfigs.TryGetValue(serverId, out var cfg) ? cfg.Name : serverId;
                Log.Information("Spread scan: {Server} found {Count} files at {Path}",
                    serverName, files.Count, sitePaths.GetValueOrDefault(serverId, "?"));
                ProcessFiles(serverId, files);
                _destSawMarker[serverId] = signals.SawCompletionMarker;
                _destHasMissingStub[serverId] = signals.HasMissingStub;
            }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/GlDrive/Spread/SpreadJob.cs
git commit -m "feat(spread): capture completion-marker + missing-stub signals per dest during scan"
```

---

## Task 6: Per-dest completion state + await phase in the race loop

Layer the per-dest state machine onto the tick loop: evaluate completion after each reconcile, end the race when all dests are terminal, and keep the await phase alive (separate completion timer) instead of failing at the 60s idle timer.

**Files:**
- Modify: `src/GlDrive/Spread/SpreadJob.cs` (new fields ~line 124; reconcile tail of `ScanSites` ~1106; `RunAsync` ~529, ~556, ~620–751; new helper methods)

- [ ] **Step 1: Add completion state fields**

In `SpreadJob.cs`, after `private int _completionRetries;` (line 124), add:

```csharp
    // Per-destination completion lifecycle (see CompletionDetector). Keyed by dest
    // serverId. _destAllFilesAt stamps when a dest first held the full file set, so
    // the await timer can expire it. Both written under _ownershipLock during the
    // post-scan reconcile and read in the RunAsync completion gate.
    private readonly Dictionary<string, DestState> _destStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _destAllFilesAt = new(StringComparer.Ordinal);
```

- [ ] **Step 2: Evaluate completion after the reconcile pass**

At the end of `ScanSites`, the reconcile pass currently ends (line 1099–1107) by stamping `progress.FilesTotal` / `progress.IsComplete` and calling `ProgressChanged?.Invoke(this)`. Immediately AFTER the `lock (_progressLock) { ... }` block and BEFORE `ProgressChanged?.Invoke(this);`, insert the per-dest completion evaluation:

```csharp
        if (_spreadConfig.WaitForDestinationComplete)
            EvaluateDestCompletion(finalTotal);
```

- [ ] **Step 3: Add the completion evaluation + helper methods**

Add these methods to `SpreadJob` (place them next to `IsJobComplete` ~line 2069):

```csharp
    /// <summary>
    /// Recompute every non-download-only destination's completion state from the
    /// latest scan. Marker/heuristic → Complete; full file set but unconfirmed →
    /// AwaitingCompletion (stamping _destAllFilesAt); otherwise Transferring. The
    /// await→TimedOut transition is time-based and applied in the RunAsync gate.
    /// Called from ScanSites after the FilesTotal reconcile.
    /// </summary>
    private void EvaluateDestCompletion(int finalTotal)
    {
        lock (_ownershipLock)
        {
            foreach (var (serverId, _) in _siteProgress)
            {
                if (!_serverConfigs.TryGetValue(serverId, out var cfg)) continue;
                if (cfg.SpreadSite.DownloadOnly) continue;
                // Sources don't need to be "received" — skip the origin.
                var owned = _serverFileCount.GetValueOrDefault(serverId);
                var sawMarker = _destSawMarker.GetValueOrDefault(serverId);
                var hasMissing = _destHasMissingStub.GetValueOrDefault(serverId);
                var state = CompletionDetector.Evaluate(owned, finalTotal, sawMarker, hasMissing);

                // Preserve a prior terminal verdict (don't flap Complete back to
                // Transferring if a later scan momentarily under-counts).
                if (_destStates.TryGetValue(serverId, out var prev) &&
                    (prev == DestState.Complete || prev == DestState.TimedOut))
                    continue;

                _destStates[serverId] = state;
                if (state == DestState.AwaitingCompletion)
                    _destAllFilesAt.TryAdd(serverId, DateTime.UtcNow);
                else if (state == DestState.Transferring)
                    _destAllFilesAt.Remove(serverId);
            }
        }
    }

    /// <summary>
    /// True when every participating (non-download-only, not-dropped) destination is
    /// in a terminal state. Applies the await→TimedOut transition using the
    /// completion-wait budget. Used by the RunAsync gate when WaitForDestinationComplete.
    /// </summary>
    private bool AllDestinationsTerminal(Dictionary<string, string> sitePaths, HashSet<string> sourceServers)
    {
        lock (_ownershipLock)
        {
            var states = new List<DestState>();
            var now = DateTime.UtcNow;
            foreach (var id in sitePaths.Keys)
            {
                if (sourceServers.Contains(id)) continue;
                if (_serverConfigs[id].SpreadSite.DownloadOnly) continue;
                if (IsDestDropped(id)) { states.Add(DestState.TimedOut); continue; }

                var state = _destStates.GetValueOrDefault(id, DestState.Transferring);
                if (state == DestState.AwaitingCompletion &&
                    _destAllFilesAt.TryGetValue(id, out var at) &&
                    CompletionDetector.IsAwaitExpired(at, now, _spreadConfig.DestinationCompletionWaitMinutes))
                {
                    state = DestState.TimedOut;
                    _destStates[id] = state;
                    Log.Information("Spread: dest {Dst} timed out awaiting completion ({Min}min) — {Release}",
                        _serverConfigs.TryGetValue(id, out var c) ? c.Name : id,
                        _spreadConfig.DestinationCompletionWaitMinutes, ReleaseName);
                }
                states.Add(state);
            }
            return CompletionDetector.AllTerminal(states);
        }
    }

    /// <summary>True if any dest is still within its completion-wait window (so the
    /// idle timer must not fail the race — the await is legitimate pending work).</summary>
    private bool AnyDestAwaiting()
    {
        lock (_ownershipLock)
        {
            var now = DateTime.UtcNow;
            foreach (var (id, state) in _destStates)
            {
                if (state != DestState.AwaitingCompletion) continue;
                if (_destAllFilesAt.TryGetValue(id, out var at) &&
                    !CompletionDetector.IsAwaitExpired(at, now, _spreadConfig.DestinationCompletionWaitMinutes))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Compact per-dest completion summary for race history, e.g. "2 complete · 1 timeout".</summary>
    internal string DestinationStateSummary()
    {
        lock (_ownershipLock)
        {
            if (_destStates.Count == 0) return "";
            int complete = 0, timeout = 0, pending = 0;
            foreach (var s in _destStates.Values)
            {
                if (s == DestState.Complete) complete++;
                else if (s == DestState.TimedOut) timeout++;
                else pending++;
            }
            var parts = new List<string>();
            if (complete > 0) parts.Add($"{complete} complete");
            if (timeout > 0) parts.Add($"{timeout} timeout");
            if (pending > 0) parts.Add($"{pending} pending");
            return string.Join(" · ", parts);
        }
    }
```

- [ ] **Step 4: Use the new gate in `RunAsync`**

`sourceServers` is a local in `RunAsync` (built in Phase 1). The two new helpers need it; `AllDestinationsTerminal` takes it as a parameter, so no field promotion is required here.

In `RunAsync`, replace the completion check at line 622:

```csharp
                if (IsJobComplete())
                {
                    State = SpreadJobState.Completed;
                    Completed?.Invoke(this);
                    return;
                }
```

with:

```csharp
                var done = _spreadConfig.WaitForDestinationComplete
                    ? AllDestinationsTerminal(sitePaths, sourceServers)
                    : IsJobComplete();
                if (done)
                {
                    State = SpreadJobState.Completed;
                    Completed?.Invoke(this);
                    return;
                }
```

- [ ] **Step 5: Keep the await phase alive at the idle timer**

In `RunAsync`, the idle-timer keep-alive block (lines 663–669) currently refreshes `lastActivity` for backoff/cooldown. Extend its condition to also treat an active await window as pending work. Replace:

```csharp
                    var anyCooldown = AnyPoolInCooldown();
                    if (((nextBackoff is { } bAt && bAt > DateTime.UtcNow) || anyCooldown)
                        && idleSeconds < 180)
                    {
                        lastActivity = DateTime.UtcNow;
                        idleSeconds = 0;
                    }
```

with:

```csharp
                    var anyCooldown = AnyPoolInCooldown();
                    // A dest still inside its completion-wait window is pending work,
                    // not idle — don't let the 60s idle timer fail the race while we
                    // legitimately wait for zipscript. Bounded by the await budget.
                    var awaitingCompletion = _spreadConfig.WaitForDestinationComplete && AnyDestAwaiting();
                    var awaitCapSeconds = _spreadConfig.DestinationCompletionWaitMinutes * 60 + 60;
                    if (((nextBackoff is { } bAt && bAt > DateTime.UtcNow) || anyCooldown
                         || (awaitingCompletion && idleSeconds < awaitCapSeconds))
                        && idleSeconds < Math.Max(180, awaitCapSeconds))
                    {
                        lastActivity = DateTime.UtcNow;
                        idleSeconds = 0;
                    }
```

- [ ] **Step 6: Slow the scan cadence during await**

In `RunAsync`, the adaptive scan interval is computed at lines 552–556. Replace:

```csharp
                    // Adaptive interval: 2s during active transfers, 5s when idle
                    int activeCount;
                    lock (_progressLock)
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                    var scanInterval = activeCount > 0 ? 2.0 : 5.0;
```

with:

```csharp
                    // Adaptive interval: 2s during active transfers, 5s idle, and the
                    // (slower) completion-refresh cadence while only awaiting zipscript.
                    int activeCount;
                    lock (_progressLock)
                        activeCount = _siteProgress.Values.Sum(s => s.ActiveTransfers);
                    var scanInterval = activeCount > 0 ? 2.0 : 5.0;
                    if (activeCount == 0 && _spreadConfig.WaitForDestinationComplete && AnyDestAwaiting())
                        scanInterval = Math.Max(scanInterval, _spreadConfig.CompletionRefreshIntervalSeconds);
```

- [ ] **Step 7: Extend the global hard timeout to cover the await phase**

In `RunAsync`, the hard timeout is set at lines 529–531. Replace:

```csharp
            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hardTimeout.CancelAfter(TimeSpan.FromSeconds(_spreadConfig.HardTimeoutSeconds));
            var token = hardTimeout.Token;
```

with:

```csharp
            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // HardTimeoutSeconds budgets the transfer phase; add the completion-wait
            // window so the global timer can't kill a legitimate await phase, while a
            // wedged race still terminates at this absolute ceiling.
            var ceilingSeconds = _spreadConfig.HardTimeoutSeconds
                + (_spreadConfig.WaitForDestinationComplete
                    ? _spreadConfig.DestinationCompletionWaitMinutes * 60
                    : 0);
            hardTimeout.CancelAfter(TimeSpan.FromSeconds(ceilingSeconds));
            var token = hardTimeout.Token;
```

- [ ] **Step 8: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 9: Run full test suite (no regressions)**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: PASS — all tests (≥172 + new).

- [ ] **Step 10: Commit**

```bash
git add src/GlDrive/Spread/SpreadJob.cs
git commit -m "feat(spread): per-dest completion state machine + await phase in race loop"
```

---

## Task 7: Alternate-source search in SpreadManager

Add a reusable cross-site search that returns the best alternate source for a release, filtered by skiplist + section blacklist, and refactor `RequestFiller` onto it.

**Files:**
- Modify: `src/GlDrive/Spread/SpreadManager.cs` (new `_getSearchService` field ~line 67; new `SearchReleaseOnServers` method)
- Modify: `src/GlDrive/Services/ServerManager.cs:36` (wire `_getSearchService`)
- Modify: `src/GlDrive/Spread/RequestFiller.cs:111` (use the shared method)

- [ ] **Step 1: Add the search-service accessor field**

In `SpreadManager.cs`, after `public Func<string, FtpConnectionPool?>? _getMainPool;` (line 67), add:

```csharp
    // Per-server search accessor (MountService.Search). Wired by ServerManager so the
    // spread engine can locate an alternate source mid-race without a hard dependency
    // on ServerManager. Null => alternate-source search unavailable (skipped).
    public Func<string, GlDrive.Downloads.FtpSearchService?>? _getSearchService;
```

- [ ] **Step 2: Add `SearchReleaseOnServers`**

Add this method to `SpreadManager` (place after `GetConnectedServerIds`, ~line 930):

```csharp
    /// <summary>
    /// Search every connected server (except <paramref name="excludeIds"/>) in
    /// parallel for an exact match of <paramref name="release"/> and return the best
    /// alternate source. Candidates failing skiplist rules or section blacklist are
    /// dropped. Returns null when nothing usable is found. Reused by RequestFiller and
    /// by SpreadJob's mid-race source-migration failover.
    /// </summary>
    public async Task<SearchResult?> SearchReleaseOnServers(
        string release, IReadOnlyCollection<string> excludeIds, CancellationToken ct)
    {
        if (_getSearchService == null) return null;
        var candidates = GetConnectedServerIds()
            .Where(id => !excludeIds.Contains(id))
            .ToList();
        if (candidates.Count == 0) return null;

        var timeout = TimeSpan.FromSeconds(Math.Max(5, _config.Spread.AlternateSourceSearchTimeoutSeconds));

        var searches = candidates.Select(async id =>
        {
            var svc = _getSearchService(id);
            if (svc == null) return (SearchResult?)null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var results = await svc.Search(release, null, cts.Token);
                var exact = results.FirstOrDefault(r =>
                    r.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase));
                if (exact == null) return null;
                exact.ServerId = id; // ensure attribution
                return exact;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Alternate-source search failed on {Server}", id);
                return null;
            }
        });

        var found = (await Task.WhenAll(searches)).Where(r => r != null).Select(r => r!).ToList();
        if (found.Count == 0) return null;

        // Filter by rules + blacklist; prefer the candidate with the most files.
        var serverConfigs = _config.Servers.ToDictionary(s => s.Id, s => s);
        SearchResult? best = null;
        foreach (var r in found.OrderByDescending(r => r.Files?.Count ?? 0))
        {
            if (!serverConfigs.TryGetValue(r.ServerId, out var sc)) continue;
            if (_blacklist.IsBlacklisted(r.ServerId, r.Category)) continue;
            var action = _skiplist.Evaluate(release, true, false, r.ServerId, r.Category,
                sc.SpreadSite.Skiplist, _config.Spread.GlobalSkiplist);
            if (action == SkiplistAction.Deny) continue;
            best = r;
            break;
        }
        return best;
    }
```

> Note: confirm the exact `_skiplist.Evaluate(...)` overload at the call site by checking an existing usage in `SpreadManager.cs` (search `_skiplist.Evaluate`); match its parameter list. The signature above mirrors `ProcessFiles`' usage (`SpreadJob.cs:1249`). Adjust argument order if the available overload differs.

- [ ] **Step 3: Wire `_getSearchService` in ServerManager**

In `src/GlDrive/Services/ServerManager.cs`, find the `_spreadManager._getMainPool = serverId => ...` assignment (line 36). Immediately after that assignment statement, add:

```csharp
        _spreadManager._getSearchService = serverId =>
            _servers.TryGetValue(serverId, out var ms) ? ms.Search : null;
```

> Verify `_servers` is the `Dictionary<string, MountService>` field and `MountService.Search` is accessible (it is — `MountService.cs:55 public FtpSearchService? Search`). If the surrounding code uses a different accessor (e.g. `GetServer(serverId)`), use that instead: `var ms = GetServer(serverId); return ms?.Search;`.

- [ ] **Step 4: Refactor RequestFiller onto the shared method**

In `src/GlDrive/Spread/RequestFiller.cs`, replace the body of `TryFill` (lines 111–155) with:

```csharp
    private async Task TryFill(string release)
    {
        try
        {
            Log.Information("RequestFiller: searching for {Release}", release);

            var exact = await _spreadManager.SearchReleaseOnServers(
                release, new[] { _requesterServerId }, CancellationToken.None);
            if (exact == null)
            {
                Log.Information("RequestFiller: no source found for {Release}", release);
                return;
            }

            Log.Information("RequestFiller: found {Release} on {Source} at {Path} — racing to {Target}",
                release, exact.ServerId, exact.RemotePath, _requesterServerId);

            _spreadManager.StartRace(
                exact.Category,
                release,
                new[] { exact.ServerId, _requesterServerId },
                SpreadMode.Race,
                knownSourceServerId: exact.ServerId,
                knownSourcePath: exact.RemotePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RequestFiller: fill failed for {Release}", release);
        }
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Run full test suite**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/GlDrive/Spread/SpreadManager.cs src/GlDrive/Services/ServerManager.cs src/GlDrive/Spread/RequestFiller.cs
git commit -m "feat(spread): reusable cross-site alternate-source search; RequestFiller reuses it"
```

---

## Task 8: Source-migration failover wired into the race

When a transfer fails source-side with file-not-found, confirm the source lost the release, purge its ownership, and pull in an alternate source via the search.

**Files:**
- Modify: `src/GlDrive/Spread/SpreadJob.cs` (new `SourceSearch` delegate field; failover state; classifier branch in `ExecuteTransfer` ~1794; failover execution; `sourceServers` field promotion)
- Modify: `src/GlDrive/Spread/SpreadManager.cs:295` (wire `job.SourceSearch`)

- [ ] **Step 1: Add the failover delegate + state on SpreadJob**

In `SpreadJob.cs`, near the other public delegates (search for `public ... LivePoolResolver` / `AcquireTransferGates`), add a delegate the manager will set:

```csharp
    /// <summary>Set by SpreadManager: search connected sites (excluding the given ids)
    /// for an alternate source of this release. Returns the best (serverId, path,
    /// category) or null. Used by the source-migration failover.</summary>
    public Func<string, IReadOnlyCollection<string>, CancellationToken, Task<(string serverId, string path, string category)?>>? SourceSearch;
```

After the `_sourceCreditDenied` field (line 100), add:

```csharp
    // Sources confirmed to have LOST the release mid-race (moved/archived/deleted).
    // Purged from _fileOwnership and never reselected. Guarded by _ownershipLock.
    private readonly HashSet<string> _sourceMigratedAway = new(StringComparer.Ordinal);
    // Guards against concurrent failover searches for the same race.
    private int _failoverInFlight; // 0/1 via Interlocked
```

- [ ] **Step 2: Promote `sourceServers` to a field so failover can extend it**

`sourceServers` is currently a local `HashSet<string>` built in Phase 1 of `RunAsync`. Add an instance field after the other source state (after `_sourceMigratedAway`):

```csharp
    // The live source set. Built in RunAsync Phase 1, mutated by failover when a
    // source migrates away / an alternate is found. Read under _ownershipLock.
    private readonly HashSet<string> _sourceServersField = new(StringComparer.Ordinal);
```

In `RunAsync` Phase 1, wherever the local `sourceServers` is finalized (the `HashSet<string> sourceServers` built around lines 296–349), after it is fully populated add:

```csharp
            lock (_ownershipLock) { _sourceServersField.Clear(); foreach (var s in sourceServers) _sourceServersField.Add(s); }
```

> Keep the existing local `sourceServers` for the rest of `RunAsync`; the field is the failover-visible mirror. When failover adds a source it updates BOTH (the running loop closes over the local; see Step 4).

- [ ] **Step 3: Detect source-migration in the transfer-failure handler**

In `ExecuteTransfer` (`SpreadJob.cs`), the failure branch currently handles credit exhaustion then falls through to `RegisterDestFailure` (lines 1794–1815). Insert a new branch BETWEEN the credit-exhaustion `if` block (ends ~line 1804) and the `else { RegisterDestFailure(...) }`. Change:

```csharp
                if (MkdFailureClassifier.IsCreditExhaustion(transfer.ErrorMessage))
                {
                    ...
                    _forceScan = true;
                }
                else
                {
                    ...
                    RegisterDestFailure(dstId, dstBasePath, transfer.ErrorMessage,
                        IsMkdError(transfer.ErrorMessage));
                }
```

to:

```csharp
                if (MkdFailureClassifier.IsCreditExhaustion(transfer.ErrorMessage))
                {
                    bool firstTime;
                    lock (_ownershipLock) firstTime = _sourceCreditDenied.Add(srcId);
                    if (firstTime)
                        Log.Warning("Spread: source {Src} out of credits ([{Section}]) — parking as a " +
                            "transfer source for this race — {Error}",
                            _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId, Section,
                            transfer.ErrorMessage);
                    _forceScan = true;
                }
                else if (_spreadConfig.AlternateSourceSearch &&
                         MkdFailureClassifier.IsSourceFileMissing(transfer.ErrorMessage))
                {
                    // The source 550'd "no such file" — it may have moved the release
                    // off mid-race. Don't punish the (blameless) dest; confirm + fail over.
                    _ = HandleSourceMigration(srcId, sitePaths, _cts.Token);
                    _forceScan = true;
                }
                else
                {
                    RegisterDestFailure(dstId, dstBasePath, transfer.ErrorMessage,
                        IsMkdError(transfer.ErrorMessage));
                }
```

> Note: the original credit branch body is repeated here verbatim because the `else if` is inserted between it and the final `else`. Confirm the credit body matches the current source (lines 1796–1803) and keep it identical.

`ExecuteTransfer` must have access to `sitePaths` — it is already passed `sitePaths[dstId]` as `dstBasePath`. Check the `ExecuteTransfer` signature: it currently takes `(file, srcId, dstId, dstBasePath, ct)`. Add the `sitePaths` dictionary as a parameter so failover can probe/insert sources. Update the signature and its single call site (`RunAsync` line 779).

Change the call site (line 779):

```csharp
                        await ExecuteTransfer(file, srcId, dstId, sitePaths[dstId], xferTimeout.Token);
```

to:

```csharp
                        await ExecuteTransfer(file, srcId, dstId, sitePaths[dstId], sitePaths, xferTimeout.Token);
```

And update the `ExecuteTransfer` method signature to accept `Dictionary<string, string> sitePaths` (add the parameter before `CancellationToken ct`).

- [ ] **Step 4: Implement `HandleSourceMigration`**

Add to `SpreadJob` (near `ExecuteTransfer`):

```csharp
    /// <summary>
    /// Confirm a suspected source migration (RETR 550 not-found), purge the dead
    /// source's ownership, and — if no remaining source holds the still-missing files
    /// — search connected sites for an alternate source and splice it into the race.
    /// Nuke-guarded; single-flighted.
    /// </summary>
    private async Task HandleSourceMigration(string srcId, Dictionary<string, string> sitePaths, CancellationToken ct)
    {
        if (_isNuked) return;
        if (Interlocked.CompareExchange(ref _failoverInFlight, 1, 0) != 0) return; // already running
        try
        {
            lock (_ownershipLock)
                if (_sourceMigratedAway.Contains(srcId)) return; // already handled

            // 1. Confirming re-probe: does the source still have its release dir?
            if (!sitePaths.TryGetValue(srcId, out var srcPath)) return;
            var stillThere = await SourceStillHasRelease(srcId, srcPath, ct);
            if (stillThere)
            {
                Log.Information("Spread: source {Src} 550'd but release dir still present — transient, not migrating",
                    _serverConfigs.TryGetValue(srcId, out var sc0) ? sc0.Name : srcId);
                return;
            }

            // 2. Purge the dead source from ownership + the source set.
            lock (_ownershipLock)
            {
                _sourceMigratedAway.Add(srcId);
                _sourceServersField.Remove(srcId);
                foreach (var owners in _fileOwnership.Values) owners.Remove(srcId);
                _serverFileCount[srcId] = 0;
            }
            Log.Warning("Spread: source {Src} migrated the release away mid-race — searching for an alternate source ({Release})",
                _serverConfigs.TryGetValue(srcId, out var sc) ? sc.Name : srcId, ReleaseName);

            // 3. If another known source still has the missing files, just let the loop reroute.
            if (HasRemainingSourceForMissingFiles())
            {
                _forceScan = true;
                return;
            }

            // 4. Search connected sites for an alternate source.
            if (SourceSearch == null) return;
            var exclude = new HashSet<string>(StringComparer.Ordinal) { srcId };
            lock (_ownershipLock) foreach (var s in _sourceMigratedAway) exclude.Add(s);
            // Don't search the destinations as sources.
            foreach (var id in sitePaths.Keys)
                if (!_sourceServersField.Contains(id)) exclude.Add(id);

            var alt = await SourceSearch(ReleaseName, exclude, ct);
            if (alt is not { } a)
            {
                Log.Information("Spread: no alternate source found for {Release} — affected dests will time out", ReleaseName);
                return;
            }

            // 5. Splice the alternate source into the race: add its path + source set.
            lock (_ownershipLock)
            {
                sitePaths[a.serverId] = a.path;
                _sourceServersField.Add(a.serverId);
            }
            Log.Information("Spread: alternate source {Src} found at {Path} — resuming {Release}",
                _serverConfigs.TryGetValue(a.serverId, out var sc2) ? sc2.Name : a.serverId, a.path, ReleaseName);
            _forceScan = true; // next scan populates ownership from the new source
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread: source-migration handling failed for {Release}", ReleaseName);
        }
        finally
        {
            Interlocked.Exchange(ref _failoverInFlight, 0);
        }
    }

    /// <summary>DirectoryExists probe of a source's release dir (confirming re-probe).</summary>
    private async Task<bool> SourceStillHasRelease(string serverId, string path, CancellationToken ct)
    {
        FtpConnectionPool? pool = null;
        if (_mainPools.TryGetValue(serverId, out var mainPool)) pool = mainPool;
        else if (_pools.TryGetValue(serverId, out var spreadPool)) pool = spreadPool;
        if (pool == null) return true; // can't probe — assume present (don't purge blindly)
        try
        {
            using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            borrowCts.CancelAfter(TimeSpan.FromSeconds(15));
            await using var conn = await pool.Borrow(borrowCts.Token);
            return await conn.Client.DirectoryExists(path, ct);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread: source re-probe failed for {Server} {Path} — assuming present", serverId, path);
            return true; // probe failure is not proof of migration
        }
    }

    /// <summary>True if some non-migrated source still owns at least one file the
    /// destinations are still missing.</summary>
    private bool HasRemainingSourceForMissingFiles()
    {
        lock (_ownershipLock)
        {
            foreach (var (_, owners) in _fileOwnership)
            {
                foreach (var o in owners)
                {
                    if (_sourceMigratedAway.Contains(o)) continue;
                    if (_sourceServersField.Contains(o)) return true;
                }
            }
            return false;
        }
    }
```

> The running `RunAsync` loop closes over its local `sourceServers`. Because failover mutates `_sourceServersField` and `sitePaths` (the same dictionary instance passed into the loop and `ScanSites`), the next scan picks up the new source automatically. `FindBestTransfer` selects sources from `_fileOwnership`, which the next scan repopulates from the new source's path — so no change to `FindBestTransfer` is required. The migrated source is excluded because its ownership was purged and it won't be re-added (its path was removed from `sitePaths`? — NO: leave the dead source's `sitePaths` entry; the next scan finds the dir gone and adds nothing). Confirm at runtime that the dead source contributes no files after purge.

- [ ] **Step 5: Wire `job.SourceSearch` in SpreadManager**

In `SpreadManager.StartRaceInternal` (`SpreadManager.cs:295`), after the existing `job.LivePoolResolver = ...` assignment (line 327), add:

```csharp
        job.SourceSearch = async (release, exclude, ct) =>
        {
            var r = await SearchReleaseOnServers(release, exclude, ct);
            return r == null ? null : (r.ServerId, r.RemotePath, r.Category);
        };
```

- [ ] **Step 6: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 7: Run full test suite**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/GlDrive/Spread/SpreadJob.cs src/GlDrive/Spread/SpreadManager.cs
git commit -m "feat(spread): reactive+confirm alternate-source failover on source migration"
```

---

## Task 9: Record destination completion state in history

**Files:**
- Modify: `src/GlDrive/Spread/RaceHistoryStore.cs` (`RaceHistoryItem`, ~line 9–40)
- Modify: `src/GlDrive/Spread/SpreadJob.cs` (`EmitRaceOutcome` / wherever `RaceHistoryItem` is built)

- [ ] **Step 1: Add the field to the history record**

In `RaceHistoryStore.cs`, in the `RaceHistoryItem` class, add:

```csharp
    public string DestinationState { get; set; } = "";
```

- [ ] **Step 2: Populate it when recording the race**

Find where `RaceHistoryItem` is constructed for this job (grep `new RaceHistoryItem` in `SpreadJob.cs` / `SpreadManager.cs`). At that construction site, set:

```csharp
            DestinationState = DestinationStateSummary(),
```

> If the history item is built in `SpreadManager` (not `SpreadJob`), expose the summary via the existing job reference — `DestinationStateSummary()` is `internal` on `SpreadJob`, callable from `SpreadManager` in the same assembly. Use `job.DestinationStateSummary()` there.

- [ ] **Step 3: Build + test**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly && dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: build succeeds, tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/GlDrive/Spread/RaceHistoryStore.cs src/GlDrive/Spread/SpreadJob.cs
git commit -m "feat(spread): record per-dest completion state in race history"
```

---

## Task 10: Final verification, version bump, release

**Files:**
- Modify: `src/GlDrive/GlDrive.csproj` (`<Version>`)

- [ ] **Step 1: Full build + test**

Run: `dotnet build src/GlDrive/GlDrive.csproj -clp:ErrorsOnly && dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: `Build succeeded. 0 Error(s)`; all tests PASS.

- [ ] **Step 2: Manual runtime verification (per CLAUDE.md)**

Run the app (`dotnet run --project src/GlDrive/GlDrive.csproj`) and confirm via `~/AppData/Roaming/GlDrive/logs/gldrive-{date}.log`:
- A race that delivers all files then logs the await phase and only completes when the marker/heuristic is satisfied (grep `awaiting completion` / `timed out awaiting`).
- Scan cadence drops to ~30s during await (timestamps between `Spread scan starting` lines).
- If a source is removed mid-race, grep for `migrated the release away` → `alternate source ... found` (or `no alternate source found`).
- No premature "completed (partial)" while a dest is still legitimately within its wait window.

- [ ] **Step 3: Bump version**

In `src/GlDrive/GlDrive.csproj`, change `<Version>3.7.2</Version>` to `<Version>3.8.0</Version>`.

- [ ] **Step 4: Pre-commit check (OneDrive hazard — MANDATORY)**

Run: `git status --short | grep -v "^??"`
Confirm ONLY intended files show `M`. If any unexpected `D ` entries appear, do NOT commit — `git reset --mixed HEAD` and re-stage by name.

- [ ] **Step 5: Commit + push + release**

```bash
git add src/GlDrive/GlDrive.csproj
git commit -m "feat(spread): zipscript-aware completion + alternate-source failover (v3.8.0)"
git push
powershell -File installer/release.ps1
```

- [ ] **Step 6: Update memory**

Add a `project_*.md` memory note + `MEMORY.md` pointer summarizing the completion-lifecycle change and the SFV parse-once fix.

---

## Self-review notes (author)

- **Spec coverage:** §A→Tasks 2,5,6; §B→Task 6; §C→Task 6 steps 6; §D→Task 6 step 7; §E→Tasks 3,7,8; §F→Tasks 1,9; §G→Tasks 5,6,8; §Testing→Tasks 2,3. SFV parse-once (latent blocker for the heuristic) added as Task 4.
- **Type consistency:** `DestState` (CompletionDetector) used across Tasks 2/6/8; `CompletionDetector.{IsCompletionMarker,IsMissingStub,Evaluate,AllTerminal,IsAwaitExpired}` consistent; `MkdFailureClassifier.IsSourceFileMissing` consistent across Tasks 3/8; `SearchReleaseOnServers` / `SourceSearch` signatures consistent across Tasks 7/8.
- **Verify-at-execution flags (engine, not unit-tested):** the `_skiplist.Evaluate` overload in Task 7 Step 2; the `ExecuteTransfer` signature change + sole call site in Task 8 Step 3; the `RaceHistoryItem` construction site in Task 9 Step 2; the `_servers`/`GetServer` accessor in Task 7 Step 3. Each step says to confirm against the live file before editing.
