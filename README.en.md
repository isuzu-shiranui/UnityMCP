# Unity MCP Integration Framework

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-3.2.0-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-C%23_9.0-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語版](./README.md)

Opens the Unity Editor to AI agents over the Model Context Protocol, and to people and scripts over a CLI.

## What's new in v3

- **Tools are defined once, in the Editor.** Put `[McpTool]` on a static C# method and its JSON Schema is derived from the signature and published at `GET /tools`. There is no tool definition on the TypeScript side.
- **The CLI does not need MCP.** `isuzu-unity-mcp` reads the descriptor file the Editor publishes and connects directly, so reaching Unity from a shell no longer requires an MCP client to be running.
- **It answers while the Editor is busy.** Tools declaring `MainThread = false`, plus `/health`, `/jobs` and `/tools`, are served from a worker thread — which is exactly when you want to know what the Editor is doing.
- **Slow work becomes a job.** A call that does not finish in a few seconds returns a job id rather than a timeout, so nothing keeps running behind a failure you might retry.
- **Authenticated.** Every request needs a bearer token; binding to loopback was never access control.

## Requirements

- Unity Editor 2022.3 or newer (EditMode verified on Unity 6.0 / 6.3 / 6.5; the 6.5 EntityId migration is handled)
- Node.js 18 or newer
- `com.unity.nuget.newtonsoft-json` 3.2.1 (resolved automatically)

## Getting started

### Installation

In Unity's Package Manager, **Add package from git URL**:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

The MCP server and CLI:

```bash
cd unity-mcp-ts
npm install
npm run build
npm link          # if you want the isuzu-unity-mcp command

isuzu-unity-mcp setup   # register with your MCP client and install the Claude Code skill
```

`setup` only updates MCP client configs that already exist, rather than creating one for a
client you do not use, and leaves every other server and key in those files untouched.

```bash
isuzu-unity-mcp doctor      # what is installed, where, and what is stale
isuzu-unity-mcp uninstall   # lists what it would remove; --yes to do it
```

### Check it works

The server starts when the Editor opens a project, and publishes a descriptor file.

```bash
isuzu-unity-mcp projects   # Editors currently running
isuzu-unity-mcp health     # server status
isuzu-unity-mcp tools      # what this Editor publishes
```

### Claude Desktop / Claude Code

```json
{
  "mcpServers": {
    "isuzu-unity-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-mcp-ts/build/index.js"]
    }
  }
}
```

The Editor does not have to be running first. Until one appears the server answers `tools/list` from its cached catalog, then sends `tools/list_changed`.

### CLI

```bash
isuzu-unity-mcp call play_mode_status
isuzu-unity-mcp call console_read_logs --type error --limit 20
isuzu-unity-mcp call scene_browse_hierarchy --json '{"name":"Player","limit":5}'

# Pass C# from a file; it is sent base64-encoded
isuzu-unity-mcp call execute_code --file snippet.cs

# When several Editors are open
isuzu-unity-mcp call play_mode_status --project MyGame
```

**Inside a project, `--project` is unnecessary.** With several Editors open, a working
directory that sits inside exactly one of them selects that one.

```bash
cd "H:/Unity Projects/MyGame/Assets/Scripts"
isuzu-unity-mcp call play_mode_status
# [using MyGame — the project this directory belongs to]
```

`isuzu-unity-mcp projects` marks the reachable one with `containsWorkingDirectory`. Run from
outside every project and it names the candidates and stops, rather than guessing. The MCP
server applies the same rule, so opening Claude Code in a Unity project removes the need for
`target`.

Errors go to stderr with a non-zero exit code, so it composes in scripts.

> **Why `--file`**: passing a C# snippet through both a shell and a JSON encoder loses the backslashes in its string literals. The result is a compile error inside generated source the caller never sees, which is close to undiagnosable. Reading from a file goes through neither.

## Architecture

```
MCP client (Claude)                        terminal / scripts
        │ stdio                                    │
        ▼                                          │
  MCP server (build/index.js)              isuzu-unity-mcp (CLI)
        │                                          │
        │  read descriptor ────────────────────────┤
        │  <port + token>                          │
        ▼                                          ▼
  Unity Editor  :27182-27199  (HttpListener, 127.0.0.1 only)
        │
        ├── GET  /tools              catalog generated from attributes
        ├── POST /tools/<name>       invoke a tool
        ├── GET  /health             state, queue depth, running jobs
        ├── GET  /jobs, /jobs/<id>   track long-running work
        └── POST /jobs/<id>/cancel   abandon a job that has not started
```

### Editor side (C#)

`ToolCatalog` collects `[McpTool]` static methods by reflection and derives each JSON Schema from the signature. `ToolInvoker` binds JSON arguments to typed parameters and applies the confirm/dry-run gate and Undo grouping the attribute declares.

