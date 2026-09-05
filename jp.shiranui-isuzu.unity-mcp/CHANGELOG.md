# Changelog

## [4.0.4] - 2026-09-05

### Fixed
- `console_read_logs` reported compilation warnings as errors. Its flag table called bit 13
  `kStickyError`; there is no such flag. Bit 13 is `kStickyLog`, which marks an entry the
  Console keeps when the user clears it by hand, and Unity leaves it out of its own
  `kErrorLogFlags`. The compilation pipeline sets it beside `kScriptingWarning` for asmdef and
  versionDefines problems, so those came back as errors, vanished under `type: "warning"`, and
  were counted as warnings by the `warnings` field of the same reply. An agent checking whether
  compilation failed was told it had. Bit 20 was listed from the managed `ConsoleWindow.Mode`
  rather than the native flags these are read from, and is gone too. A test now compares the
  whole set against the Editor'''s.
- `console_read_logs` says when the Console window is withholding entries. 4.0.2 changed
  `ConsoleCommandHandler`, which answers `console_get_count`, and its note said both console
  tools were covered; `console_read_logs` reaches the Console through `LogReader`, which was
  not changed. That reply also contradicted itself: `total` came from the filtered
  `LogEntries.GetCount` while `errors` and `warnings` came from the unfiltered
  `GetCountsByType`, so a Console with its Log toggle off could report a total smaller than the
  severities printed beside it. The arithmetic and the wording now live in one place both paths
  call, and a test covers the read path.
- `animator_audit` takes `path`, not `asset_path`, and the agent skill said `asset_path`. The
  unknown argument was dropped and the call failed with `invalid_params`, while every other
  `animator_` example on the page worked, so the tool looked broken.
- Both guide pages said a project without Timeline and Recorder publishes 75 tools. It
  publishes 77; reaching 75 also needs the Test Framework absent, and Unity installs that by
  default. The READMEs and the tool tables already stated the condition correctly.
- `uninstall` left the registration, and its bearer token, in `~/.claude.json` on a machine set
  up before 4.0.4. It looked for the entry only at the key this version writes, found nothing,
  listed nothing, and exited successfully. What stayed behind was a credential for an endpoint
  that runs arbitrary C# in the Editor.
- `doctor` called that same un-migrated entry healthy. Normalising both sides of the comparison
  fixed the lookup and, as a side effect, made a key Claude Code cannot read match the running
  Editor, so the one entry that needed moving was reported as the good one. It is now named as
  filed where Claude Code does not read it, and `doctor --fix` moves it and takes the old key
  away, which also fixes `upgrade`, since that ends by running `doctor --fix`.
- A tool result of the form `{"error": "message"}` reached MCP clients as a success. The REST
  path promoted it to a 400; the MCP path, which is what every MCP client speaks, wrapped it as
  a completed call and never set `isError`. `capture_screenshot` with no scene view,
  `console_read_logs` when the reflection is unavailable and every failure `console_get_count`
  and `console_clear` report this way were all delivered as though they had worked. Both
  transports read the convention through one method now.
- `capture_screenshot` answered a bad `save_path` with a 500 when capturing a panel and with a
  handled error when capturing a camera. The panel path had no `catch`, so the IO exception
  escaped as an internal error on a tool declared `Safe`, which is the one shape a client
  retries. Writing the file now fails with `save_path_unusable` and a 400 on both paths.
- A test walks every `readonly` field holding a reflected Unity member and fails when one no
  longer resolves. There are 45 of them, reaching APIs Unity does not make public, and nothing
  in the compiler checks their names. Running it across the version matrix is what turns a
  Unity rename into a failing test rather than a tool that answers wrongly.
- Tool descriptions a model reads before choosing arguments: `execute_code` said to write every
  type in full when seven namespaces are already imported, `animator_add_layer` explained a
  default of 0 that belongs to Unity rather than to the tool, which creates the layer at 1, and
  `scene_open` stated its refusal over unsaved changes without the additive exception its own
  sibling `scene_create` documents.
- Both guide pages implied the Recorder tools need only the Recorder package. They need Timeline
  as well. The 4.0.1 note described the install-button fix as though it applied everywhere; only
  the Windows branch downloads the script first.

- The READMEs and the client guide say where Claude Code has to be started. The server is
  filed under the Unity project'''s own path, which is what lets one machine hold several Unity
  projects at once, and a session started anywhere else does not see it. Nothing said so.
