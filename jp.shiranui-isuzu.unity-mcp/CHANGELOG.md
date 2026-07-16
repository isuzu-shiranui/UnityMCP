# Changelog

## [2.1.1] - 2026-07-16

### Fixed
- Removed `System.Private.CoreLib.dll` and `System.Runtime.Loader.dll` from `Editor/Plugins/` — they conflicted with Unity's Mono runtime and broke Scene view left-click drag (#14, thanks @pandrabox)
- MCP server no longer starts in AssetImportWorker / batchmode processes, which raced the main Editor for the HTTP port and caused `SocketException` (#13, thanks @Lasagnoa)

---

## [2.1.0] - 2026-04-24

### Added
- MCP tool `unity_execute_code` — built-in code execution via MCP (previously sample-only)
- MCP prompt `code_execute` — C# code templates for `unity_execute_code`
- `UnityConnection.sendToEndpoint(path, body, opts)` — transport for handlers calling non-/command endpoints
- `/capture_screenshot` supports Editor panel capture via `view` = `inspector` / `hierarchy` / `project` / `console` / `game_view_window` / `scene_view_window` / `window:<title>`
- New error codes: `window_not_found`, `window_minimized`, `unsupported_platform`
- Response field `windowTitle` on `/capture_screenshot`

### Removed
- `Samples~/UnityMCPHandlerSamples/` (C# samples redundant with built-in `/execute_code`)
- `Samples~/UnityMCPHandlerSamplesJS/` (JS samples replaced by built-in MCP tool/prompt)
- Both entries from `package.json` `samples` array

### Platform notes
- Editor panel capture (inspector/hierarchy/etc.) is Windows-only in 2.1. Non-Windows returns `unsupported_platform` (501). `view=game` / `view=scene` (camera-based) remain cross-platform.

### Migration v2.0 → v2.1
- If you imported `UnityMCPHandlerSamples` or `UnityMCPHandlerSamplesJS` for code execution, remove them — functionality is now built-in.
- No API breaking changes for existing v2.0 consumers.

---

## [2.0.0] - 2026-04-24 — BREAKING

### Breaking

- **Unified response envelope** `{status, result?, error?, truncated?, next?}` across all HTTP endpoints. Old clients that parse flat response keys (e.g. `output`, `image`, `isPlaying` at top level) will break — these are now under `result`.
- **`autoRestartOnPlayModeChange` setting removed.** PlayMode transitions no longer restart the server (domain reload is handled separately via `AssemblyReloadEvents`).
- **`UnityConnection` auto-active-on-register removed.** Multi-instance + no target + no explicit `unity_setActiveClient` → `target_required` error. Single registered instance is still used automatically as a convenience.
- **ProjectApi port changed** to 27180–27189 fallback range (previously fixed 27180).

### Added

- **Context-economy params** `limit` / `offset` / `fields` on list-returning endpoints (`/read_logs`, `/browse_hierarchy`, `/inspect` list mode, `/resource`). Truncated responses include `truncated: true` and `next: {offset, limit}` in the envelope.
- **`/health` expanded**: now includes `clientId`, `uptimeSec`, `reqCount`, `handlers[]` (each with `name` and `idempotency`), and `resources[]`.
- **`ProjectApi /proxy/:name/*`** passthrough route for CLI / curl usage. Provides multi-project routing, body buffering (10 MB cap), automatic retry, and sub-path-based idempotency classification.
- **TS-side `err.cause.code` classification**: Unsafe handlers only retry on TCP pre-handshake failures (`ECONNREFUSED`, `ENOTFOUND`, `UND_ERR_CONNECT_TIMEOUT`). Post-handshake failures are fatal for Unsafe endpoints to prevent double-execution.
- **Per-handler `Idempotency` declaration** (`Safe` / `Unsafe`). Advertised in `/health.handlers[].idempotency` and cached by the TS server.
- **SessionState port persistence**: bound port is written to `SessionState` before reload and restored after, so the same port is re-bound after domain reload.
- **Race-free `Start()` order**: `SessionState` update + `running = true` before listener thread start. Rollback on thread launch failure.
- **`ProjectRegistry` 3-state model** (`healthy` / `reloading` / `unhealthy`) with configurable `unhealthyCooldownMs` (default 60s, env `MCP_UNHEALTHY_COOLDOWN_MS`). `reloading` state is TS-local estimation — no Editor-side notification needed.
- **MCP tools accept optional `target` parameter** (clientId or projectName match with full/partial precedence rules).
- **Jest tests for TS** (46 tests): ProjectRegistry UDP parse, UnityConnection retry logic, ProjectApi proxy forwarding.
- **NUnit tests for Editor**: `ListResponseBuilder` limit/offset/fields, envelope serialization.
- **TS env vars**: `MCP_RELOAD_RETRY_MAX_MS` (15000), `MCP_UNHEALTHY_COOLDOWN_MS` (60000), `MCP_PROJECT_API_PORT` (27180), `MCP_UDP_PORT` (27183), `MCP_HEALTH_INTERVAL` (10000).

### Changed

- PlayMode state changes no longer trigger server stop/restart. Only actual domain reloads (via `AssemblyReloadEvents`) cause a brief listener downtime.
- `/health` `state` field is now always `"running"` constant (listener cannot respond when down). `reloading` / `unhealthy` states are TS-local inferences.
- Handler registration uses reflection-based auto-discovery (`IMcpCommandHandler` / `IMcpResourceHandler`), same pattern as before but now also populates `/health.handlers[]`.

### Removed

- `McpServer.cs` — replaced by `McpHttpServer.cs`
- `autoRestartOnPlayModeChange` setting and all associated UI/init code
- `UnityConnection` auto-active-on-register logic (lines 60–64 of old implementation)
- Flat response bodies on all endpoints (everything is now under `.result`)

### Migration v1 → v2

1. **Update curl/client code** parsing flat response bodies to look under `result`:
   - `.returnValue` → `.result.returnValue`
   - `.output` → `.result.output`
   - `.image` → `.result.image`
   - `.isPlaying` → `.result.isPlaying`
   - `.logs` → `.result.logs`
   - All other fields: add `.result.` prefix

2. **Remove `autoRestartOnPlayModeChange`** from any stored config or settings files.

3. **Multi-Editor users**: call `unity_setActiveClient` after listing clients, or pass `target` parameter in each tool call. The implicit "first discovered = active" behavior is removed.

4. **CLI users**: prefer `http://127.0.0.1:27180/proxy/<projectName>/<endpoint>` over direct port access for reload resilience and multi-project routing.

5. **ProjectApi port**: update any hardcoded `27180` references to use discovery via `GET /projects` if the port may have shifted in the 27180–27189 range.
