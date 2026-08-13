# Control API Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the live config-override telemetry leak, then restructure `ControlApi` from a single 320-line `switch` into a router with per-domain endpoint modules, guarded by reflection tests that make the secret boundary structural.

**Architecture:** `ControlApi` keeps listener lifecycle, loopback check, token compare, JSON serialization and the error envelope. Routing moves to a `RouteTable` mapping `(method, pattern)` to handlers, with `{param}` segment capture. Each domain implements `IControlEndpoint.Register(RouteTable)`. This plan moves the five existing handlers onto that structure with **no behaviour change** — the existing `ControlApiSecurityTests` must pass unmodified, which is the proof the audited security code was not weakened.

**Tech Stack:** .NET 10 (`net10.0-windows`), C#, WPF, `System.Net.HttpListener`, `System.Text.Json`, xUnit.

**Source spec:** `docs/superpowers/specs/2026-08-13-controlapi-buildout-design.md` (§3, §5.4, §6, §7, §10 steps 1-3)

**Plan 1 of 4.** Follow-on plans cover: read endpoints + cursor store (2), action endpoints (3), config CRUD (4).

## Global Constraints

- Target framework `net10.0-windows`, win-x64. Build via `dotnet build src/GlDrive/GlDrive.csproj` — **never** the `.sln`, which has no project references.
- **MANDATORY before every commit:** run `git status --short | grep -v "^??"` and confirm ONLY intentionally-modified files show `M`. If any `D ` (deleted) entry appears you did not delete, **DO NOT COMMIT** — run `git reset --mixed HEAD` and re-stage by name. OneDrive periodically corrupts git's index view; historical incidents staged 130+ phantom deletions.
- Stage files **by name**. Never `git add -A` or `git add .`.
- Never mutate `AppConfig` outside a validator in `Validators/*`.
- `ControlApiSecurityTests.cs` must pass **unmodified** through Task 4. If a change there seems necessary, stop — it means the refactor altered a security guarantee.
- Full suite must stay green: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`. Baseline at plan start: **660 passing**.
- Do not bump `<Version>` or run `installer/release.ps1` during this plan. Release once at the end, as one version.

---

### Task 1: Stop config-override telemetry recording secret values

`ConfigManager.Save` diffs the previous file against the new one and records raw scalar
values into `%AppData%\GlDrive\ai-data\overrides-{date}.jsonl`. Change an IRC password, a
FiSH channel key, or an API key and the plaintext value is written to disk. This is live
today, independent of the control API, and ships on its own.

**Files:**
- Create: `src/GlDrive/AiAgent/ConfigSecretPointers.cs`
- Modify: `src/GlDrive/Config/ConfigManager.cs:96-115`
- Test: `src/GlDrive.Tests/ConfigSecretPointersTests.cs`

**Interfaces:**
- Consumes: `AiAgent.ConfigDiff.Diff(JsonNode?, JsonNode?, string)` returning `IEnumerable<(string pointer, string? before, string? after)>`
- Produces: `ConfigSecretPointers.IsSecret(string jsonPointer) -> bool` and `ConfigSecretPointers.Mask(string? value) -> string?`

- [ ] **Step 1: Write the failing test**

Create `src/GlDrive.Tests/ConfigSecretPointersTests.cs`:

```csharp
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class ConfigSecretPointersTests
{
    [Theory]
    [InlineData("/servers/0/irc/channels/0/key")]
    [InlineData("/servers/0/connection/password")]
    [InlineData("/controlApi/token")]
    [InlineData("/downloads/omdbApiKey")]
    [InlineData("/downloads/tmdbApiKey")]
    [InlineData("/agent/openRouterApiKey")]
    [InlineData("/servers/0/connection/proxy/password")]
    public void Secret_pointers_are_recognised(string pointer)
        => Assert.True(ConfigSecretPointers.IsSecret(pointer));

    [Theory]
    [InlineData("/servers/0/spreadSite/sections/x265")]
    [InlineData("/servers/0/pool/loginCap")]
    [InlineData("/spread/maxConcurrentRaces")]
    [InlineData("/logging/level")]
    [InlineData("/servers/0/irc/channels/0/name")]
    [InlineData("")]
    public void Ordinary_pointers_are_not_secret(string pointer)
        => Assert.False(ConfigSecretPointers.IsSecret(pointer));

    [Fact]
    public void Mask_is_stable_and_reveals_nothing()
    {
        var a = ConfigSecretPointers.Mask("hunter2");
        var b = ConfigSecretPointers.Mask("hunter2");

        Assert.Equal(a, b);                              // stable: change detection still works
        Assert.StartsWith("sha256:", a);
        Assert.DoesNotContain("hunter2", a!);
        Assert.NotEqual(a, ConfigSecretPointers.Mask("hunter3"));
    }

    [Fact]
    public void Mask_preserves_null_so_added_and_removed_stay_distinguishable()
        => Assert.Null(ConfigSecretPointers.Mask(null));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ConfigSecretPointersTests"`
Expected: FAIL — build error, `ConfigSecretPointers` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/GlDrive/AiAgent/ConfigSecretPointers.cs`:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace GlDrive.AiAgent;

/// <summary>
/// Decides whether a JSON pointer into appsettings.json addresses a secret.
///
/// ConfigManager.Save diffs the old and new config and records every changed scalar to
/// ai-data/overrides-{date}.jsonl. Without this, changing a password, a FiSH channel key
/// or an API key wrote the plaintext value to disk.
///
/// The rule keys on the LEAF NAME's suffix rather than a list of pointers observed to
/// carry secrets: config grows, and an enumeration of known cases silently fails to cover
/// the field somebody adds next month.
/// </summary>
public static class ConfigSecretPointers
{
    private static readonly string[] SecretLeafSuffixes =
        ["password", "passphrase", "token", "secret", "apikey", "key"];

    /// <summary>True when the pointer's last segment names a credential.</summary>
    public static bool IsSecret(string? jsonPointer)
    {
        if (string.IsNullOrEmpty(jsonPointer)) return false;

        var lastSlash = jsonPointer.LastIndexOf('/');
        var leaf = lastSlash >= 0 ? jsonPointer[(lastSlash + 1)..] : jsonPointer;
        if (leaf.Length == 0) return false;

        foreach (var suffix in SecretLeafSuffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Replaces a value with a stable digest. Stable so the agent can still see THAT a
    /// field changed; one-way so it cannot see to what. Null passes through, keeping
    /// "added" and "removed" distinguishable from "changed".
    /// </summary>
    public static string? Mask(string? value)
    {
        if (value == null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ConfigSecretPointersTests"`
Expected: PASS — 15 tests.

- [ ] **Step 5: Apply it at the recording site**

In `src/GlDrive/Config/ConfigManager.cs`, replace the `foreach` inside `Save`:

```csharp
                    foreach (var (ptr, b, a) in AiAgent.ConfigDiff.Diff(beforeNode, afterNode))
                        recorder.Record(AiAgent.TelemetryStream.Overrides,
                            new AiAgent.ConfigOverrideEvent
                            {
                                JsonPointer = ptr,
                                BeforeValue = b,
                                AfterValue  = a
                            });
```

with:

```csharp
                    foreach (var (ptr, b, a) in AiAgent.ConfigDiff.Diff(beforeNode, afterNode))
                    {
                        // Never write a credential's value to the telemetry stream — the
                        // digest still proves the field changed. See ConfigSecretPointers.
                        var secret = AiAgent.ConfigSecretPointers.IsSecret(ptr);
                        recorder.Record(AiAgent.TelemetryStream.Overrides,
                            new AiAgent.ConfigOverrideEvent
                            {
                                JsonPointer = ptr,
                                BeforeValue = secret ? AiAgent.ConfigSecretPointers.Mask(b) : b,
                                AfterValue  = secret ? AiAgent.ConfigSecretPointers.Mask(a) : a
                            });
                    }
```

- [ ] **Step 6: Add the call-site regression test**

Append to `src/GlDrive.Tests/ConfigSecretPointersTests.cs`, inside the class:

```csharp
    [Fact]
    public void ConfigManager_masks_secret_values_before_recording_them()
    {
        var src = ReadSource("src/GlDrive/Config/ConfigManager.cs");

        Assert.Contains("ConfigSecretPointers.IsSecret(ptr)", src, System.StringComparison.Ordinal);
        Assert.Contains("ConfigSecretPointers.Mask(b)", src, System.StringComparison.Ordinal);
        Assert.Contains("ConfigSecretPointers.Mask(a)", src, System.StringComparison.Ordinal);

        // The unguarded assignment must be gone, not merely shadowed.
        Assert.DoesNotContain("BeforeValue = b,", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AfterValue  = a", src, System.StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var dir = System.AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = System.IO.Path.Combine(
                dir, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.InvalidOperationException($"Could not locate {relativePath}");
    }
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: PASS, 676 tests (660 baseline + 16).

- [ ] **Step 8: Commit**

```bash
git status --short | grep -v "^??"     # MANDATORY: confirm no phantom D entries
git add src/GlDrive/AiAgent/ConfigSecretPointers.cs \
        src/GlDrive/Config/ConfigManager.cs \
        src/GlDrive.Tests/ConfigSecretPointersTests.cs
git commit -m "Stop config-override telemetry recording secret values

ConfigManager.Save diffed old vs new config and wrote every changed scalar to
ai-data/overrides-{date}.jsonl, so changing an IRC password, a FiSH channel key
or an API key put the plaintext value on disk.

Secret pointers now record a stable SHA-256 digest instead: the agent can still
see THAT a field changed, not what to. Keys on the leaf name's suffix rather
than a list of known pointers, so a config field added later is covered."
```

---

### Task 2: RouteTable

Pattern matching with `{param}` capture. Pure logic, no HTTP — testable on its own.

**Files:**
- Create: `src/GlDrive/Services/Control/RouteTable.cs`
- Test: `src/GlDrive.Tests/RouteTableTests.cs`

**Interfaces:**
- Produces:
  - `RouteTable.Map(string method, string pattern, Func<ControlRequest, Task> handler)` — `pattern` uses `{name}` for a captured segment
  - `RouteTable.TryMatch(string method, string path, out Func<ControlRequest, Task> handler, out IReadOnlyDictionary<string,string> parameters) -> bool`
  - `RouteTable.MethodNotAllowed(string path) -> bool` — true when the path matches some route under a different verb
  - `RouteTable.Routes -> IReadOnlyList<(string Method, string Pattern)>` — used by `GET /` in Plan 2

- [ ] **Step 1: Write the failing test**

Create `src/GlDrive.Tests/RouteTableTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

public class RouteTableTests
{
    private static Func<ControlRequest, Task> Noop => _ => Task.CompletedTask;

    [Fact]
    public void Matches_a_literal_route()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);

        Assert.True(t.TryMatch("GET", "/status", out var handler, out var ps));
        Assert.NotNull(handler);
        Assert.Empty(ps);
    }

    [Fact]
    public void Captures_a_parameter_segment()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.True(t.TryMatch("GET", "/irc/zephyr", out _, out var ps));
        Assert.Equal("zephyr", ps["serverId"]);
    }

    [Fact]
    public void Captures_multiple_parameters()
    {
        var t = new RouteTable();
        t.Map("POST", "/downloads/{id}/{action}", Noop);

        Assert.True(t.TryMatch("POST", "/downloads/abc123/retry", out _, out var ps));
        Assert.Equal("abc123", ps["id"]);
        Assert.Equal("retry", ps["action"]);
    }

    [Fact]
    public void A_literal_route_wins_over_a_parameter_route()
    {
        var t = new RouteTable();
        t.Map("GET", "/races/{id}", _ => Task.FromResult("param") as Task ?? Task.CompletedTask);
        t.Map("GET", "/races/active", Noop);

        Assert.True(t.TryMatch("GET", "/races/active", out _, out var ps));
        Assert.Empty(ps);   // matched the literal, captured nothing
    }

    [Fact]
    public void Segment_count_must_match_exactly()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.False(t.TryMatch("GET", "/irc", out _, out _));
        Assert.False(t.TryMatch("GET", "/irc/zephyr/messages", out _, out _));
    }

    [Fact]
    public void Method_is_part_of_the_match()
    {
        var t = new RouteTable();
        t.Map("GET", "/races", Noop);

        Assert.False(t.TryMatch("POST", "/races", out _, out _));
        Assert.True(t.MethodNotAllowed("/races"));
        Assert.False(t.MethodNotAllowed("/nonexistent"));
    }

    [Fact]
    public void Matching_is_case_insensitive_on_method_and_literal_segments()
    {
        var t = new RouteTable();
        t.Map("GET", "/Status", Noop);

        Assert.True(t.TryMatch("get", "/status", out _, out _));
    }

    [Fact]
    public void A_parameter_never_matches_an_empty_segment()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.False(t.TryMatch("GET", "/irc/", out _, out _));
    }

    [Fact]
    public void Routes_are_enumerable_for_the_index_endpoint()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);
        t.Map("POST", "/races", Noop);

        Assert.Contains(("GET", "/status"), t.Routes);
        Assert.Contains(("POST", "/races"), t.Routes);
    }

    [Fact]
    public void Duplicate_registration_is_rejected_loudly()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);

        Assert.Throws<InvalidOperationException>(() => t.Map("GET", "/status", Noop));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~RouteTableTests"`