`McpMainThreadDispatcher` marshals work from worker threads onto the Editor main thread. It holds the queue lock only to dequeue and runs outside it, so one slow call cannot stall every other request, and a job that has not started can be cancelled with certainty.

### MCP server (TypeScript)

`ToolCatalogClient` fetches `/tools` and `ToolRouter` serves it as `tools/list` and `tools/call`. It uses the low-level request handlers, so **the Editor's JSON Schema reaches the client unchanged**.

## Built-in tools

### Published by the Editor (57)

**Looking**

| Tool | Idempotency | Purpose |
|---|---|---|
| `console_read_logs` | safe | Console entries |
| `console_get_count` | safe | Error / warning / log counts |
| `console_clear` | unsafe | Clear the console |
| `editor_log_tail` | safe | `Editor.log` from disk (**works while the Editor is wedged**) |
| `compile_status` | safe | Whether scripts are compiling, and whether the last compile succeeded |
| `compile_request` | unsafe | Ask for a recompile |
| `test_run` | unsafe | Start an EditMode or PlayMode test run |
| `test_results` | safe | The current or most recent run (**readable while it runs**) |
| `scene_browse_hierarchy` | safe | Walk the hierarchy. **Emits `path`**, which every editing tool takes |
| `scene_list` | safe | Open scenes, and the scenes in the build settings |
| `inspect_read` | safe | Read a serialized property |
| `inspect_list` | safe | Discover property paths |
| `play_mode_status` | safe | Playing / paused / compiling |
| `project_assemblies` | safe | Loaded assemblies |
| `project_packages` | safe | UPM packages |
| `capture_screenshot` | safe | Game or Scene view, or an Editor panel. `save_path` writes it to disk |

**Authoring** — every mutation collapses into one undo step.

| Tool | Idempotency | Purpose |
|---|---|---|
| `gameobject_create` | unsafe | Create an object, optionally a primitive |
| `gameobject_delete` | unsafe | Delete it (undoable) |
| `gameobject_duplicate` | unsafe | Duplicate it |
| `gameobject_reparent` | unsafe | Move it under another parent; world position kept by default |
| `gameobject_set_transform` | unsafe | Position, rotation, scale. **Only the axes given** |
| `gameobject_set_active` | unsafe | Activate or deactivate |
| `gameobject_add_component` | unsafe | Add a component by type name |
| `gameobject_remove_component` | unsafe | Remove one |
| `inspect_write` | unsafe | Write a serialized property |
| `asset_find` | safe | Search by type, name, folder or label |
| `asset_info` | safe | Type, GUID, importer, dependencies |
| `asset_create_folder` | unsafe | Create a folder and its parents, idempotently |
| `asset_move` | unsafe | Move or rename, keeping the GUID |
| `asset_delete` | unsafe | Delete. **Goes to the OS trash, so it is recoverable** |
| `asset_reimport` | unsafe | Reimport |
| `scene_open` | unsafe | Open a scene (refuses over unsaved changes) |
| `scene_save` | unsafe | Save, or save as |
| `scene_create` | unsafe | New scene |
| `prefab_create` | unsafe | Save a scene object as a prefab |
| `prefab_instantiate` | unsafe | Place a prefab |
| `prefab_apply` | unsafe | Push instance overrides back into the asset |
| `menu_execute` | unsafe | Invoke a menu item |
| `play_mode_play` | unsafe | Enter play mode |
| `play_mode_stop` | unsafe | Leave it |
| `play_mode_pause` | unsafe | Pause |
| `play_mode_unpause` | unsafe | Resume |
| `play_mode_step` | unsafe | Step one frame |

**Rendering and shaders**

| Tool | Idempotency | Purpose |
|---|---|---|
| `render_compare` | safe | How two captures differ, **in numbers** (changed pixels, mean/max delta, bounding box, grid) |
| `render_pipeline_info` | safe | Active pipeline, colour space, graphics API, quality level. **Reports the quality-level override too** |
| `render_camera_info` | safe | Cameras with view, projection and **GPU projection** matrices |
| `shader_errors` | safe | Shader compilation errors (**a broken shader renders magenta and says nothing**) |
| `shader_info` | safe | Passes, properties, keyword space, render queue |
| `material_read` | safe | A material's **current** values, keywords and render queue |
| `material_set` | unsafe | Set a property, keyword or render queue |

**Live state and GPU**

| Tool | Idempotency | Purpose |
|---|---|---|
| `reflect_read` | safe | Read live state by type and member path, **private members included** |
| `reflect_find_type` | safe | Find a loaded type by name |
| `gpu_readback` | safe | Read a buffer or texture back and report **statistics, not contents** (range, mean, zero count, histogram) |
| `execute_code` | unsafe | Compile and run a C# snippet (**the last resort when no tool reaches it**) |

