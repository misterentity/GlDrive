# Control API build-out — design

**Date:** 2026-08-13
**Status:** approved, ready for implementation planning
**Baseline:** v3.10.58 (`d4f5f04`)

## 1. Motivation

`ControlApi` today exposes five race endpoints and is disabled by default. Everything else
about a running GlDrive — IRC connection state, mount and pool health, download queue,
config, logs — is reachable only through the WPF Dashboard.

The cost of that showed up concretely on 2026-08-12. Diagnosing "zephyr IRC is not
connecting" required reconstructing live state from log archaeology: which channels were
joined had to be inferred from the size of `irc-logs/a8a08694-*.log`, and whether the join
had been abandoned could not be determined at all, because the entire invite/join path
writes only to the in-app system tab. The questions that would have answered it in one
request — *is it connected, which channels is it in, when did it last receive anything, did
it give up joining* — had no way to be asked.

Three of the four root causes found that day were invisible in the log by construction, not
by accident. An introspection surface is the structural fix.

## 2. Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Scope | Full CRUD across all subsystems | User requirement |
| Spec granularity | One spec, whole thing | User preference over phasing |
| Remote access | Loopback-only + user-provided tunnel | Keeps the audited security model intact; phone/web work over WireGuard/Tailscale/SSH with zero new attack surface |
| Config apply | Write to disk, restart to apply | No per-field apply-strategy table to maintain or get wrong |
| Live updates | Cursor polling, no streaming | No long-lived responses inside `HttpListener`; a dropped tunnel just resumes |
| Code structure | Router + per-domain endpoint modules | Preserves audited auth code; keeps files small enough to reason about |

**Rejected:** ASP.NET Core / Kestrel (pulls the hosting stack into a WPF tray app and
discards reviewed security code, for routing sugar a loopback surface barely uses);
extending the existing `switch` in place (a ~2,000-line file mixing auth, routing, DTO
shaping and config writes — the failure shape this codebase keeps hitting); binding the
LAN directly (plaintext HTTP carrying site credentials).

## 3. Architecture

### 3.1 Layout

```
Services/ControlApi/
  ControlApi.cs          listener lifecycle, loopback check, token compare,
                         JSON serialization, error envelope, dispatch
  RouteTable.cs          (method, pattern) -> handler; {param} segment capture
  ControlRequest.cs      parsed request: path params, query, body, responder
  IControlEndpoint.cs    void Register(RouteTable routes)
  Endpoints/             StatusEndpoints, IrcEndpoints, ServerEndpoints,
                         SpreadEndpoints, DownloadEndpoints, ConfigEndpoints,
                         LogEndpoints
  Readers/               IIrcReader, IServerReader, ISpreadReader,
                         IDownloadReader, IConfigReader, IConfigWriter, ILogReader
  Dto/                   immutable snapshot records
  ConfigWriter.cs        validate -> snapshot -> atomic replace
  CursorStore.cs         monotonic cursors for /logs and /irc messages
```

### 3.2 The facade boundary — the load-bearing decision

**`ControlApi` and its endpoint classes never receive `ServerManager`, `MountService`,
`IrcService`, `SpreadManager`, or `AppConfig`.** They receive only the `Readers/`
interfaces, which can return nothing but DTOs.

This is not stylistic. An adversarial mapping pass over the six subsystems found **37
secret-bearing members** reachable from the handles the endpoints would otherwise hold,
including three one-hop paths from `ServerManager`:

| Path | Lands on |
|---|---|
| `GetServer() -> MountService.Pool -> PooledConnection.Client` | FluentFTP `BaseFtpClient.Credentials` — plaintext glftpd password |
| `GetIrcService() -> IrcService.KeyStore -> GetAllKeys()` | Live FiSH keys and DH1080 shared secrets |
| `GetRecentIrcMessages()` / `GetScrollback()` | Decrypted PM bodies, stored DPAPI-encrypted at rest |

Every one is `public` and callable from any handler in the same assembly. An endpoint
written a year from now inherits that reach by default. Moving the boundary into the type
system makes the secret containment structural rather than a rule contributors must
remember — the same lesson as v3.10.55's CPSV desync guard: an invariant about a resource
belongs on the resource, not in each caller's discretion.

Reader implementations live beside their subsystems, where they can see private state and
take the right locks.

### 3.3 Threading

The API is served on `HttpListener` thread-pool threads, never the WPF dispatcher. Readers
must return snapshots taken under whatever lock the owning subsystem uses. Handlers must
never serialize a live collection.

Known hazards found by the mapping pass:

- `IrcService.Channels` returns the **live non-concurrent `Dictionary`**, mutated unlocked
  from the read loop and cleared on every reconnect. Enumerating it off-thread can throw
  `InvalidOperationException`. Requires a copying accessor.
- `_pendingInviteJoins` is mutated from both the read loop and `RetryJoinAfterDelay`
  continuations with no lock. Requires a locked snapshot.
