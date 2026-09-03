# Unity MCP

A Unity Editor package that exposes the Editor to AI agents over the Model Context Protocol (MCP), and to people and scripts over a command line. Agents can read the console, browse and edit the scene, run tests, drive Timeline and Recorder, capture screenshots, and run C# snippets.

The full documentation, including the tool list, lives in the repository README:
https://github.com/isuzu-shiranui/UnityMCP

## Requirements

- Unity 2022.3 or later. The EditMode suite is verified on 2022.3, 2023.1, 2023.2, 6.0, 6.1, 6.3 and 6.5.
- `com.unity.nuget.newtonsoft-json` 3.2.1. It is resolved automatically as a dependency.
- Node.js 18 or later, for the MCP server and the CLI.

## Installation

In the Package Manager choose **Add package from git URL** and enter:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

Then install the MCP server and CLI from npm:

```bash
npm install -g @shiranui_isuzu/unity-mcp
isuzu-unity-mcp setup     # registers the server with the MCP clients found on this machine
isuzu-unity-mcp doctor    # shows what is installed and where
```

## How it works

When the Editor opens a project, the package starts an HTTP server on `127.0.0.1` (port 27182, moving up to 27199 if busy) and writes a descriptor file with the port and a bearer token. The MCP server and the CLI read that file to find and authenticate to the Editor. Nothing needs to be started by hand.

Tools are defined once, in C#. A static method with `[McpTool]` becomes a tool. Its JSON Schema is generated from the method signature and published at `GET /tools`. The MCP server forwards that catalog, so adding a tool never touches TypeScript.

```csharp
[McpTool("asset_find_by_type", "Find project assets of a given type.", Idempotency = McpIdempotency.Safe)]
public static string[] FindByType(
    [McpArg("type", "Unity type name, e.g. Material.")] string type,
    [McpArg("limit", "Maximum paths to return.")] int limit = 50)
{
    return AssetDatabase.FindAssets($"t:{type}").Take(limit).Select(AssetDatabase.GUIDToAssetPath).ToArray();
}
```

Tools that declare `MainThread = false`, together with `/health`, `/jobs` and `/tools`, answer from a worker thread. They keep working while the Editor main thread is blocked. A call that takes longer than `syncWaitMs` (3 seconds by default) returns a job id instead of a timeout. Poll the job for the result instead of repeating the call.

Timeline tools appear only when `com.unity.timeline` is installed. Recorder tools appear only when both `com.unity.recorder` and `com.unity.timeline` are installed. `test_run` and `test_results` appear only when `com.unity.test-framework` is installed.

## Settings

**Edit > Preferences > Unity MCP**

| Setting | Default | Meaning |
|---|---|---|
| `httpPort` | 27182 | First port to try. Moves up to 27199 if busy |
| `autoStartOnLaunch` | true | Start the server when the Editor opens |
| `syncWaitMs` | 3000 | Calls slower than this return a job id |
| `detailedLogs` | true | Log each request |

## Security

- The server binds to `127.0.0.1` only and requires a bearer token on every request.
- No CORS headers are sent. A web page open in a browser cannot call the server.
- Treat the descriptor file as a credential. Anyone who can read it can run code inside the Editor.
- `execute_code` and `menu_execute` run with the Editor's full permissions. Do not feed them untrusted code.

Nothing in this package reaches a player build, Development Build included. All sources are under `Editor/` and every assembly definition is restricted to the Editor platform. CI checks both on every change.

## Tests

The EditMode suite lives inside the package. To run it, add the package to `testables` in the project's `Packages/manifest.json`:

```json
"testables": ["jp.shiranui-isuzu.unity-mcp"]
```

## License

MIT
