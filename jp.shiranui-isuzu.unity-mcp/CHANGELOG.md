# Changelog

## Unreleased

### Added
- **Timeline tools**, for the video and live work these projects are for: `timeline_inspect`
  reports a director's tracks, clips and bindings, and — the point — follows Control tracks into
  the child timelines they drive, recursively. A live stage is routinely a root timeline whose
  Control clips each start a character or effect timeline several layers down, and a tool that
  stops at the first layer cannot see where anything happens. `timeline_evaluate` moves a director
  to a time or frame and evaluates it in the Editor without entering Play Mode, so
  `capture_screenshot` and `render_compare` can check one exact frame.

  Their own assembly, constrained to `UNITY_TIMELINE`: a project without `com.unity.timeline`
  loses the two tools rather than failing to compile. `timeline_evaluate` saves and restores the
  director's update mode, so scrubbing in the Editor does not leave it unable to advance under Play.

  `UnityMCP.Editor.Timeline.Tests` covers this against a fixture whose nested structure the test
  defines — a Root timeline whose Control clip drives a Child timeline with an Activation track —
  checking that the nesting resolves to the driven object, the child's tracks and clip timings come
  back, `nest_depth` names the child without expanding it, a director evaluated to frame 60 reads
  back at 2.0s, and the update mode is left as it was found.

- **Server instructions, and per-tool hints for clients that defer tool definitions.** A client with
  a large catalogue no longer loads tool definitions upfront — it loads the names and the server's
  instructions and searches for the rest. With 68 tools this server is firmly in that regime, and it
  was sending no instructions at all, so the only thing a client had to go on at the start of a
  session was the tool names. It now says which kinds of work live here, in the words someone would
  use to ask for them, and lists the name prefixes first because those are what a search matches.
  Kept to 1,176 bytes: clients truncate this at 2KB.

  Two hints travel with the tools themselves, declared on `[McpTool]` and emitted into each tool's
  `_meta`, so nothing has to be configured on the client side:
  - `AlwaysLoad` keeps a definition loaded rather than deferred. Set on three tools only —
    `console_read_logs`, `scene_browse_hierarchy`, `compile_status` — the ones wanted on nearly
    every turn, where a search round trip each time costs more than the context they take. Marking
    more would put the whole catalogue back in the prompt and defeat the mechanism.
  - `MaxResultSizeChars` raises where a text result is spilled to a file instead of returned. Set on
    the four tools whose useful answer is genuinely large. Deliberately not set on
    `capture_screenshot`: the limit it would raise does not apply to image content.

  Responses now state `charset=utf-8`. Tool descriptions contain non-ASCII punctuation, and a client
  that falls back to Latin-1 on a bare `application/json` turns each em dash into three characters —
  observed while auditing the catalogue.

  Audited the rest of what a deferring client cares about and changed nothing, because it was
  already right: `tools/list` is ordered by name and stable across calls (now a spec-level SHOULD,
  for prompt-cache hit rates), no schema has a root-level `anyOf`/`oneOf`/`allOf`, all 68 tools come
  back in one page, the longest description is 440 bytes, and no description contains a surrogate
  pair.

- **Timeline editing**, so the Timeline tools are no longer one-way. `timeline_edit_clip` retimes or
  renames a clip; `timeline_shift_clips` ripples everything at or after a time so a length change
  earlier in the sequence does not have to be repaired clip by clip; `timeline_set_track` mutes,
  locks, renames and binds; `timeline_delete` removes a track or a clip; and `timeline_create`,
  `timeline_create_track` and `timeline_create_clip` build one from nothing, including the Control
  clip that nests a child timeline inside a parent.

  Two decisions shape the rest. **The result reports the value read back, not the value requested.**
  Timeline's setters are gated on capabilities a clip's asset declares and discard what they do not
  accept without raising anything — an Activation clip advertises no capabilities at all, so setting
  its speed or blend does nothing — and a caller that trusted its own request would carry on
  believing a change it never made. Anything that did not take is listed in `ignored` with the
  effective value and the reason. **And the creation tools refuse before acting when the timeline is
  not yet an asset.** Timeline writes a track into the file only if the timeline already is one, and
  offers nothing to persist it afterwards, so a timeline built in the wrong order looks entirely
  correct until the next domain reload discards it.

  Tracks are addressed by path (`Cameras/CamFront`), which `timeline_inspect` now reports for every
  track, because Timeline places no uniqueness requirement on track names and a track can sit inside
  a group. Clips cannot be addressed that way — a `TimelineClip` is not a UnityEngine.Object and has
  no id — so they are addressed by name, index or a time they cover, and every edit reports the
  clip's address again, since changing a start re-sorts its track. A locked track is refused with the
  tool that unlocks it named in the message; unlocking itself stays allowed, or the lock would be a
  one-way door.

  Reordering tracks is deliberately absent: Timeline exposes no public API for it, and the
  serialized lists behind it need a cache invalidation that is also internal, so the failure would be
  quiet. Extrapolation modes are internal-set and likewise out.