- The version badge in both READMEs still read 4.0.0.
- `setup --mcp --agent claude-code` registered the server where Claude Code never looks, on
  Windows. Claude Code keys its `projects` map with forward slashes; `Path.GetFullPath` returns
  backslashes, and the entry was filed under a key Claude Code had not written and does not
  read. Setup reported success, `doctor` reported the dead entry as matching the running Editor,
  and the server never appeared. The key is normalised now, `doctor` compares through the same
  normalisation, and setup takes away the entry an earlier build left behind.
- The READMEs said `[McpTool]` has four properties. It has eight, and `Group` is the only way to
  put a tool in the group `tools/list` filters by.
- The Preferences button is labelled Install, not "Install CLI", and it is drawn only while the
  CLI is missing from PATH. Six documents said otherwise.
- `execute_code` already imports `System`, `System.Collections`, `System.Collections.Generic`,
  `System.Linq`, `System.Threading.Tasks`, `UnityEngine` and `UnityEditor`. The tool tables told
  readers to write every type in full.

## [4.0.3] - 2026-09-05

### Fixed
- The agent skill covers the tools 4.0.0 added. `SKILL.md` is what Claude Code and Codex read to
  learn what this can do, and it named none of the twelve `animator_` tools, neither
  `editor_dialog_list` nor `editor_dialog_press`, nor `input_pointer`, `definitions_list` or
  `scene_browse_hierarchy --missing_scripts`. A tool absent from the skill is a tool an agent
  does not reach for. The dialog pair mattered most: its own section, "The Editor stopped
  responding", offered only `health`, `jobs` and `editor_log_tail`, so the way out of a modal
  dialog was missing from the one page a stuck agent reads.
- The NuGet listing shows what the package is. `dotnet tool search` returned the SDK's
  placeholder `Package Description`, the assembly name in place of an author, and no licence,
  project link or tags. The csproj now carries a description, `Authors`, `PackageTags`,
  `PackageLicenseExpression`, `PackageProjectUrl`, `RepositoryUrl` and the README.
- Package Manager's Documentation link points at the getting started guide rather than the
  repository root, and `keywords` no longer names one AI client. The package works with Claude
  Code, Claude Desktop, Cursor, Codex, the Gemini CLI and VS Code.

## [4.0.2] - 2026-09-05

### Fixed
- `console_get_count` says when the Console window is withholding entries. Unity's `LogEntries.GetCount` answers with that window's own filter applied: its
  Error, Warning and Log toggles and the text in its search box. A project with 25 entries
  reported 9 with the Log toggle off and 0 with anything typed in the search box, and neither
  answer said why. An agent checking whether something failed was told nothing had. The reply
  now carries `hiddenByConsoleFilter` and a sentence naming the cause, derived from
  `GetCountsByType`, which ignores the filter. Nothing in the user's Console window is changed
  to produce it: a tool that reads should not rewrite the window it reads from.

The other half of this was already closed. `console_set_filter` is deliberately not a tool
because setting that filter narrows every later read, and the note in `ConsoleTools.cs` records
it. What remained was the same filter set by hand, by the person at the Editor.

## [4.0.1] - 2026-09-05

### Fixed
- The install line the READMEs and the getting started guide print now runs. `install.ps1`
  carried a UTF-8 BOM, and `irm` hands `iex` a string whose first character is U+FEFF, so
  `param()` was no longer the first statement and PowerShell refused the whole script with a
  parse error on its first parameter. The file is ASCII, so the BOM bought nothing. CI checks
  both install scripts for one now, because they are parsed rather than displayed.
- The Install CLI button in Preferences now opens a terminal. It ran the same one-liner through
  `Process.Start`, and security software refuses to create a process whose command line
  downloads and runs a script in one expression, returning access denied to the caller.
  Windows Defender records it as `Trojan:Win32/Commando.A!ml` against the command line, not
  against any file. The
  button fetches the script and hands the terminal a path instead, which also leaves the script
  on disk to read when an install fails. That is the Windows branch; macOS and Linux still
  pipe the script into the shell, where nothing refuses it.

Neither path had been exercised against a real release before 4.0.0 existed. The CLI binary is
unchanged from 4.0.0.

## [4.0.0] - 2026-09-04

