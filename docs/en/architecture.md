# Architecture

This page shows how the CLI and MCP clients reach the Editor, the main Editor-side classes, the settings, and how to run the tests. [Back to the README](../../README.en.md)

## Overview

```
AI agent                              MCP client                     Claude Desktop
    │                                     │                                │
    ▼                                     │ Streamable HTTP                │ stdio
isuzu-unity-cli (CLI)                     │                                ▼
    │                                     │                          mcp-stdio bridge
    │  read descriptor                    │                                │
    │  <port + token>                     │                                │
    ▼                                     ▼                                │
Unity Editor  :27200-27999  (HttpListener, 127.0.0.1 only)  ◀──────────────┘
    │
    ├── GET  /tools              catalog generated from attributes
    ├── POST /tools/<name>       invoke a tool
    ├── POST /mcp                MCP over Streamable HTTP
    ├── GET  /health             state, queue depth, running jobs
    ├── GET  /jobs, /jobs/<id>   track long-running work
    └── POST /jobs/<id>/cancel   abandon a job that has not started
```

- Tools are defined once, in the Editor. Put `[McpTool]` on a static C# method and its JSON Schema is derived from the signature. The generated tool is served to both the CLI and MCP clients.
- MCP is served by the Editor itself. There is no separate server process.
- Tools declaring `MainThread = false`, plus `/health`, `/jobs` and `/tools`, are served from a worker thread. They answer while the Editor main thread is blocked.
- A call that does not finish in a few seconds returns a job id. It never returns a timeout while the work keeps running in the background. The `job_status` tool reports the result.
- The port is derived from the project path into 27200-27999, so the URL is stable across restarts.
- Every request needs a bearer token.

## Editor side (C#)

`ToolCatalog` collects `[McpTool]` static methods by reflection and derives each JSON Schema from the signature. `ToolInvoker` binds JSON arguments to typed parameters. It also applies the `confirm` and `dry_run` gates and the Undo grouping the attribute declares.

The MCP endpoint and the CLI's `/tools/<name>` both go through the same `ToolCatalog` and `ToolInvoker`.

`McpMainThreadDispatcher` marshals work from worker threads onto the Editor main thread. It holds the queue lock only while dequeuing and runs the work outside the lock. One slow call therefore cannot stall other requests, and a job that has not started can be cancelled reliably.

An unfocused Editor runs its main loop about every 100 ms. The server wakes it while a request is waiting, so calls normally complete in a few milliseconds. `/health` reports `loopWaker` as `on-demand`, `always` or `unavailable`.

## Settings (Preferences > Unity MCP)

These settings live in Unity's preferences folder and are shared by every Unity project on the machine. They are not stored per project. A positive `httpPort` therefore makes every other project try that port too. The defaults below are what a machine that has never saved them starts with.


| Setting | Default | Meaning |
|---|---|---|
| `httpPort` | 0 | 0 derives the port from the project path. A positive value pins it to that port, and clients have to be re-registered after the change |
| `autoStartOnLaunch` | true | Start the server when the Editor launches |
| `syncWaitMs` | 3000 | Work slower than this returns a job id |
| `detailedLogs` | false | Log each request and each start and stop step to the Unity console. Those lines also come back through `console_read_logs`, so they are off by default. Warnings and errors are logged either way |
| `keepEditorAwake` | false | An unfocused Editor runs its main loop about every 100 ms. The server wakes it while a request is waiting. This setting keeps the Editor awake for the whole session instead, at the cost of idle CPU |
| `uiLanguage` | 0 | The language of the Preferences page. 0 follows the Editor, 1 is English, 2 is Japanese. Tool descriptions and CLI output stay in English |

## Tests

```bash
# CLI
cd isuzu-unity-cli && dotnet test

# Unity (headless)
Unity.exe -batchmode -nographics -projectPath <project> \
  -runTests -testPlatform EditMode -testResults results.xml
```

A running Editor can also be driven from the CLI or an MCP client.

```bash
isuzu-unity-cli call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-cli call test_results          # readable while the run is in progress
isuzu-unity-cli call test_results --include_passed true --limit 200
```

A test run holds the main thread for its whole duration, so no other tool answers during it. `test_results` is declared `MainThread = false`, so it still reports counts and failures. `test_run` returns as soon as the run is queued and does not wait for the outcome.

Running the package's tests needs `"testables": ["jp.shiranui-isuzu.unity-mcp"]` in the project's `Packages/manifest.json`.

The EditMode suite is verified on 2022.3.22f1, 6000.0.35f1 and 6000.5.10f1.