**Builds**

| Tool | Idempotency | Purpose |
|---|---|---|
| `build_settings` | safe | Active target, the scenes in the build, whether the module is installed |
| `build_player` | unsafe | Build a player. A cold build becomes a job; an incremental one answers inline |
| `build_switch_target` | unsafe | Switch the active target (reimports assets) |

Editor panel capture (`inspector`, `hierarchy`, `project`, `console`, `window:<title>`) is Windows-only; `game` and `scene` work everywhere.

`test_run` and `test_results` appear only when `com.unity.test-framework` is present, which it is by default. They live in their own assembly constrained to `UNITY_INCLUDE_TESTS`, so a project without the framework loses those two tools rather than failing to compile the package.

### Things that will bite otherwise

- **The `path` an editing tool takes is the one `scene_browse_hierarchy` returns.** It resolves
  inactive objects, and carries an index only where a sibling name repeats: `/Canvas/Button[1]/Text`.
- **A scene edit made during Play Mode looks like it worked and is reverted when Play Mode stops.**
  Those responses carry a `playModeWarning`. Asset edits made at the same moment do survive, so
  they do not.
- **Deletion is recoverable rather than gated.** Assets go to the OS trash and GameObjects go
  through Undo, so neither asks for confirmation. Opening or replacing a scene over unsaved
  changes *is* refused, because that is the one edit no undo stack can return.
- **`execute_code` is not on the undo stack.** Use the authoring tools for authoring.

### Provided by the MCP server (3)

`unity_list_clients`, `unity_set_active_client`, `unity_get_active_client` — choosing between Editors. No single Editor can answer that, which is why these stay here.

### Prompts

`code_execute` — how to write C# for `execute_code`.

## Adding a tool

**Write one method in the Editor.** There is nothing to add on the TypeScript side.

```csharp
using System.Linq;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

internal static class MyTools
{
    [McpTool(
        "asset_find_by_type",
        "Find project assets of a given type. Prefer a narrow type and a small limit: " +
        "a full asset list is large and rarely relevant to one question.",
        Idempotency = McpIdempotency.Safe)]
    public static string[] FindByType(
        [McpArg("type", "Unity type name, e.g. Material.")] string type,
        [McpArg("limit", "Maximum paths to return.")] int limit = 50)
    {
        return UnityEditor.AssetDatabase.FindAssets($"t:{type}")
            .Take(limit)
            .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
            .ToArray();
    }
}
```

It appears in `/tools` and is callable from both MCP clients and the CLI. The schema comes from the signature, so there is only one place to write it.

`[McpTool]` properties:

| Property | Default | Meaning |
|---|---|---|
| `Idempotency` | `Unsafe` | Whether the call may be retried automatically after a connection failure. Read-only tools should say `Safe` |
| `MainThread` | `true` | Whether the Editor main thread is required. `false` keeps the tool answerable while the Editor is busy — only for tools touching no Unity API |
| `Destructive` | `false` | When true the call refuses without `confirm: true` and supports `dry_run` |
| `UndoGroup` | `null` | When set, the whole call collapses into a single Undo step |

Tool names must match `^[a-z][a-z0-9_]{0,63}$`; MCP tool names cannot contain dots.

**The description is the model's only cue for reaching for a tool.** Say when to use it, not just what it does.

## Configuration

### Unity Editor (Preferences → Unity MCP)

| Setting | Default | Meaning |
|---|---|---|
| `httpPort` | 27182 | Starting port; scans up to 27199 if taken |
| `autoStartOnLaunch` | true | Start the server when the Editor launches |
| `syncWaitMs` | 3000 | Work slower than this returns a job id |
| `detailedLogs` | true | Request logging |

### MCP server environment variables

| Variable | Default | Meaning |
|---|---|---|
| `MCP_DESCRIPTOR_INTERVAL` | 2000 | Descriptor directory sweep interval (ms) |
| `MCP_HEALTH_INTERVAL` | 10000 | `/health` poll interval (ms) |
| `MCP_RELOAD_RETRY_MAX_MS` | 15000 | Retry budget while a domain reload is in flight (ms) |
| `MCP_PROJECT_API_PORT` | 27180 | ProjectApi port |

## Tests

```bash
# TypeScript
cd unity-mcp-ts && npm test

# Unity (headless)
Unity.exe -batchmode -nographics -projectPath <project> \
  -runTests -testPlatform EditMode -testResults results.xml
```

A running Editor can also be driven from MCP or the CLI.

