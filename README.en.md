# Unity MCP Integration Framework

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-4.0.0-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-10-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語版](./README.md)

If this is your first time, start with [Getting started with Unity MCP](https://unity-mcp.shiranui-isuzu.dev/en/). It is an illustrated guide that walks through the install.

This framework opens the Unity Editor to AI agents, to people and to scripts. Running a command by hand and calling it from a script go through the same path.

The main path is the command line `isuzu-unity-cli`. The published binaries are native, so they need no Node and no .NET runtime.

MCP clients connect directly to the Streamable HTTP endpoint that the Editor itself publishes at `http://127.0.0.1:<port>/mcp`. There is no separate MCP server process.

A tool is a static C# method with `[McpTool]` on it. Every tool is served to both the CLI and MCP clients.

The port is derived from the project path, so it survives an Editor restart. Calling a tool needs a bearer token.

## Requirements

- Unity Editor 2022.3 or newer. The EditMode suite is verified on 2022.3.22f1, 6000.0.35f1 and 6000.5.10f1
- A Git client, 2.14.0 or newer, on `PATH`. Unity's Package Manager runs it to fetch a package from a git URL ([Unity manual](https://docs.unity3d.com/Manual/upm-git.html)). The VPM repository below needs none
- `com.unity.nuget.newtonsoft-json` 3.2.1. It is resolved automatically as a dependency
- The CLI needs no Node.js. A .NET SDK is needed only to install the CLI with `dotnet tool install`

On Unity 6.5 and later, `instanceId` comes back as a JSON string rather than a number. A 6.5 EntityId can exceed 2^53. A value that large no longer round-trips through a JSON number. The `instance_id` argument accepts either a string or an integer.

## Installation

In Unity's Package Manager choose **Add package from git URL** and enter:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

With the VRChat Creator Companion (VCC) or ALCOM, install from the VPM repository instead. Both download a package as a zip, so that route needs no Git.

```
https://unity-mcp.shiranui-isuzu.dev/vpm.json
```

Paste that URL into Add Repository. In VCC that button is on the Packages tab of the Settings page. In ALCOM it is on the Repositories page under Resources. Once the repository is added, Unity MCP appears in the project's package list.

The one-click add link is on the getting started guide, under [If you use VCC or ALCOM](https://unity-mcp.shiranui-isuzu.dev/en/#vpm-title).

Install the CLI:

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh

# With the .NET SDK installed
dotnet tool install -g IsuzuUnityCli
```

You can also download a binary directly from GitHub Releases. The file names are `isuzu-unity-cli-win-x64.exe`, `-osx-arm64`, `-osx-x64` and `-linux-x64`, and you can verify them against `SHA256SUMS`. Preferences > Unity MCP in the Editor has an "Install CLI" button as well.

Then install the agent skill for Claude Code and Codex:

```bash
isuzu-unity-cli setup
```

## First commands

The server starts when the Editor opens a project, and it publishes a descriptor file. The CLI reads that file, so you never type a port or a token.

```bash
isuzu-unity-cli projects                  # Editors currently running
isuzu-unity-cli health                    # server status
isuzu-unity-cli tools                     # what this Editor publishes
isuzu-unity-cli call play_mode_status     # invoke a tool
isuzu-unity-cli verify                    # recompile, collect errors, read console errors
```

`verify` gathers the recompile and the error collection that follow a script edit into one call. Add `--test` and it runs the tests as well.

With several Editors open, choose one with `--project <name>`. Inside a project directory the choice is automatic. Every command is described in the [CLI reference](docs/en/cli.md).

## MCP clients

For Claude Code, run this:

```bash
claude mcp add --transport http isuzu-unity http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"
```

The port is part of the URL that `isuzu-unity-cli doctor` prints under "Running Editors". The token is not printed there. Open the Editor's Preferences > Unity MCP page and press Copy on the Bearer token row under Connection.

To avoid handling the token yourself, let the CLI register the client for you:

```bash
isuzu-unity-cli setup --mcp --agent claude-code
```

`--agent` accepts `claude-code`, `claude-desktop`, `codex`, `cursor`, `gemini` or `vscode`.

Claude Desktop also has an extension bundle. Double-click `isuzu-unity-cli.mcpb` from [Releases](https://github.com/isuzu-shiranui/UnityMCP/releases) to install it.

Per-client snippets, the Claude Desktop stdio bridge and the protocol facts are in [Connecting MCP clients](docs/en/mcp-clients.md).

## Tools

The Editor publishes at most 88 tools. The nine Timeline entries and the two Recorder entries appear only when `com.unity.timeline` and `com.unity.recorder` are installed. `test_run` and `test_results` appear only when `com.unity.test-framework` is installed. A project with none of those packages publishes 75 tools.

The full list is in the [tool reference](docs/en/tools.md).

| Group | Contents |
|---|---|
| Diagnostics | Console, `Editor.log`, compile status, tests, scene hierarchy, serialized property and asset reads, reading and auditing Animator Controllers, screenshots, job status |
| Authoring | Create and change GameObjects, components, assets, scenes and prefabs. Edit an Animator Controller's layers, states, transitions and parameters. Invoke menu items and control Play Mode. The eight `gameobject_*` tools, `inspect_write`, `prefab_create`, `prefab_instantiate` and the ten `animator_*` editing tools collapse into one undo step |
| Rendering | Effective pipeline, camera, shader and material values, statistics for a GPU buffer or a texture, and a numeric comparison of two captures |
| Timeline / Recorder | Inspect and edit tracks and clips, evaluate at a time, add Recorder tracks. These appear only when those packages are installed |
| Build | Build settings, player builds, target switching |
| Code | Read live state by reflection, run a C# snippet. A read invokes property getters. A few Unity getters change the scene |
| Input | Synthesize mouse and key input through the Editor's GUI path, and record and replay it |

Append `?group=diagnostics,authoring` to the MCP URL and `tools/list` returns only those groups.

## Adding a tool

Write one method in the Editor.

```csharp
using System.Linq;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

internal static class MyTools
{
    [McpTool(
        "asset_find_by_type",
        "Find project assets of a given type. Prefer a narrow type and a small limit.",
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

The tool appears in `/tools` and can be called from both MCP clients and the CLI. The JSON Schema comes from the signature.

`[McpTool]` has four properties.

| Property | Default | Meaning |
|---|---|---|
| `Idempotency` | `Unsafe` | Whether the call may be retried automatically after a connection failure. Read-only tools should say `Safe` |
| `MainThread` | `true` | Whether the Editor main thread is required. `false` keeps the tool answerable while the Editor is busy. Use it only for tools that touch no Unity API |
| `Destructive` | `false` | When true, the call refuses to run without `confirm: true`. It also supports `dry_run` |
| `UndoGroup` | `null` | When set, the whole call collapses into a single Undo step |

Tool names must match `^[a-z][a-z0-9_]{0,63}$`. The description is the only cue the model has for choosing a tool. Say when to use it, not just what it does.

You can also add a tool from a JSON file without writing C#. See [Defined tools](docs/en/defined-tools.md).

## Measurements

The three paths return the same thing, and the benchmark verifies that before it times anything. If the REST `result`, the MCP `structuredContent` and the CLI's stdout disagree, it exits without sending a single timed request.

| Path | p50 per call | Editor-side heap growth per 100 calls |
|---|---|---|
| MCP, connection kept open | 2.3 ms | 1.3 MB |
| REST, connection kept open | 2.2 ms | 1.4 MB |
| CLI, one process per call | 27.0 ms | 49 MB |

The CLI builds a fresh process and a fresh TCP connection for every call. The difference in heap growth is those per-connection buffers, not the work a tool does. A kept-open connection is faster because it skips both, and what the CLI buys in exchange is that it needs no client configuration and no resident process.

One CLI call took 24.0 ms from process creation to printed output. Reaching `Main` accounted for 15.8 ms of that. The remaining 8.2 ms covers argument parsing, finding the Editor, connecting, the round trip and the output. The round trip itself was 3.4 ms. `UNITY_MCP_TRACE=1` prints this breakdown.

The conditions were a Core i9-14900KF, Windows 11 (10.0.26200), .NET 10.0.100 and Unity 6000.5.10f1, with thirty timed repeats and three warmups per path and nine Unity processes running throughout. Reproduce it with `scripts/bench-cli-vs-mcp.ps1`; what each figure measures is defined in [scripts/README.md](scripts/README.md).

## Documentation

- [Tool reference](docs/en/tools.md): all 88 tools, with the things to know before editing
- [Connecting MCP clients](docs/en/mcp-clients.md): per-client configuration, the Claude Desktop bridge, protocol facts
- [CLI reference](docs/en/cli.md): every command, project selection, exit codes, what lands on your machine
- [Defined tools](docs/en/defined-tools.md): `probe`, `script` and `sequence` tools from a JSON file
- [Synthesizing, recording and replaying Editor input](docs/en/input-tools.md): `input_pointer`, `input_key`, `input_record`, `input_replay`
- [Architecture](docs/en/architecture.md): the diagram, Editor-side classes, settings, tests
- [Troubleshooting](docs/en/troubleshooting.md)
- [Security](docs/en/security.md)
- [Migrating from v3](docs/en/migration-v3.md)
- [CHANGELOG](jp.shiranui-isuzu.unity-mcp/CHANGELOG.md)

## Security

- The server binds `127.0.0.1` only. Every request except `OPTIONS` needs a bearer token. `OPTIONS` is the CORS preflight, and it is answered 204 with no body.
- Treat the descriptor file and the token file as credentials. Anything that can read them can run code in the Editor.
- Nothing ships in a player build, Development Build included. Every source lives under `Editor/`, and the assembly definition is Editor-only. CI checks both on every change.

Details are in [Security](docs/en/security.md).

## License

MIT