- **Recorder tools**, so a timeline can be rendered out without leaving the agent:
  `recorder_add_track` puts a Recorder track on a Timeline — movie (mp4, webm, mov) or image
  sequence (png, jpeg, exr), capturing the game view, the active/main/tagged camera or a
  RenderTexture, at a chosen resolution — and `recorder_list` reports what a timeline will record
  and where it will land. Recording is set up as a track rather than through the Recorder API
  directly because the frame rate then comes from the Timeline itself, so the recording cannot
  drift from the animation, and because the surface reached this way has stayed put across
  Recorder 2.x to 5.x.

  `recorder_add_track` declared an undo group without recording anything, so it claimed an undo it
  did not provide: Timeline registers the track and clip it creates, but the recorder settings object
  is created by the tool and would have been left inside the .playable after the track was undone
  away. It registers that object now, and a test undoes a real call and counts the objects in the
  file to confirm nothing is left behind.

  Omitting `output_path` writes to a `Recording` folder beside `Assets`, named after the timeline.
  An explicit absolute path needed a workaround: Recorder splits it into Root=Absolute plus the
  directory in `Leaf`, but leaves its internal `absolutePath` null and only falls back to `Leaf`
  while that field *is* null. Unity deserializes a null string as `""`, so the domain reload on
  entering Play Mode made the root resolve to empty and the recording landed in the project folder
  — silently, with the correct path still reported back. The tool now pins that field so the
  destination survives the reload, and refuses the recording if it cannot, rather than writing
  somewhere the caller did not ask for.

  Their own assembly, constrained to `UNITY_RECORDER` and `UNITY_TIMELINE`. Verified two ways.
  `UnityMCP.Editor.Recorder.Tests` puts the settings back through Unity's serializer — the step a
  domain reload performs, and the one that broke the absolute path — and checks the destination,
  format, resolution and camera source survive it. Separately, a nested stage was rendered end to
  end: a root timeline whose Control clip drives a child timeline holding three Activation-tracked
  cameras and an Animation track spinning a cube, out to a 1920x1080 mp4, checked by decoding every
  frame — 180 frames, 180 distinct, exactly 6.000s. An earlier run of that same stage reported the
  right resolution, fps and frame count while being frozen for 5.5 of its 6 seconds, so the frame
  content is what the check looks at.

- Compiles and runs on Unity 6.5, which made the int instance-id API obsolete-as-error. The ten
  call sites that identified an object go through one `EntityIdCompat` helper that uses
  `EntityId` on 6.5+ and the int API below it. The wire contract is unchanged: the field is still
  `instanceId` and still a JSON number, widened from int to long so a 64-bit id is not truncated.
  Verified by running the EditMode suite on three Editors — 6.0 and 6.3 on the int path, 6.5 on
  the EntityId path — 163 passing on each. The version boundary is 6.5, not 6.2: `EntityId.ToULong`,
  `FromULong` and `objectReferenceEntityIdValue` do not exist on 6.3, established by compiling
  against it.

### Changed
- The release refuses to publish Editor sources that no recorded test run covers.
  `scripts/run-editmode-tests.ps1` runs the EditMode suite in a real Editor and writes an
  attestation naming the sources it ran against; the release compares that against the sources
  being published. No runner here has a Unity licence, so nothing automated compiled a line of
  C# — a regression could have reached npm without a single test having run.

  The attestation names the sources rather than the commit, so a documentation change still
  releases without re-running anything, while any edit to a `.cs` or `.asmdef` requires a fresh
  run.

### Known
- The package does not compile on Unity 6000.5. `Object.GetInstanceID()`,
  `EditorUtility.InstanceIDToObject` and `SerializedProperty.objectReferenceInstanceIDValue`
  became obsolete-as-error there and the package has not been migrated to `EntityId` — ten call
  sites across six files. Found by the script above on its first run, which had picked the
  newest installed Editor. The tested version is now pinned to 6000.0.x and recorded in the
  attestation.

