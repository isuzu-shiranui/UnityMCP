# Unity MCP Integration Framework

<!-- mcp-name: dev.shiranui-isuzu/unity-mcp -->

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-10-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語版](./README.md)

This framework opens the Unity Editor to AI agents. Running a command by hand and calling it from a script go through the same path.

- MCP clients connect directly to the Streamable HTTP endpoint that the Editor itself publishes at `http://127.0.0.1:<port>/mcp`. There is no separate MCP server process. Checked with Claude Code, Cursor, Codex, the Gemini CLI, VS Code and Claude Desktop.
- The command line `isuzu-unity-cli` calls the same tools. The published binaries are native, so they need no Node and no .NET runtime.
- A tool is a static C# method with `[McpTool]` on it.

If this is your first time, start with [Getting started with Unity MCP](https://unity-mcp.shiranui-isuzu.dev/en/), an illustrated guide that walks through the installation.

## Requirements

- Unity Editor 2022.3 or newer. The EditMode test suite is run on Unity 6000.0.35f1
- A Git client, 2.14.0 or newer, on `PATH`. Unity's Package Manager runs it to fetch a package from a git URL ([Unity manual](https://docs.unity3d.com/Manual/upm-git.html)). The VPM repository below needs none
- `com.unity.nuget.newtonsoft-json` 3.2.1. It is resolved automatically as a dependency

## Installation

In Unity's Package Manager choose **Add package from git URL** and enter:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

With the VRChat Creator Companion (VCC) or ALCOM, add the VPM repository `https://unity-mcp.shiranui-isuzu.dev/vpm.json` instead. Both download a package as a zip, so that route needs no Git. The one-click add link, and where to find the button in each app, are on the getting started guide under [If you use VCC or ALCOM](https://unity-mcp.shiranui-isuzu.dev/en/#vpm-title).

Install the CLI:

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh
```

You can also download a binary from GitHub Releases and verify it against `SHA256SUMS`. With the .NET SDK installed, `dotnet tool install -g IsuzuUnityCli` works too.

Then install the agent skill and register an MCP client:

```bash
isuzu-unity-cli setup                              # the skill for Claude Code and Codex
isuzu-unity-cli setup --mcp --agent claude-code    # register an MCP client
```

`--agent` accepts `claude-code`, `claude-desktop`, `codex`, `cursor`, `gemini` or `vscode`. Claude Code files the server under the Unity project's path, so start Claude Code in the Unity project folder. Preferences > Unity MCP in the Editor can register a client as well.

Per-client configuration, the Claude Desktop extension bundle and the stdio bridge are in [Connecting MCP clients](docs/en/mcp-clients.md).

## First commands

The server starts when the Editor opens a project, and it publishes a descriptor file. The CLI reads that file, so you never type a port or a token.

```bash
isuzu-unity-cli projects                  # Editors currently running
isuzu-unity-cli tools                     # what this Editor publishes
isuzu-unity-cli call play_mode_status     # invoke a tool
isuzu-unity-cli verify                    # recompile, collect errors, read console errors
```

`verify` gathers the recompile and the error collection that follow a script edit into one call. Add `--test` and it runs the tests as well.

With several Editors open, choose one with `--project <name>`. Inside a project directory the choice is automatic. Every command is described in the [CLI reference](docs/en/cli.md).

## Tools

There are tools for diagnostics (console, `Editor.log`, compile status, tests, scene hierarchy, asset reads), authoring (create and change GameObjects, components, assets, scenes, prefabs and Animator Controllers), rendering, Timeline and Recorder, builds, running a C# snippet, and synthesizing Editor input. An authoring call collapses into a single undo step.

The Timeline tools appear only with `com.unity.timeline`, the Recorder tools only with both `com.unity.recorder` and `com.unity.timeline`, and `test_run` and `test_results` only with `com.unity.test-framework`.

The full list, with the things to know before editing, is in the [tool reference](docs/en/tools.md). Append `?group=diagnostics,authoring` to the MCP URL and `tools/list` returns only those groups.

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

That is all it takes to call it from both MCP clients and the CLI. The JSON Schema comes from the signature. The properties `[McpTool]` accepts are listed in [Architecture](docs/en/architecture.md).

You can also add a tool from a JSON file without writing C#. See [Defined tools](docs/en/defined-tools.md).

## Documentation

- [Tool reference](docs/en/tools.md): every tool, with the things to know before editing
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

The server binds to `127.0.0.1` only, and every request except `OPTIONS` needs a bearer token. Treat the descriptor file and the token file as credentials: anything that can read them can run code in the Editor. Nothing ships in a player build, Development Build included. Details are in [Security](docs/en/security.md).

## License

MIT