```bash
isuzu-unity-mcp call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-mcp call test_results          # readable while the run is in progress
isuzu-unity-mcp call test_results --include_passed true --limit 200
```

**A test run holds the main thread for its whole duration, so no other tool answers during it.**
`test_results` is declared `MainThread = false` and reports counts and failures anyway. `test_run`
returns as soon as the run is queued; it does not wait for the outcome.

Running the package's tests needs `"testables": ["jp.shiranui-isuzu.unity-mcp"]` in the
project's `Packages/manifest.json`.

## Troubleshooting

**`isuzu-unity-mcp projects` finds nothing** — check an Editor has a project open and its server started. Descriptors live under `%LOCALAPPDATA%\UnityMCP\instances\`, or `~/.local/share` / `~/Library/Application Support` on macOS and Linux.

**401 responses** — the token is in the descriptor file. The CLI and MCP server read it for you; curl needs `Authorization: Bearer <token>`. Going through `isuzu-unity-mcp`, or the MCP server's `/proxy`, avoids handling it.

**The console reports nothing but you expect output** — `console_read_logs` reflects what the Editor console currently holds. When you suspect it has dropped something, read the file directly with `editor_log_tail`, which works even while the Editor is busy.

**A script edit does not take effect** — `AssetDatabase.Refresh()` does not reliably trigger a compile. Use `compile_request`, then check `succeeded` with `compile_status`. After a failed compile the Editor keeps running the previous assembly with `isCompiling` back to false, so silence does not mean success.

**A call returned a job id** — work slower than `syncWaitMs` (3 s by default) becomes a job. Collect it with `isuzu-unity-mcp jobs <id>`. **Do not repeat the call**: the work is still running.

## Security

- The server binds `127.0.0.1` only and **requires a bearer token on every request**.
- No CORS headers are sent. v2 returned `Access-Control-Allow-Origin: *`, which let any web page the user had open POST to `/execute_code` and run arbitrary C# in their Editor.
- **Treat the descriptor file as a credential.** Anything that can read it can run code in the Editor.
- `execute_code` and `menu_execute` run with full Editor privileges. Do not feed them untrusted code.

### It cannot ship in a build

This package runs an HTTP server that compiles and executes arbitrary C#. In the Editor that
is the point; in a shipped game it would be a remote code execution hole. Two independent
guarantees keep it out:

1. The assembly definition is `"includePlatforms": ["Editor"]`, so it is never compiled into
   a player build.
2. Every source file and binary lives under an `Editor/` folder, which Unity excludes from
   builds regardless of importer settings.

**Nothing reaches a player, Development Build included** — there is no runtime assembly at
all. If runtime functionality is ever added, gate it explicitly on `DEVELOPMENT_BUILD`.

This is a checked property rather than a remembered convention: CI asserts both on every
change, and both were verified to fail the build when violated — one by adding a script under
`Runtime/`, the other by emptying `includePlatforms`.

## What this puts on your machine

All state lives under a single root, so there is one thing to delete.

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\UnityMCP\instances\` | Descriptors for running Editors (port and token). Withdrawn on quit, and swept for dead pids on start |
| `%LOCALAPPDATA%\UnityMCP\cache\` | Cached tool catalog |
| `~/.claude/skills/isuzu-unity-mcp/` | Claude Code and Codex skill, installed by `setup` |
| The `isuzu-unity-mcp` entry in your MCP client config | Added by `setup` |

On macOS and Linux the root is under `~/.local/share` or `~/Library/Application Support`
instead. `isuzu-unity-mcp doctor` prints the real locations.

```bash
isuzu-unity-mcp uninstall         # lists what would go
isuzu-unity-mcp uninstall --yes   # removes it
```

`uninstall` takes only the `isuzu-unity-mcp` entry out of your MCP client configs and leaves every
other server alone. It refuses while an Editor is running, since that Editor would republish
its descriptor moments later. The Unity package itself is removed through the Package Manager.

## Migrating from v2

This release is deliberately breaking.

| v2 | v3 |
|---|---|
| `/command` with `console.getLogs` etc. | tools such as `console_read_logs` |
| `unity_listClients` | `unity_list_clients` |
| `/inspect` with a `mode` argument | `inspect_read` / `inspect_list` / `inspect_write` |
| `/play_mode` with an `action` argument | `play_mode_status` / `_play` / `_stop` / … |
| MCP resource `unity://assemblies` | tool `project_assemblies` |
| `unity_connectToProject` | `unity_set_active_client` |
| UDP broadcast discovery | descriptor files |
| No authentication | bearer token required |
| Handlers written in TypeScript | `[McpTool]` written in C# |

Replace curl-based procedures with `isuzu-unity-mcp call`, or route them through the MCP server's `/proxy/<project>/...`, which supplies the token for you.

## License

MIT