## [3.2.0] - 2026-07-28

Thirty-four tools, taking the count from 23 to 57. Until now the only way to
change anything was `execute_code`, which is not on the undo stack, cannot
declare its idempotency, and costs a round trip whenever a type name is guessed
wrong. The rendering tools were chosen from what a real pipeline port actually
did over and over, not from what looked useful.

### Added
- **Authoring.** `gameobject_create` / `_delete` / `_reparent` / `_duplicate` /
  `_set_transform` / `_set_active` / `_add_component` / `_remove_component`,
  `asset_find` / `_info` / `_create_folder` / `_move` / `_delete` / `_reimport`,
  `scene_list` / `_open` / `_save` / `_create`, `prefab_create` / `_instantiate` /
  `_apply`. Every mutation is on the undo stack, so one call is one Ctrl+Z.
- **Builds.** `build_settings`, `build_player`, `build_switch_target`. A cold
  build crosses the synchronous window and comes back as a job; an incremental
  one answers inline. Unity Hub is deliberately not wrapped — an uninstalled
  target says so and gives the Hub CLI command to run.
- **Rendering and shaders.** `render_compare` reports how two captures differ in
  numbers rather than pictures; `render_pipeline_info` reports both the graphics
  and quality-level pipelines, because the second overrides the first;
  `render_camera_info` exposes the view, projection and GPU projection matrices
  so a value read off a screenshot can be checked against one computed on the
  CPU. `shader_errors`, `shader_info`, `material_read`, `material_set`.
- **Live state.** `reflect_read` and `reflect_find_type` read private members by
  path; `gpu_readback` reads a buffer or texture back and reports its range,
  mean, zero count and histogram rather than its contents.
- `capture_screenshot` takes `save_path`, writing the PNG to disk and returning
  the path. Comparing two inline captures costs most of a context window, which
  defeats the point of having a comparison tool.
- `scene_browse_hierarchy` emits `path`. Without it, a caller who had just
  browsed the hierarchy had to guess at the identifier every other tool takes.
- Scene edits made during Play Mode carry a `playModeWarning`. They succeed,
  report truthfully, and are reverted on exit; asset edits made at the same
  moment survive, and nothing distinguished them.

### Fixed
- **One tool call is one undo step.** `ToolInvoker` captured the undo group
  without incrementing first, so every call in a session shared a group and each
  collapse merged everything recorded since — a single Ctrl+Z reversed the whole
  conversation. Present since 3.0.0, and invisible to any test that made one
  call.
- **Editor window captures were upside down.** Every Inspector, Hierarchy,
  Project and Console screenshot came back mirrored top to bottom. The DIB was
  requested top-down while `LoadRawTextureData` expects bottom-up.
- `render_compare` leaked a texture per failed call when the second image was
  missing, and reported a mismatched pair as `tool_failed` because it read the
  sizes after destroying them.

### Changed
- Mutating a GameObject returns what the operation was about rather than
  everything about the object. The transform comes back from the tools that
  move it and the component list from the tools that change it.
- Tests: 94 to 163. The new ones are shaped around repetition and boundaries,
  because every defect above was correct once and wrong afterwards.

## [3.1.0] - 2026-07-28

### Added
- `test_run` and `test_results` drive Unity's test runner. `test_run` returns as soon as the
  run is queued, because a run holds the main thread for its whole duration; `test_results` is
  declared `MainThread = false` so it can report counts and failures during exactly that window,
  when no other tool answers. State is mirrored into `SessionState`, so a PlayMode run's domain
  reloads do not lose it — a run that was interrupted says so rather than reporting "running"
  forever.
- These two live in their own assembly, `UnityMCP.Editor.TestRunner`, constrained to
  `UNITY_INCLUDE_TESTS`. A project without `com.unity.test-framework` loses the two tools
  instead of failing to compile the package, and no dependency is pushed onto consumers who
  do not want it.

### Fixed
- `/health` advertised a hand-written version that had already fallen behind the package, the
  exact failure its own comment warned about. The version is read from the package manifest
  now; the literal that remains is only for an assembly loaded outside a package, and CI
  checks that one too.

### Removed
- The `501` stubs for `POST /test/run` and `GET /test/results`, now that the tools exist.

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
