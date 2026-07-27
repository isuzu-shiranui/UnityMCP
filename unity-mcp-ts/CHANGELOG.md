# Changelog

## [3.1.0] - 2026-07-28

Released to keep the version in step with the Unity package, which gained `test_run` and
`test_results`. No change was needed here: this server forwards whatever the Editor publishes
at `GET /tools`, so the two new tools appear on their own. That is the property the v3
architecture exists to have, and this release is the first time it has been exercised.

### Fixed
- The version reported in the MCP `initialize` handshake was the literal `3.0.0`, so this
  release would have introduced itself as the previous one. It is read from package.json now,
  and a test asserts the manifest is actually found rather than silently falling back.

### Changed
- The bundled skill documents the test tools and the polling they require.
- Releases now publish through npm trusted publishing (OIDC) rather than a stored token.

## [3.0.0] - 2026-07-27

A breaking release. Tools are declared once, in the Editor; the TypeScript server forwards
what it finds rather than keeping its own copy.

### Breaking
- Tools are renamed and the multi-action endpoints are split per action:
  `console.getLogs` -> `console_read_logs`, `/inspect` with a `mode` argument ->
  `inspect_read` / `inspect_list` / `inspect_write`, `/play_mode` with an `action` argument ->
  `play_mode_status` / `_play` / `_stop` / `_pause` / `_unpause` / `_step`.
- Every request requires a bearer token, published in the Editor's descriptor file.
- UDP discovery is gone. Editors publish a descriptor file; clients read it.
- MCP resources are withdrawn. `project_assemblies` and `project_packages` cover the same
  ground as tools; the TypeScript resource handlers had posted to an endpoint the Editor never
  registered, so they had never worked.
- `unity_listClients` and friends are snake_case; `unity_connectToProject` is removed as an
  alias of `unity_set_active_client`.
- `console_set_filter` is not exposed as a tool. Setting the console's search filter is
  persistent Editor UI state that silently narrows every later read: with a filter set,
  `console_read_logs` returned 1 entry instead of 21 and `console_get_count` reported
  `errorCount` 0 next to `logCount` 23. An agent checking for errors would have concluded
  there were none. The `console.setFilter` command endpoint remains.
- Five settings are removed. All of them looked like controls and governed nothing: the UDP
  toggle never stopped the broadcaster, port persistence was unconditional, and
  `reloadRetryMaxMs` was documented as being read from `/health`, which never published it.

### Added
- `[McpTool]` and `[McpArg]`: a tool is a static method, and its JSON Schema is derived from
  the signature. `GET /tools` publishes the catalog.
- Attributes declare retry classification, whether the main thread is needed, whether the call
  is destructive, and an Undo group.
- `MainThread = false` tools, `/health`, `/jobs` and `/tools` are served off the main thread,
  so they answer while the Editor is busy. `/health` reports queue depth.
- Work slower than `syncWaitMs` (3 s) returns a job id: `GET /jobs`, `GET /jobs/<id>`,
  `POST /jobs/<id>/cancel`.
- `editor_log_tail` reads `Editor.log` from disk and works while the Editor is wedged.
- `compile_status` and `compile_request` replace the `/compile/*` stubs.
- `execute_code` accepts `code_base64`, caches compilations by source hash, serializes return
  values structurally, and reports rather than awaits an incomplete Task.
- The npm package is scoped: @shiranui_isuzu/unity-mcp, with a single isuzu-unity-mcp
  command. An unscoped unity- name in the public registry reads as official Unity tooling,
  and the unscoped unity-mcp is already taken by an unrelated project that also installs a
  binary called unity — Unity's own CLI command. unity-mcp-server is taken twice over.
  The scope says who owns this without anyone having to check.
- The installer fetches the server from npm, pinned to this package's version.

### Fixed
- The port scan never scanned. It caught only `HttpListenerException`, but the Editor runs on
  Mono, whose `HttpListener` is implemented over managed sockets, so a busy port arrives as a
  `SocketException` and aborted the loop on its first candidate. A second Editor, or the same
  Editor rebinding after a domain reload, started no server at all with nineteen free ports in
  the range. Related to the AssetImportWorker race worked around in 2.1.1 (#13), which treated
  a symptom of the same defect.
- A ten-second main-thread timeout returned 504 while leaving the work queued, so the side
  effect landed anyway and a retry ran it twice.
- The queue lock was held across execution, so one slow call stalled every other request.
- `Access-Control-Allow-Origin: *` was sent on every response, letting any web page the user
  had open POST to `/execute_code` and run arbitrary C# in their Editor.
- Targets resolved by first-hit, so asking for "MyGame" with "MyGame Sandbox" also open
  depended on registration order and could send a write to the wrong project.
- The Editor did not stop its server on quit, leaving a stale descriptor behind.
- `execute_code` returned `ToString()`, so a list came back as a type name; loaded one
  un-unloadable assembly per call; and never refreshed its metadata references.
- Two bundled Roslyn Scripting assemblies were never referenced and are removed (160 kB).

### Notes
- Build exclusion is asserted in CI rather than left to convention: every assembly definition
  must be Editor-only, and every source file and binary must live under an `Editor/` folder.
  The package compiles and runs arbitrary C#, so reaching a player build would be a remote
  code execution hole. Nothing reaches a player today, Development Build included, because no
  runtime assembly exists.
- Third-party notices are added for the redistributed Roslyn and .NET assemblies.
- CI now type-checks, lints, tests and builds on every pull request; previously nothing ran.

## [2.1.1] - 2026-07-16

### Added
- Task-resilience for background tasks (#9): UDP listener restarts use exponential backoff (5s→60s, reset on successful bind); health-poll/eviction ticks emit rate-limited degradation warnings after 3 consecutive failures; repeated uncaughtException/unhandledRejection (5+ in 60s) log a "consider restarting" warning

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

- **Unified response envelope** `{status, result?, error?, truncated?, next?}` across all HTTP endpoints.
- **`autoRestartOnPlayModeChange` setting removed.**
- **`UnityConnection` auto-active-on-register removed.**
- **ProjectApi port changed** to 27180–27189 fallback range.

### Added

- Context-economy params `limit` / `offset` / `fields` on list-returning endpoints.
- `/health` expanded with `clientId`, `uptimeSec`, `reqCount`, `handlers[]`, `resources[]`.
- `ProjectApi /proxy/:name/*` passthrough route.
- `ProjectRegistry` 3-state model (`healthy` / `reloading` / `unhealthy`).
- MCP tools accept optional `target` parameter.
- Jest tests for TS (46 tests); NUnit tests for Editor.