- `IrcScrollbackBuffer.Targets`/`Snapshot`, `FishKeyStore.GetKey`/`GetAllKeys`, and
  `ServerManager`'s `ConcurrentDictionary`-backed getters already copy under lock and are
  safe to call directly.

### 3.4 Error envelope

```json
{ "error": "no such server", "code": "not_found", "detail": "zephyr", "path": "/irc/zephyr" }
```

Status conventions unchanged: `400` malformed, `401` bad token, `403` non-loopback,
`404` unknown route or entity, `409` refused by state, `503` subsystem unavailable,
`500` unhandled.

## 4. Endpoint surface

### 4.1 Reads

| Endpoint | Returns |
|---|---|
| `GET /` | route index, version |
| `GET /status` | version, servers, active race count (existing, unchanged) |
| `GET /sections` | section keys only (fixed in v3.10.58) |
| `GET /irc` | per server: state, nick, joined channels, last inbound UTC |
| `GET /irc/{id}` | + pending invite retries, FiSH key metadata (mode/manual/setAt only) |
| `GET /irc/{id}/messages?target=&since=&includePms=` | scrollback page + next cursor |
| `GET /servers` | mount state, drive letter, pool counters, login-gate limits |
| `GET /servers/{id}` | + connection monitor state, SITE STATS cache |
| `GET /spread` | active races, spread pool health |
| `GET /races` / `GET /races/{id}` | existing; detail via `SpreadJob.GetDetail()` |
| `GET /downloads?since=` | queue, status, progress, speed |
| `GET /wishlist` | wishlist entries |
| `GET /logs?since=&level=` | log page + next cursor |
| `GET /config` | allow-list projection (§5) |
| `GET /config/schema` | field paths, types, validation rules, restart-required flag |

### 4.2 Writes

Each maps to an action the Dashboard already performs:

```
POST /irc/{id}/reconnect
POST /irc/{id}/join      {channel}
POST /irc/{id}/msg       {target, text}
POST /downloads/{id}/retry | /pause | /cancel
POST /servers/{id}/remount
POST /races              {section, release}      (existing)
POST /races/{id}/stop                            (existing)
PUT  /config/{json-pointer}  {value}             (§5)
```

`POST /irc/{id}/msg` returns `409 not_connected` when the client is down, rather than the
silent `return` in `IrcService.SendMessage` that currently makes a PM vanish with no error,
no log line, and no local echo. It is retained deliberately despite being the highest-risk
endpoint (it can speak as the user on a private network); the loopback + tunnel model is
what makes that acceptable.

### 4.3 Documented limits

- **Scrollback caps at 500 entries per target** (`IrcScrollbackBuffer.DefaultMaxPerTarget`).
  `?since=` pages within the ring; it cannot reach further back. Older lines exist only in
  `irc-logs/` and `pm-history-{id}.json`. `/irc/{id}/messages` is a live tail, not an
  archive, and the response says so via a `truncated` flag when the cursor has fallen off
  the ring.
- **PM bodies are opt-in.** `?target=` defaults to channels and the `*` system tab.
  PM targets require `includePms=true`. `GET /irc/{id}` lists channel names but **not** PM
  peer nicks — `GetScrollbackTargets()` returns both mixed together, so the reader must
  filter rather than pass it through.

### 4.4 New accessors required

Private today, with no way in:

| Member | Why |
|---|---|
| `IrcService.LastInboundUtc` | `_lastPongOrMessage`; what the 180 s liveness check reads and the best single "is it alive" signal |
| `IrcService.ChannelSnapshot()` | `Channels` is live and non-concurrent (§3.3) |
| `IrcService.PendingInviteJoins()` | `_pendingInviteJoins`; the state that would have shown "gave up joining #ent" |
| `IrcService.ConnectedAtUtc` | `_connectedAt` stamps the connect **attempt**, not registration — expose with that documented, or fix the stamp |

## 5. Config CRUD

### 5.1 Reads are an allow-list, never a deny-list