Expected: FAIL — `RouteTable` and `ControlRequest` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/GlDrive/Services/Control/RouteTable.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlDrive.Services.Control;

/// <summary>
/// Maps (method, path pattern) to a handler. Patterns use {name} for a captured segment:
/// "/irc/{serverId}/messages". Literal segments beat parameter segments, so "/races/active"
/// is reachable even when "/races/{id}" is registered.
///
/// Deliberately tiny: the surface is loopback-only and every route is registered in-process,
/// so there is nothing to gain from a general routing engine.
/// </summary>
public sealed class RouteTable
{
    private sealed record Route(string Method, string Pattern, string[] Segments,
                                Func<ControlRequest, Task> Handler, int LiteralCount);

    private readonly List<Route> _routes = [];

    public IReadOnlyList<(string Method, string Pattern)> Routes =>
        _routes.Select(r => (r.Method, r.Pattern)).ToList();

    public void Map(string method, string pattern, Func<ControlRequest, Task> handler)
    {
        var normalisedMethod = method.ToUpperInvariant();
        if (_routes.Any(r => r.Method == normalisedMethod
                             && string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Duplicate route: {normalisedMethod} {pattern}");

        var segments = Split(pattern);
        var literals = segments.Count(s => !IsParameter(s));
        _routes.Add(new Route(normalisedMethod, pattern, segments, handler, literals));
    }

    public bool TryMatch(string method, string path,
        out Func<ControlRequest, Task>? handler,
        out IReadOnlyDictionary<string, string> parameters)
    {
        handler = null;
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        var pathSegments = Split(path);
        var normalisedMethod = method.ToUpperInvariant();

        // Most literal segments first, so "/races/active" beats "/races/{id}".
        foreach (var route in _routes.Where(r => r.Method == normalisedMethod)
                                     .OrderByDescending(r => r.LiteralCount))
        {
            if (route.Segments.Length != pathSegments.Length) continue;
            if (!TryBind(route.Segments, pathSegments, out var bound)) continue;

            handler = route.Handler;
            parameters = bound;
            return true;
        }

        return false;
    }

    /// <summary>True when the path matches a registered route under some other verb.</summary>
    public bool MethodNotAllowed(string path)
    {
        var pathSegments = Split(path);
        return _routes.Any(r => r.Segments.Length == pathSegments.Length
                                && TryBind(r.Segments, pathSegments, out _));
    }

    private static bool TryBind(string[] pattern, string[] actual,
        out Dictionary<string, string> bound)
    {
        bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (IsParameter(pattern[i]))
            {
                if (actual[i].Length == 0) return false;
                bound[pattern[i][1..^1]] = actual[i];
                continue;
            }
            if (!string.Equals(pattern[i], actual[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsParameter(string segment) =>
        segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}';

    /// <summary>
    /// Splits on '/' keeping empty inner segments, so "/irc/" is two segments and cannot
    /// bind a parameter — a trailing slash must not smuggle an empty id through.
    /// </summary>
    private static string[] Split(string path)
    {
        var trimmed = path.StartsWith('/') ? path[1..] : path;
        return trimmed.Length == 0 ? [] : trimmed.Split('/');
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~RouteTableTests"`
Expected: PASS — 10 tests. (Requires `ControlRequest` from Task 3 to compile; if you are executing tasks strictly in order, create the empty `ControlRequest` shell from Task 3 Step 3 first, then return here.)

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Services/Control/RouteTable.cs src/GlDrive.Tests/RouteTableTests.cs
git commit -m "Add RouteTable for the control API

Maps (method, pattern) to handlers with {param} segment capture. Literal
segments outrank parameter segments so a fixed sub-path stays reachable
alongside an id route. Trailing slash cannot bind an empty parameter."
```

---

### Task 3: ControlRequest

The per-request context handed to every handler. Wraps `HttpListenerContext` so endpoints
never touch it directly, and centralises the JSON response and error envelope.

**Files:**
- Create: `src/GlDrive/Services/Control/ControlRequest.cs`
- Test: `src/GlDrive.Tests/ControlRequestTests.cs`

**Interfaces:**
- Consumes: `RouteTable` parameter dictionary
- Produces:
  - `ControlRequest.Param(string name) -> string?`
  - `ControlRequest.Query(string name) -> string?`
  - `ControlRequest.QueryInt(string name, int fallback, int min, int max) -> int`
  - `ControlRequest.ReadBodyAsync() -> Task<string>`
  - `ControlRequest.RespondAsync(int status, object payload) -> Task`
  - `ControlRequest.ErrorAsync(int status, string code, string error, string? detail = null) -> Task`

- [ ] **Step 1: Write the failing test**

Create `src/GlDrive.Tests/ControlRequestTests.cs`. These cover the pure parsing helpers;
`HttpListenerContext` cannot be constructed in a unit test, so response writing is covered
by the endpoint tests in Plan 2.

```csharp
using System.Collections.Generic;
using System.Collections.Specialized;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

public class ControlRequestTests
{
    private static ControlRequest Make(
        IReadOnlyDictionary<string, string>? parameters = null,
        NameValueCollection? query = null)
        => ControlRequest.ForTesting(
            parameters ?? new Dictionary<string, string>(),
            query ?? new NameValueCollection());

    [Fact]
    public void Param_returns_a_captured_segment()
    {
        var r = Make(new Dictionary<string, string> { ["serverId"] = "zephyr" });
        Assert.Equal("zephyr", r.Param("serverId"));
    }

    [Fact]
    public void Param_returns_null_when_absent()
        => Assert.Null(Make().Param("nope"));

    [Fact]
    public void QueryInt_clamps_into_range()
    {
        var q = new NameValueCollection { { "limit", "9999" } };
        Assert.Equal(500, Make(query: q).QueryInt("limit", fallback: 25, min: 1, max: 500));
    }

    [Fact]
    public void QueryInt_falls_back_when_missing_or_unparseable()
    {
        Assert.Equal(25, Make().QueryInt("limit", 25, 1, 500));

        var q = new NameValueCollection { { "limit", "banana" } };
        Assert.Equal(25, Make(query: q).QueryInt("limit", 25, 1, 500));
    }

    [Fact]
    public void QueryInt_clamps_a_negative_up_to_min()
    {
        var q = new NameValueCollection { { "since", "-5" } };
        Assert.Equal(0, Make(query: q).QueryInt("since", 0, 0, int.MaxValue));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ControlRequestTests"`
Expected: FAIL — `ControlRequest` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/GlDrive/Services/Control/ControlRequest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GlDrive.Services.Control;

/// <summary>
/// One control-API request. Endpoints receive this and never see HttpListenerContext, so
/// the response shape and error envelope stay in one place.
/// </summary>
public sealed class ControlRequest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpListenerContext? _ctx;
    private readonly IReadOnlyDictionary<string, string> _parameters;
    private readonly NameValueCollection _query;

    public string Path { get; }

    private ControlRequest(HttpListenerContext? ctx, string path,
        IReadOnlyDictionary<string, string> parameters, NameValueCollection query)
    {
        _ctx = ctx;
        Path = path;
        _parameters = parameters;
        _query = query;
    }

    public static ControlRequest FromContext(HttpListenerContext ctx, string path,
        IReadOnlyDictionary<string, string> parameters)
        => new(ctx, path, parameters, ctx.Request.QueryString);

    /// <summary>Parsing-only instance for unit tests; responding on it throws.</summary>
    public static ControlRequest ForTesting(
        IReadOnlyDictionary<string, string> parameters, NameValueCollection query)
        => new(null, "/test", parameters, query);

    public string? Param(string name) => _parameters.TryGetValue(name, out var v) ? v : null;

    public string? Query(string name) => _query[name];

    public int QueryInt(string name, int fallback, int min, int max)
        => int.TryParse(_query[name], out var n) ? Math.Clamp(n, min, max) : fallback;

    public async Task<string> ReadBodyAsync()
    {
        if (_ctx == null) return "";
        using var reader = new StreamReader(_ctx.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public async Task RespondAsync(int status, object payload)
    {
        if (_ctx == null) throw new InvalidOperationException("No context — test instance");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        _ctx.Response.StatusCode = status;
        _ctx.Response.ContentType = "application/json";
        _ctx.Response.ContentLength64 = bytes.Length;
        await _ctx.Response.OutputStream.WriteAsync(bytes);
        _ctx.Response.Close();
    }

    public Task ErrorAsync(int status, string code, string error, string? detail = null)
        => RespondAsync(status, new { error, code, detail, path = Path });
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ControlRequestTests"`
Expected: PASS — 5 tests.

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Services/Control/ControlRequest.cs src/GlDrive.Tests/ControlRequestTests.cs
git commit -m "Add ControlRequest as the control API's per-request context

Endpoints get parameters, query helpers, body reading and a single response
and error envelope, and never touch HttpListenerContext directly."
```

---

### Task 4: Move the five existing handlers onto the router

Pure refactor. Behaviour must not change, and `ControlApiSecurityTests.cs` must pass
untouched — that is the proof the audited security path is intact.

**Files:**
- Create: `src/GlDrive/Services/Control/IControlEndpoint.cs`
- Create: `src/GlDrive/Services/Control/Endpoints/StatusEndpoints.cs`
- Create: `src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs`
- Modify: `src/GlDrive/Services/ControlApi.cs` — replace the `switch` with router dispatch
- Test: `src/GlDrive.Tests/ControlApiSecurityTests.cs` — **must not be modified**

**Interfaces:**
- Consumes: `RouteTable`, `ControlRequest` from Tasks 2-3
- Produces: `IControlEndpoint.Register(RouteTable routes)`

- [ ] **Step 1: Create the endpoint interface**

Create `src/GlDrive/Services/Control/IControlEndpoint.cs`:

```csharp
namespace GlDrive.Services.Control;

/// <summary>
/// A group of related routes. Implementations receive their dependencies as constructor
/// arguments and register their routes on the shared table.
///
/// Implementations must NOT take ServerManager, AppConfig, IrcService, MountService or
/// SpreadManager — see ControlApiBoundaryTests. They take reader facades instead, so an
/// endpoint cannot reach a live credential even by accident.
/// </summary>
public interface IControlEndpoint
{
    void Register(RouteTable routes);
}
```

- [ ] **Step 2: Move the status and sections handlers**

Create `src/GlDrive/Services/Control/Endpoints/StatusEndpoints.cs`. Move the bodies of
`Status()` and `Sections()` from `ControlApi.cs` verbatim — including the `/sections`
keys-only projection added in v3.10.58:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Services.Control.Endpoints;

public sealed class StatusEndpoints : IControlEndpoint
{
    private readonly AppConfig _config;
    private readonly Func<SpreadManager?> _getSpread;
    private readonly Func<IReadOnlyList<string>> _getConnectedServerIds;
    private readonly RouteTable _routes;

    public StatusEndpoints(AppConfig config, Func<SpreadManager?> getSpread,
        Func<IReadOnlyList<string>> getConnectedServerIds, RouteTable routes)
    {
        _config = config;
        _getSpread = getSpread;
        _getConnectedServerIds = getConnectedServerIds;
        _routes = routes;
    }

    public void Register(RouteTable routes)
    {
        routes.Map("GET", "/", r => r.RespondAsync(200, Index()));
        routes.Map("GET", "/status", r => r.RespondAsync(200, Status()));
        routes.Map("GET", "/sections", r => r.RespondAsync(200, Sections()));
    }

    private object Index() => new
    {
        version = typeof(ControlApi).Assembly.GetName().Version?.ToString(),
        routes = _routes.Routes.Select(r => $"{r.Method} {r.Pattern}").OrderBy(s => s)
    };

    private object Status()
    {
        var spread = _getSpread();
        var connected = _getConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        return new
        {
            version = typeof(ControlApi).Assembly.GetName().Version?.ToString(),
            activeRaces = spread?.ActiveJobs.Count ?? 0,
            maxConcurrentRaces = _config.Spread.MaxConcurrentRaces,
            servers = _config.Servers.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                connected = connected.Contains(s.Id),
                loginCap = s.Pool.LoginCap,
                loginHeadroom = s.Pool.LoginHeadroom,
                uploadSlots = s.SpreadSite.MaxUploadSlots,
                downloadSlots = s.SpreadSite.MaxDownloadSlots,
                sections = s.SpreadSite.Sections.Count
            })
        };
    }

    private object Sections()
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _config.Servers)
            foreach (var k in s.SpreadSite.Sections.Keys) keys.Add(k);

        // Section KEYS only — the values are each site's real remote paths. See v3.10.58.
        return new
        {
            sections = keys,
            perServer = _config.Servers.ToDictionary(
                s => s.Name,
                s => (IEnumerable<string>)s.SpreadSite.Sections.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
        };
    }
}
```

- [ ] **Step 3: Move the race handlers**

Create `src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs`, moving `Races()`,
`HistoryList()`, `StartRace()` and the two path-prefix handlers. The `{id}` segments that
were parsed by hand with `path["/races/".Length..]` now come from the router:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GlDrive.Config;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services.Control.Endpoints;

public sealed class SpreadEndpoints : IControlEndpoint
{
    private readonly AppConfig _config;
    private readonly Func<SpreadManager?> _getSpread;

    public SpreadEndpoints(AppConfig config, Func<SpreadManager?> getSpread)
    {
        _config = config;
        _getSpread = getSpread;
    }

    public void Register(RouteTable routes)
    {
        routes.Map("GET", "/races", r => r.RespondAsync(200, Races()));
        routes.Map("GET", "/history", r => r.RespondAsync(200, History(r)));
        routes.Map("GET", "/races/{id}", RaceDetail);
        routes.Map("POST", "/races", StartRace);
        routes.Map("POST", "/races/{id}/stop", StopRace);
    }

    private object Races()
    {
        var spread = _getSpread();
        if (spread == null) return new { races = Array.Empty<object>() };
        return new
        {
            races = spread.ActiveJobs.Select(j => new
            {
                id = j.Id,
                release = j.ReleaseName,
                section = j.Section,
                state = j.State.ToString(),
                score = j.Score,
                startedAt = j.StartedAt,
                isAutoRace = j.IsAutoRace,
                sites = j.Sites.Values.Select(s => new { s.ServerName, s.FilesOwned, s.FilesTotal, s.IsSource })
            })
        };
    }

    private object History(ControlRequest r)
    {
        var limit = r.QueryInt("limit", fallback: 25, min: 1, max: 500);
        var spread = _getSpread();
        if (spread == null) return new { history = Array.Empty<object>() };
        return new { history = spread.History.Items.Take(limit) };
    }

    private Task RaceDetail(ControlRequest r)
    {
        var id = r.Param("id");
        var job = _getSpread()?.ActiveJobs.FirstOrDefault(j => j.Id == id);
        return job == null
            ? r.ErrorAsync(404, "not_found", "no such active race", id)
            : r.RespondAsync(200, job.GetDetail());
    }

    private Task StopRace(ControlRequest r)
    {
        var spread = _getSpread();
        if (spread == null)
            return r.ErrorAsync(503, "unavailable", "spread engine unavailable");

        var id = r.Param("id")!;
        spread.StopJob(id);
        return r.RespondAsync(200, new { stopped = id });
    }

    private async Task StartRace(ControlRequest r)
    {
        var body = await r.ReadBodyAsync();

        string? section, release;
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            section = doc.RootElement.TryGetProperty("section", out var s) ? s.GetString() : null;
            release = doc.RootElement.TryGetProperty("release", out var rel) ? rel.GetString() : null;
        }
        catch (JsonException ex)
        {
            await r.ErrorAsync(400, "bad_request", "invalid JSON body", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(release))
        {
            await r.ErrorAsync(400, "bad_request", "both 'section' and 'release' are required");
            return;
        }

        var spread = _getSpread();
        if (spread == null)
        {
            await r.ErrorAsync(503, "unavailable", "spread engine unavailable");
            return;
        }

        var connected = spread.GetConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connected.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id).ToList();

        if (serverIds.Count < 2)
        {
            await r.ErrorAsync(409, "conflict", "need 2+ connected servers with sections configured",
                string.Join(", ", _config.Servers.Where(s => connected.Contains(s.Id)).Select(s => s.Name)));
            return;
        }

        try
        {
            var job = spread.StartRace(section!, release!, serverIds, SpreadMode.Race);
            if (job == null)
            {
                await r.ErrorAsync(409, "conflict", "race not started (queued or rejected)");
                return;
            }
            Log.Information("Control API started race {Id}: [{Section}] {Release}", job.Id, section, release);
            await r.RespondAsync(202, new { id = job.Id, release = job.ReleaseName, section = job.Section });
        }
        catch (Exception ex)
        {
            await r.ErrorAsync(500, "internal", ex.Message);
        }
    }
}
```

- [ ] **Step 4: Replace the switch in ControlApi**

In `src/GlDrive/Services/ControlApi.cs`:

1. Add `using GlDrive.Services.Control;` and `using GlDrive.Services.Control.Endpoints;`
2. Add a field `private readonly RouteTable _routes = new();`
3. At the end of the constructor, register the endpoints:

```csharp
        // Endpoints receive only what they need. See ControlApiBoundaryTests for why they
        // must never take ServerManager/AppConfig-reaching handles once readers land.
        new StatusEndpoints(_config, _getSpread, _getConnectedServerIds, _routes).Register(_routes);
        new SpreadEndpoints(_config, _getSpread).Register(_routes);
```

4. Replace everything in `Handle` from `var path = ...` through the final
   `await Respond(ctx, 404, ...)` with:

```csharp
            var path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            var method = ctx.Request.HttpMethod.ToUpperInvariant();

            if (_routes.TryMatch(method, path, out var handler, out var parameters))
            {
                await handler!(ControlRequest.FromContext(ctx, path, parameters));
                return;
            }

            if (_routes.MethodNotAllowed(path))
            {
                await Respond(ctx, 405, new { error = "method not allowed", code = "method_not_allowed", path });
                return;
            }

            await Respond(ctx, 404, new { error = "not found", code = "not_found", path });
```

5. Delete the now-unused private methods `Status()`, `Sections()`, `Races()`,
   `HistoryList()` and `StartRace()`. Keep `Respond`, `FixedTimeEquals`, `EnsureToken`,
   `Start`, `AcceptLoop`, `Dispose` and the loopback/auth block **unchanged**.

- [ ] **Step 5: Run the security tests unmodified**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ControlApiSecurityTests"`
Expected: PASS with **zero edits** to that file. If it fails, the refactor changed a security
guarantee — fix the refactor, not the test.

- [ ] **Step 6: Run the full suite and build**

Run: `dotnet build src/GlDrive/GlDrive.csproj && dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: 0 errors, 10 pre-existing warnings; 691 tests pass.

- [ ] **Step 7: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Services/ControlApi.cs \
        src/GlDrive/Services/Control/IControlEndpoint.cs \
        src/GlDrive/Services/Control/Endpoints/StatusEndpoints.cs \
        src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs
git commit -m "Move control API handlers onto the router

Pure refactor: ControlApi keeps listener lifecycle, the loopback check, the
fixed-time token compare and the response envelope; routing moves to RouteTable
and the five handlers move into StatusEndpoints and SpreadEndpoints. Hand-parsed
path prefixes become router parameters, and an unmatched path under a registered
route now answers 405 rather than 404.

ControlApiSecurityTests passes unmodified, which is the check that the audited
security path was not disturbed. Adds GET / as a route index."
```

---

### Task 5: Pin the facade boundary with reflection tests

Spec invariants 5 and 6. Without these, the boundary is a convention that decays the first
time someone needs "just one field" from a manager.

**Files:**
- Create: `src/GlDrive.Tests/ControlApiBoundaryTests.cs`

**Interfaces:**
- Consumes: `IControlEndpoint` implementations from Task 4

- [ ] **Step 1: Write the test**

Create `src/GlDrive.Tests/ControlApiBoundaryTests.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Invariants 5 and 6 of the control API spec. An adversarial pass over the subsystems found
/// 37 secret-bearing members reachable from the handles endpoints would otherwise hold —
/// including a one-hop path from ServerManager to the plaintext glftpd password. These tests
/// make the boundary structural instead of a rule people remember.
/// </summary>
public class ControlApiBoundaryTests
{
    /// <summary>Types an endpoint must never hold: from any of these a secret is reachable.</summary>
    private static readonly string[] ForbiddenTypeNames =
    [
        "ServerManager", "MountService", "IrcService", "SpreadManager",
        "AppConfig", "FtpConnectionPool", "FishKeyStore", "FtpClientFactory"
    ];

    private static IEnumerable<Type> EndpointTypes =>
        typeof(IControlEndpoint).Assembly.GetTypes()
            .Where(t => typeof(IControlEndpoint).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false });

    [Fact]
    public void There_is_at_least_one_endpoint_so_this_test_cannot_pass_vacuously()
        => Assert.NotEmpty(EndpointTypes);

    [Fact]
    public void No_endpoint_holds_a_manager_or_config_field()
    {
        var violations = new List<string>();

        foreach (var type in EndpointTypes)
        {
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (IsForbidden(f.FieldType))
                    violations.Add($"{type.Name}.{f.Name} : {f.FieldType.Name}");

            foreach (var ctor in type.GetConstructors())
                foreach (var p in ctor.GetParameters())
                    if (IsForbidden(p.ParameterType))
                        violations.Add($"{type.Name}.ctor({p.Name} : {p.ParameterType.Name})");
        }

        Assert.True(violations.Count == 0,
            "Endpoints must take reader facades, not live subsystems:\n  " +
            string.Join("\n  ", violations.Distinct()));
    }

    /// <summary>
    /// Unwraps Func&lt;T&gt;, Lazy&lt;T&gt;, nullables and collections — passing a
    /// Func&lt;ServerManager&gt; is exactly as reachable as passing the manager.
    /// </summary>
    private static bool IsForbidden(Type type)
    {
        foreach (var t in Flatten(type))
            if (ForbiddenTypeNames.Contains(t.Name, StringComparer.Ordinal))
                return true;
        return false;
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
            foreach (var t in Flatten(element)) yield return t;

        if (type.IsGenericType)
            foreach (var arg in type.GetGenericArguments())
                foreach (var t in Flatten(arg)) yield return t;
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ControlApiBoundaryTests"`
Expected: **FAIL.** `StatusEndpoints` and `SpreadEndpoints` both take `AppConfig`. This is
correct — the test is telling the truth about Task 4's interim state.

- [ ] **Step 3: Record the known exemption**

The readers that remove `AppConfig` land in Plan 2. Until then, record the exemption
explicitly rather than weakening the test. Add to `ControlApiBoundaryTests`:

```csharp
    /// <summary>
    /// StatusEndpoints and SpreadEndpoints still take AppConfig directly: they predate the
    /// reader facades and Plan 2 replaces that with IConfigReader/ISpreadReader. Listed here
    /// so the exemption is visible and finite — a new endpoint cannot quietly join it.
    /// </summary>
    private static readonly string[] KnownAppConfigHolders = ["StatusEndpoints", "SpreadEndpoints"];

    [Fact]
    public void The_AppConfig_exemption_list_does_not_grow()
    {
        var holders = EndpointTypes
            .Where(t => t.GetConstructors().Any(c => c.GetParameters()
                        .Any(p => Flatten(p.ParameterType).Any(x => x.Name == "AppConfig"))))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(KnownAppConfigHolders.OrderBy(n => n).ToArray(), holders);
    }
```

Then narrow the main test's forbidden list for this plan only by removing `"AppConfig"`
from `ForbiddenTypeNames`, leaving a comment:

```csharp
    private static readonly string[] ForbiddenTypeNames =
    [
        // "AppConfig" is covered by The_AppConfig_exemption_list_does_not_grow until the
        // reader facades land in Plan 2, at which point it moves back into this list.
        "ServerManager", "MountService", "IrcService", "SpreadManager",
        "FtpConnectionPool", "FishKeyStore", "FtpClientFactory"
    ];
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --filter "FullyQualifiedName~ControlApiBoundaryTests"`
Expected: PASS — 3 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: PASS, 694 tests.

- [ ] **Step 6: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive.Tests/ControlApiBoundaryTests.cs
git commit -m "Pin the control API facade boundary with reflection tests

No endpoint may hold ServerManager, MountService, IrcService, SpreadManager or
a pool/keystore/factory, directly or wrapped in Func/Lazy/collection — each is
one hop from a plaintext credential. AppConfig has a finite, named exemption
for the two endpoints that predate the reader facades; the list is asserted not
to grow, so a new endpoint cannot quietly join it."
```

---

### Task 6: Release

**Files:**
- Modify: `src/GlDrive/GlDrive.csproj` — `<Version>`

- [ ] **Step 1: Bump the version**

Read the current `<Version>` in `src/GlDrive/GlDrive.csproj` and increment the patch
component (baseline for this plan is `3.10.58`, so `3.10.59` unless later work landed first).

- [ ] **Step 2: Verify build and full suite**

Run: `dotnet build src/GlDrive/GlDrive.csproj && dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj`
Expected: 0 errors; 694 tests pass.

- [ ] **Step 3: Commit, push, release**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/GlDrive.csproj
git commit -m "Control API foundation: router, endpoint modules, boundary tests (v3.10.59)"
git push
powershell -File installer/release.ps1
```

- [ ] **Step 4: Verify all three version sources agree**

```bash
grep -o "<Version>[^<]*" src/GlDrive/GlDrive.csproj
gh release list --limit 1
cat ~/AppData/Roaming/GlDrive/logs/last-heartbeat.json
```

Expected: csproj, latest GitHub release, and the running process all report the same version.
The running process only updates after the box installs — if it lags, check for
`%AppData%\GlDrive\.update-declined`.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §3.1 layout (router, request, endpoints) | 2, 3, 4 |
| §3.2 facade boundary | 5 (pinned; readers land in Plan 2) |
| §3.4 error envelope | 3 (`ErrorAsync`) |
| §4.1 `GET /`, `/status`, `/sections`, `/races*` | 4 |
| §5.4 `ConfigManager.Save` leak | 1 |
| §6 invariants 1-4 | 4 Step 5 (existing tests, unmodified) |
| §6 invariants 5-6 | 5 |
| §6 invariant 7 (`/logs` traversal) | Plan 2 — no `/logs` route exists yet |
| §7 routing/regression test rows | 2, 4 |
| §10 steps 1-3 | 1, 4, 5 |

Deferred to later plans, by design: §3.3 reader threading, §4.1 remaining reads, §4.2 actions,
§4.3 limits, §4.4 accessors, §5.1-5.3 config CRUD, §7 endpoint/threading/fuzz rows.

**Placeholder scan:** No TBD/TODO. Every code step carries the actual code. Task 5 Step 2
expects a genuine failure and Step 3 resolves it — that is a real state, not a placeholder.

**Type consistency:** `ControlRequest` — `Param`, `Query`, `QueryInt`, `ReadBodyAsync`,
`RespondAsync`, `ErrorAsync`, `FromContext`, `ForTesting`, `Path` — used identically in
Tasks 2-4. `RouteTable` — `Map`, `TryMatch`, `MethodNotAllowed`, `Routes` — consistent.
`ConfigSecretPointers.IsSecret`/`Mask` match between Task 1 Steps 3 and 5.

**Known ordering note:** `RouteTableTests` (Task 2) needs `ControlRequest` (Task 3) to
compile. Flagged in Task 2 Step 4. Executing Task 3 Step 3 first, then Task 2, avoids it.
