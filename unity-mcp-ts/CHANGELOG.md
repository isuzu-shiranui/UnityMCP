# Changelog

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