`GET /config` is built by explicit projection. A field is invisible until someone adds it to
the projector. This is the inverse of redaction and is chosen because the mapping pass found
37 secret-bearing members a deny-list author would have had to think of in advance —
including non-obvious ones like `IrcChannelConfig.Key` (plaintext FiSH channel key in
`appsettings.json`), `DownloadConfig.OmdbApiKey`/`TmdbApiKey`, and `ControlApiConfig.Token`
(the API's own credential).

Secrets are represented as presence only:

```json
{ "password": { "set": true }, "channels": [ { "name": "#ent", "key": { "set": true } } ] }
```

Never the value, and never a `"[REDACTED]"` sentinel that a client could echo back on a
write and have stored literally.

### 5.2 Writes

`PUT /config/{json-pointer}` with `{ "value": ... }`. The writer:

1. **Validates** against the schema in `GET /config/schema` — type, range, enum membership,
   and cross-field rules (e.g. `loginHeadroom < loginCap`).
2. **Snapshots** the current file via the existing `AiAgent.SnapshotStore` for undo.
3. **Writes atomically** — temp file, flush, `File.Replace`. `SecureFile.WriteAllTextRestricted`
   sets the ACL but does not make the write atomic; a crash mid-write currently truncates
   `appsettings.json`.
4. **Responds** with the field path, whether a restart is required, and the snapshot id.

Handlers never touch `AppConfig`. This preserves the intent of the project invariant that
only `Validators/*` mutate configuration.

### 5.3 Audit must record paths, not values

`AuditTrail.Before`/`After` are `public object?` and store raw values. Routing API config
writes through them as-is would persist a password to `ai-data/` the moment one is changed.

The API audit record stores the **JSON pointer, a SHA-256 of the before/after values, and a
timestamp** — enough to prove what changed and to detect an unexpected change, without
retaining the secret.

### 5.4 Pre-existing leak to fix as part of this work

`ConfigManager.Save` (`ConfigManager.cs:92-105`) re-reads the previous file, diffs it, and
records **raw scalar values** as `ConfigOverrideEvent.BeforeValue`/`AfterValue` into
`ai-data/overrides-{date}.jsonl`. Every config save today writes changed values in
plaintext, and the API would make that path far hotter.

`ConfigDiff` must emit hashes for pointers matching the secret allow-list, or the recorder
must drop those pointers. This is a bug independent of the API and should land early in the
implementation order.

## 6. Security invariants

Pinned by test, not by review:

1. Listener binds `http://127.0.0.1:{port}/` only.
2. Every request re-checks `IPAddress.IsLoopback` even with a valid token.
3. Bearer token compared with `CryptographicOperations.FixedTimeEquals`.
4. Disabled by default; token generated on first enable and never logged.
5. No endpoint type holds a field or constructor parameter of type `ServerManager`,
   `AppConfig`, `IrcService`, `MountService`, or `SpreadManager`.
6. No response DTO graph reaches a `NetworkCredential`, `FishKeyEntry`, or a property whose
   name matches `*Password|*Token|*ApiKey|Key`.
7. `/logs` takes no caller-supplied path. `irc-logs/` — plaintext, FiSH-decrypted channel
   chat — is a sibling directory of `logs/`, one `..\` from any file parameter.

Invariants 1-4 already hold and are covered by `ControlApiSecurityTests`; 5-7 are new.

## 7. Testing strategy

| Layer | Approach |
|---|---|
| Redaction / projection | Pure unit tests over the projector: every `AppConfig` leaf either appears in the allow-list or is asserted absent from the projection |
| Security invariants 5-6 | Reflection tests walking endpoint constructors and DTO type graphs |
| Path traversal | Fuzz `/logs` parameters with `..`, absolute paths, UNC, encoded separators |
| Routing | `RouteTable` unit tests: match, param capture, method mismatch, trailing slash |
| Endpoints | Each endpoint against a fake reader — no live subsystem needed |
| Threading | Reader snapshot tests asserting a returned collection is not the backing instance |
| Regression | Existing `ControlApiSecurityTests` must pass **unmodified** through the refactor |

The last row is the guard that the refactor did not weaken the audited security code.

## 8. Out of scope

- SSE/WebSocket streaming (cursor polling is the contract; SSE could layer on later)
- Binding any non-loopback address, TLS, scoped tokens
- Hot-apply of config changes
- Replacing any part of the WPF Dashboard
- Credential writes — passwords stay in Windows Credential Manager, set through the UI

## 9. Risks

| Risk | Mitigation |
|---|---|
| A future endpoint bypasses a reader and grabs a manager directly | Invariant 5 is a reflection test, so it fails the build |
| Allow-list projector drifts as config grows | `GET /config/schema` is generated from the projector, so an unlisted field is visibly absent |
| Refactor silently weakens auth | `ControlApiSecurityTests` must pass unmodified |
| Token is plaintext in `appsettings.json` | Accepted: loopback-only, and the file is already ACL-restricted via `SecureFile`. Revisit only if remote binding is ever added |
| `/irc/{id}/msg` speaks as the user | Accepted per explicit decision; loopback + tunnel is the containment |

## 10. Implementation order

1. Fix the `ConfigManager.Save` telemetry leak (§5.4) — independent, ships alone
2. Refactor to router + endpoint modules, no behaviour change, existing tests unmodified
3. Readers + DTOs + invariants 5-6 as tests
4. Read endpoints, subsystem by subsystem
5. `CursorStore` + `/logs` + invariant 7
6. Action endpoints
7. Config schema, projector, `ConfigWriter`, audit

## Related

- `project_recurring_bug_patterns` — patterns #4 (enumeration vs defining property) and #10
  (invariant belongs on the resource) both shaped §3.2
- v3.10.58 — `/sections` path disclosure and the IRC trace redaction, found by the same
  mapping pass that produced this spec