### Breaking
- The CLI binary and command are renamed: `isuzu-unity-mcp` is now `isuzu-unity-cli`.
- The npm package `@shiranui_isuzu/unity-mcp` and the Node MCP server (`unity-mcp-ts`) are removed.
  The Editor serves MCP itself at `http://127.0.0.1:<port>/mcp` over Streamable HTTP.
- Removed: the tools `unity_list_clients`, `unity_set_active_client`, `unity_get_active_client`;
  the `target` parameter on every tool; the `code_execute` MCP prompt; the interfaces
  `IMcpCommandHandler` and `IMcpResourceHandler` (write a `[McpTool]` method instead).
- Removed: the v2 HTTP routes `/command`, `/resource`, `/read_logs`, `/execute_code`,
  `/browse_hierarchy`, `/capture_screenshot`, `/play_mode`, `/inspect`, `/hlsl/errors`; the
  `/proxy` ProjectApi route; the environment variables `MCP_DESCRIPTOR_INTERVAL`,
  `MCP_HEALTH_INTERVAL`, `MCP_RELOAD_RETRY_MAX_MS`, `MCP_PROJECT_API_PORT`.
- Removed: the settings `clientInstallationPath` and per-handler enable/disable; the `/health`
  fields `handlers[]` and `resources[]` (`/health` now reports `mcpUrl`, `preferredPort`,
  `portMismatch`, `toolCount`); the npm installer window (Tools > Unity MCP > Installer).
- `inspect_list`, `inspect_read` and `inspect_write` take `object_path` instead of
  `game_object_path`. Every other tool that addresses a scene object already used that name.

### Added
- A component whose script Unity cannot resolve is named `<missing script>` rather than reported
  as null, `scene_browse_hierarchy` and `inspect_list` count them per object, and
  `scene_browse_hierarchy` takes `missing_scripts: true` to return only the objects carrying one.
  That is what a removed package leaves behind, and it was previously indistinguishable from any
  other null.
- The package carries its own `LICENSE.md`, and `package.json` names the licence and links to the
  documentation, the changelog and the licence. Installing by git URL brings only this directory,
  so the licence has to travel with it.
- The release publishes to NuGet through trusted publishing: nuget.org trades the workflow's
  OpenID Connect token for a key that lasts an hour, so no API key is stored anywhere. Setting it
  up is described in `scripts/README.md`.
