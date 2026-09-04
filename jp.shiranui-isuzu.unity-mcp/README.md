# Unity MCP

This Unity Editor package exposes the Editor to AI agents over the Model Context Protocol (MCP). It also exposes the Editor to people and scripts over a command line. Agents can read the console, browse and edit the scene, run tests, drive Timeline and Recorder, capture screenshots, and run C# snippets.

The full documentation lives in the repository. The README is at
https://github.com/isuzu-shiranui/UnityMCP. The pages under
https://github.com/isuzu-shiranui/UnityMCP/tree/main/docs/en cover the tool list, the CLI
reference, MCP client configuration and troubleshooting.

## Requirements

- Unity 2022.3 or later. The EditMode suite is verified on 2022.3.22f1, 6000.0.35f1 and 6000.5.10f1.
- A Git client, 2.14.0 or newer, on `PATH`. Unity's Package Manager runs it to fetch a package from a git URL.
- `com.unity.nuget.newtonsoft-json` 3.2.1. It is resolved automatically as a dependency.
- Node.js is not required. The downloaded CLI is a native binary and needs no runtime. The `dotnet tool` package instead runs on the .NET 10 SDK you install it with.

## Installation

In the Package Manager choose **Add package from git URL** and enter:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

Then install the CLI:

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh
```

or, with the .NET SDK installed:

```bash
dotnet tool install -g IsuzuUnityCli
```

Preferences > Unity MCP also has an "Install CLI" button. It runs the install script for you and shows the MCP connection details.

## How it works

When the Editor opens a project, the package starts an HTTP server on `127.0.0.1`. It writes a descriptor file that holds the port and a bearer token. The port is derived from the project path, so it stays the same across restarts. Preferences can pin it to a fixed value instead.

The CLI reads the descriptor to find the Editor and to authenticate to it. Nothing needs to be started by hand.

MCP clients connect directly to the Editor over Streamable HTTP at `http://127.0.0.1:<port>/mcp`. The request carries an `Authorization: Bearer <token>` header. There is no separate MCP server process.

Claude Desktop cannot reach a local HTTP endpoint directly. It connects through a stdio bridge instead, with `isuzu-unity-cli mcp-stdio --project <name>`.

Tools are defined once, in C#. A static method with `[McpTool]` becomes a tool. Its JSON Schema is generated from the method signature. The schema is published at `GET /tools` and over MCP's `tools/list`.

```csharp
[McpTool("asset_find_by_type", "Find project assets of a given type.", Idempotency = McpIdempotency.Safe)]
public static string[] FindByType(
    [McpArg("type", "Unity type name, e.g. Material.")] string type,
    [McpArg("limit", "Maximum paths to return.")] int limit = 50)
{
    return AssetDatabase.FindAssets($"t:{type}").Take(limit).Select(AssetDatabase.GUIDToAssetPath).ToArray();
}
```

Tools that declare `MainThread = false` answer from a worker thread, and so do `/health`, `/jobs` and `/tools`. They keep working while the Editor main thread is blocked.

A call that takes longer than `syncWaitMs`, three seconds by default, returns a job id instead of a timeout. The `job_status` tool reports the result once it is ready. `job_status` is itself safe.

Timeline tools appear only when `com.unity.timeline` is installed. Recorder tools appear only when both `com.unity.recorder` and `com.unity.timeline` are installed. `test_run` and `test_results` appear only when `com.unity.test-framework` is installed.

## Settings

**Edit > Preferences > Unity MCP**

These live in Unity's preferences folder. Every project on the machine shares them. A positive `httpPort` makes every other project try that port too.

| Setting | Default | Meaning |
|---|---|---|
| `httpPort` | 0 | 0 derives the port from the project path. A positive value pins it |
| `autoStartOnLaunch` | true | Start the server when the Editor opens |
| `syncWaitMs` | 3000 | Calls slower than this return a job id |
| `detailedLogs` | false | Log each request. The lines also reach `console_read_logs`, so this is off by default |
| `keepEditorAwake` | false | An unfocused Editor ticks about every 100 ms. The server wakes it while a request is waiting. This setting keeps the Editor awake for the whole session instead, at the cost of idle CPU |

## Security

- The server binds to `127.0.0.1` only. Every request that reaches a tool needs a bearer token. A CORS preflight `OPTIONS` is answered before the check and returns no data.
- No CORS headers are sent. A web page open in a browser cannot call the server.
- Treat the descriptor file and the token file as credentials. Anyone who can read them can run code inside the Editor. The token is stored under `%LOCALAPPDATA%\UnityMCP\tokens\`, and under `~/.local/share/UnityMCP/tokens/` with owner-only permissions on macOS and Linux. Preferences can regenerate it.
- `execute_code` and `menu_execute` run with the Editor's full permissions. Do not feed them untrusted code.

Nothing in this package reaches a player build, Development Build included. All sources are under `Editor/`, and every assembly definition is restricted to the Editor platform. CI checks both on every change.

## Tests

The EditMode suite lives inside the package. To run it, add the package to `testables` in the project's `Packages/manifest.json`:

```json
"testables": ["jp.shiranui-isuzu.unity-mcp"]
```

## License

MIT