- Blocking dialogs are reported instead of looking like a hang. `/health` carries `mainThread`
  (`stalledMs`, the visible dialog's title, message and buttons on Windows), and a call that is
  still running because a dialog is up says so in its job envelope, in `job_status` and over MCP.
  `editor_dialog_list` reads the dialog; `editor_dialog_press` (destructive, `confirm` required)
  presses a button from outside the Editor. The CLI prints the notice while it waits.
- `isuzu-unity-cli`. The binaries in the release, and the ones the install scripts place, are
  native (.NET NativeAOT) and need no Node and no .NET runtime. The `dotnet tool` package is an
  ordinary .NET 10 tool and runs on the runtime that ships with the SDK you install it with.
  Install with `install.ps1`, `install.sh`, `dotnet tool install -g IsuzuUnityCli`, or a
  GitHub Releases binary (`isuzu-unity-cli-win-x64.exe`, `-osx-arm64`, `-osx-x64`, `-linux-x64`,
  verified against `SHA256SUMS`).
  A call takes about 20 ms end to end on Windows: the binary is native code, the Editor is
  reached over a plain loopback socket instead of a general HTTP client, and the descriptor is
  read without the serializer's set-up. `UNITY_MCP_TRACE=1` prints where the time goes.
- `isuzu-unity-cli.mcpb`, a Claude Desktop Extension bundle in the GitHub release. It carries the
  Windows, macOS and Linux binaries, declares all three platforms, installs by double-click, and
  asks only for the Unity project name.
- A VPM repository at `https://unity-mcp.shiranui-isuzu.dev/vpm.json`, so VCC and ALCOM install
  the package without a Git client on `PATH`, which is what Unity's git URL route requires. The
  release attaches the package as `jp.shiranui-isuzu.unity-mcp-<version>.zip`, and
  `scripts/build-vpm.sh` derives the listing from the releases themselves when the site deploys:
  every release carrying that zip is one version in it, described by the `package.json` inside
  that zip, so earlier versions stay installable without being stored anywhere.
- `isuzu-unity-cli verify`: recompile, wait out the domain reload, collect compiler errors, run the
  tests (`--test`, `--filter`, `--assembly`, `--category`, `--no-compile`) and read the console in
  one call. Exit code 0 success, 1 compile errors or test failures, 4 `--timeout` (300 s). `--raw`
  prints JSON.
- `isuzu-unity-cli setup` installs the agent skill for Claude Code and Codex; `setup --mcp` also
  registers the MCP endpoint from the running Editor's descriptor. Flags: `--agent
  claude-code|claude-desktop|codex|cursor|gemini|vscode`, `--scope user|project` (project scope
  writes `.mcp.json` with `${UNITY_MCP_TOKEN}`, never a raw token), `--no-skill`, `--project <name>`.
- `doctor --fix`, `upgrade [--version vX.Y.Z]`, `mcp-stdio --project <name>` (stdio bridge for
  Claude Desktop), `tools --group <name>[,<name>]`, and the environment variables
  `UNITY_MCP_STATE_DIR` / `UNITY_MCP_HOST` for a CLI in WSL2 reaching an Editor on Windows.
- Preferences > Unity MCP opens on a checklist of what is done and what is left: whether the
  server is listening, whether the CLI is on `PATH`, and the client registration that has to
  be made either from the page or with `isuzu-unity-cli setup --mcp`. Below it are the MCP URL,
  the token, and a ready-to-paste config snippet for Claude Code, Cursor, Codex, Gemini CLI,
  VS Code or the Claude Desktop stdio bridge. The settings and the help links sit behind
  foldouts, since most projects never change a setting. "Install CLI" opens a terminal running
  the install script.
- Every setting carries a tooltip, and the two that can be given an unusable value say so:
  an `HTTP Port` outside 1024-65535 cannot be bound, and a `Sync wait` below 250 ms is raised
  to the floor the server enforces. Both were accepted silently before.
- A screen-read capture is refused with `window_occluded` when another application is in front of
  the Editor. The grab reads the desktop, not the window, so the picture would have been that
  application's window, and it goes on to whatever the image is sent to. `game` and `scene` render
  through the camera and are unaffected. The views that read the screen are `inspector`,
  `hierarchy`, `project`, `console` and every view whose name ends in `_window`; the security page
  and the tool description had listed only the panels. A window of the same Editor covering the
  target is not detected, so a floating Package Manager still returns the wrong part of the screen.
- A capture failure now carries its own code and status. `McpScreenshotException` did not derive
  from `McpToolException`, so `ToolInvoker` turned every one of them into `tool_failed` with a 500.
  A refusal read as a fault in the Editor, and a client that retries safe calls repeated it for its
  whole budget.
- `JToken` and `JValue` arguments declare every JSON type rather than `object`. A schema-validating
  client could not send `inspect_write`'s own worked example, which passes a number. The four
  `animator_` arguments of that shape were affected too. A test now checks every published example
  against the schema its tool declares.
- The Preferences page draws in Japanese when the Editor is set to Japanese, and `uiLanguage`
  pins it either way. That page is the only translated surface: tool descriptions, the text a
  tool returns and the CLI output stay in English, because a model reads them to decide what
  to call.
- MCP endpoint: stateless; protocol revisions 2025-11-25, 2025-06-18, 2025-03-26; `tools/list`
  carries `readOnlyHint` / `idempotentHint` on safe tools and `destructiveHint` on destructive
  ones; `tools/call` returns `structuredContent` alongside text and tool errors as `isError`
  results; GET and DELETE answer 405; a foreign `Origin` answers 403.
- Tool groups `diagnostics`, `authoring`, `rendering`, `timeline`, `build`, `code`, `input`, from
  the name prefix or `[McpTool(Group = ...)]`. `?group=a,b` on the MCP URL or `GET /tools?group=a,b`
  filters `tools/list`; calls are never filtered. Discovery rejects an unknown group and rejects
  `UndoGroup` on a `MainThread = false` tool.
- `job_status`, a safe tool that answers while the main thread is busy.
- Animator Controller tools. `animator_inspect` reads a controller named by asset path or
  reached through any component on a scene object that points at one, which is how a character
  keeping one controller per body layer is reached without this package knowing that component.
  Without a `layer` it reports the parameters and one line per layer and no states, because a
  twenty-layer controller has hundreds of them. `animator_audit` reports what the Editor never
  mentions: parameters nothing references, states with no motion, states unreachable from the
  default state, layers with no states, duplicate layer names, transitions with neither a
  condition nor an exit time, and Write Defaults mixed within one layer with the majority and
  the states that disagree. Ten editing tools cover layers, states, transitions, parameters and
  Write Defaults across a whole layer: `animator_add_layer`, `animator_remove_layer`,
  `animator_add_state`, `animator_remove_state`, `animator_set_state`,
  `animator_set_write_defaults`, `animator_add_transition`, `animator_remove_transition`,
  `animator_add_parameter`, `animator_remove_parameter`. Each is one undo step. A controller is
  a shared asset, so a write reaches every scene and character using it, and the `.controller`
  file is written before the call returns; undo restores the controller in memory, not the file.
- `material_read` and `material_set` take `object_path` and `slot`, so a material can be reached
  through the scene object that draws it instead of only by its asset path. Reading reports every
  material slot on the renderer with its shader and its property count, and naming a `slot` returns
  that slot's property values. A shader that is missing or unsupported is named in `shaderProblem`,
  which is what makes an object magenta. Writing goes to the shared material, never to a
  per-renderer copy.
- Defined tools: a JSON file under `%LOCALAPPDATA%\UnityMCP\tools\<projectHash>\` or
  `...\tools\shared\` adds a tool without C#. Kinds: `probe` (reflection reads with root notations
  `@type:`, `@scene:`, `@id:`, `@selection`, `@sceneview:camera` and a `changes` mode), `script` (a
  `.cs` file receiving a `JObject`, re-read on every call), `sequence` (a chain of tool calls with
  `{{stepId.json.path}}` templating). Files are watched; `GET /tools?refresh=1` forces a rebuild;
  `definitions_list` reports what loaded and why something did not. Declared input types and enums
  are checked at call time (`invalid_params`); a `script` compile error answers
  `script_compile_error` (400); a `sequence` that contains a destructive step is destructive itself,
  may contain multi-frame steps such as `input_replay`, and mutual references between sequences are
  refused at load.
- Input tools `input_pointer` (drags spread over frames with `steps` / `frames_per_step`),
  `input_key`, `input_record` (writes `%LOCALAPPDATA%\UnityMCP\recordings\<projectHash>\<name>.json`)
  and `input_replay` (`then_capture` chains into `capture_screenshot`), through the Editor's GUI path.
- Preferences setting `keepEditorAwake`; `/health` reports `loopWaker` as `on-demand`, `always` or
  `unavailable`.

### Changed
- `capture_screenshot` says in its own description, and the docs say, that a panel capture reads
  the screen: an application in front of the Editor is in the image and in whatever the image is
  sent to. `game` and `scene` are rendered by Unity and carry nothing else.
- `prefab_apply` now requires `confirm: true`. It rewrites the prefab asset, reaches every
  instance of it, and is not undoable.
- `detailedLogs` starts off. Each request and each start and stop step went to the Unity console,
  and those lines come back through `console_read_logs` to the agent driving the Editor. Warnings
  and errors are logged either way, and the line naming the bound URL is always logged. The
  settings live in Unity's preferences folder and are shared by every project on the machine, so a
  machine that already saved them keeps its own value; turn it off in Preferences > Unity MCP.
- The Editor main loop is woken while a request waits, so an unfocused Editor answers in a few
  milliseconds instead of up to about 100 ms per call.
- The port is derived from the project path into 27200-27999. Preferences `HTTP Port` 0 uses the
  derived port; a positive value pins it. If the port is taken, the server scans for a free one and
  reports `portMismatch` in `/health`, the descriptor and a Preferences warning.
- The bearer token is fixed per project under `%LOCALAPPDATA%\UnityMCP\tokens\`
  (`~/.local/share/UnityMCP/tokens/` on macOS/Linux, owner-only on Unix). Preferences has a
  "Regenerate" action; run `isuzu-unity-cli doctor --fix` afterwards.
- Descriptors carry `mcpUrl`, `preferredPort`, `portMismatch` and `mcpProtocolVersions`.
- Window names `scene_view_window`, `game_view_window`, `inspector`, `hierarchy`, `project`,
  `console`, `window:<title>` are shared by `capture_screenshot` and the input tools.

### Fixed
- A request that was in flight when the listener closed for a domain reload logged an error to
  the console on every script compile, and `console_read_logs` handed it to the agent as if the
  project had failed.
- `test_run` on an EditMode run refuses with `scene_dirty` when an open scene has unsaved changes.
  The runner would otherwise stop at the Editor's save dialog, which blocks every tool while the
  call has already answered `started`. `force: true` restarts after a run that never reported.
- `test_run` and `test_results` were absent from every project that did not list this package in
  `testables`, because their assembly was constrained to `UNITY_INCLUDE_TESTS`. It is now constrained
  on the presence of `com.unity.test-framework`, so `verify --test` works in an ordinary project.
- `console_read_logs` with `type: error` or `type: warning` returned no entries while the counts
  said otherwise. The classifier tested the wrong mode bits: a `Debug.LogError` sets the scripting
  error flag and a compiler error the compile error flag, neither of which is bit 0.
- Unity 2022.3 compiles again; `render_pipeline_info` reports `batchingStatic` as null there
  (#20, #21, @takara2314).
- Unity 6.5 and later: `instanceId` is a JSON string (an EntityId can exceed 2^53); `instance_id`
  accepts a string or an integer.
- The Settings window no longer freezes the Editor when open during a domain reload.
- `timeline_inspect` detects a Control track that loops back to its own timeline on 6.5.

### Migration
| v3 | v4 |
|---|---|
| `isuzu-unity-mcp <cmd>` | `isuzu-unity-cli <cmd>` |
| `npm i -g @shiranui_isuzu/unity-mcp` | an install script, or `dotnet tool install -g IsuzuUnityCli` |
| `{"command":"node","args":[".../build/index.js"]}` | `{"type":"http","url":"http://127.0.0.1:<port>/mcp","headers":{"Authorization":"Bearer <token>"}}`, or `claude mcp add --transport http` |
| `target` parameter to select an Editor | one URL per project |
| `unity_list_clients` | `isuzu-unity-cli projects` |
| skill `isuzu-unity-mcp` | skill `isuzu-unity-cli` (`setup` removes the old folder) |
| Preferences npm installer window | Preferences "Install CLI" button |

### Verified
EditMode suite on 2022.3.22f1, 6000.0.35f1, 6000.5.10f1. CLI: `dotnet test` in `isuzu-unity-cli/`.

## [3.3.0] - 2026-07-31

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

- **Worked examples on the tools whose argument shape is not obvious.** Declared on `[McpTool]` and
  published as the input schema's `examples`, which is standard JSON Schema and where a model looks
  to see the shape rather than infer it. Added to the five tools where the parameter list can state
  a rule but not demonstrate it: arguments that constrain each other (`recorder_add_track`'s
  `camera_tag` only means something with `source=tagged_camera`), alternatives that cannot both be
  given (`timeline_edit_clip`'s `duration` and `end`, `timeline_shift_clips`'s `by` and `to_time`),
  a value whose type the schema can only call "any" (`inspect_write`), and the one-call form of
  nesting a timeline (`timeline_create_clip`'s `control_source`). Each is parsed while the catalogue
  is built, and a malformed one fails that tool's registration rather than shipping a broken schema.

### Fixed

- **A cross-model review of the Timeline editing tools found eleven defects; ten are fixed here and
  the eleventh is now refused rather than guessed.** Most were places where the code broke a rule the
  rest of it follows.

  The one that could lose work: `timeline_create` checked for an existing timeline with a typed load,
  which returns null for an asset of any other kind — so a path holding a material was reported free
  and then overwritten. It now checks for any asset at all.

  Four were mutations that ran before everything had been validated, so a refused call left part of
  its change behind: a clip moved by `start` before `end` was checked, a track created before its
  binding was resolved, a mute applied before the same, and a timeline asset written before the
  GameObject meant to host its director was resolved. All of them now resolve and validate first.
  Note that resolving the *path* was not enough — the failure that actually happens is "this object
  has no Animator", so the component is resolved too, which for a track that does not exist yet
  means reading the required type from `[TrackBindingType]`.

  Two were holes in the lock policy: a child track could be added to a locked group, and deleting a
  group deleted locked tracks inside it, which made a lock avoidable by deleting its parent.
  Deleting a group also left its children's bindings on the director, pointing at tracks that no
  longer existed.

  Three were the code not holding itself to its own standard: `timeline_shift_clips` did not read
  back what it wrote although single-clip editing does, and both track paths and `at_time` picked
  the first match silently where name collisions were already an error. Ambiguity is now refused in
  all three.

  The last was a false claim: the rollback after a failed clip creation ignored whether the deletion
  succeeded and reported the track clean regardless.

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
