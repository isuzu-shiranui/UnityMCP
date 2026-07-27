# unity-mcp-ts

The MCP server and CLI for [UnityMCP](https://github.com/isuzu-shiranui/UnityMCP).

This package deliberately knows nothing about individual tools. The Editor publishes them at
`GET /tools` with a JSON Schema generated from its C# signatures, and this server forwards
that catalog. Adding a tool is a change in the Unity package alone.

## Architecture

```
MCP client (Claude)                        terminal / scripts
        │ stdio                                    │
        ▼                                          │
  build/index.js                              build/cli.js
  (MCP server)                                (unity-mcp)
        │                                          │
        │  read descriptor ────────────────────────┤
        │  <port + token>                          │
        ▼                                          ▼
  Unity Editor  :27182-27199  (127.0.0.1 only, bearer auth)
```

| Module | Role |
|---|---|
| `core/InstanceDescriptors.ts` | Finds running Editors by reading their descriptor files |
| `core/ToolCatalogClient.ts` | Fetches `/tools`, caches it to disk |
| `core/ToolRouter.ts` | Serves `tools/list` and `tools/call` |
| `core/UnityConnection.ts` | HTTP transport, instance resolution, envelope unwrapping |
| `core/ProjectRegistry.ts` | Descriptor sweep, health polling, instance state machine |
| `core/ProjectApi.ts` | `/projects` and `/proxy/:name/*` for HTTP clients |
| `core/CliArgs.ts` | CLI parsing, kept out of the entry point so it is testable |

## Requirements

- Node.js 18 or newer
- A Unity Editor running the `jp.shiranui-isuzu.unity-mcp` package

## Installation

```bash
npm install
npm run build
npm link     # provides unity-mcp and unity-mcp-server
```

## Usage

### As an MCP server

```json
{
  "mcpServers": {
    "unity-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-mcp-ts/build/index.js"]
    }
  }
}
```

`unity-mcp serve` starts the same server, so one binary covers both roles.

The Editor need not be running at startup. Until one appears the server answers `tools/list`
from `~/.unity-mcp/tool-catalog.json`, then sends `tools/list_changed` once it has a live
catalog. Clients ask for the tool list the moment they connect, which is routinely before any
Editor is open; answering from the last known catalog beats answering "no tools".

### As a CLI

```bash
unity-mcp projects
unity-mcp tools
unity-mcp health
unity-mcp jobs [id]

unity-mcp call <tool> --json '{"key":"value"}'
unity-mcp call <tool> --name value --other 3
unity-mcp call execute_code --file snippet.cs

unity-mcp call <tool> --project MyGame   # when several Editors are open
unity-mcp call <tool> --raw              # print the whole envelope
```

The CLI talks to the Editor directly rather than through this server, so it works with no MCP
client running. Errors print to stderr and set a non-zero exit code.

`--file` sends `execute_code` snippets base64-encoded. Passing C# through a shell and a JSON
encoder loses the backslashes in its string literals, and the failure surfaces as a compile
error in generated source the caller never sees.

### As an HTTP proxy

While the MCP server is running it exposes a small API for HTTP clients:

```bash
curl http://127.0.0.1:27180/projects
curl -X POST http://127.0.0.1:27180/proxy/MyProject/tools/play_mode_status \
  -H 'Content-Type: application/json' -d '{}'
```

The proxy supplies the bearer token, so requests through it need no credential handling. It
only exists while an MCP client has this server running — for a standalone path, use the CLI.

## Multi-instance behaviour

Every Editor publishes its own descriptor. When more than one is running, a call must say
which to use, either per call with `target` (MCP) / `--project` (CLI), or once with
`unity_set_active_client`.

Descriptors are checked for a live pid, so an Editor that crashed rather than quit cannot
linger as a phantom instance. A withdrawn descriptor unregisters its instance immediately: a
clean shutdown is a more definite signal than any health poll result.

## Reload resilience

A domain reload takes the Editor's HTTP server down for a few seconds. The instance moves to
`reloading` rather than being dropped, requests retry within `MCP_RELOAD_RETRY_MAX_MS`, and
the Editor keeps its descriptor and token across the reload so the reconnect needs no new
credential.

## Retry safety

Each tool declares its own idempotency in the catalog, and only `safe` calls are retried after
a post-handshake failure. Retrying an `unsafe` call could apply its side effect twice.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `MCP_DESCRIPTOR_INTERVAL` | 2000 | Descriptor directory sweep interval (ms) |
| `MCP_HEALTH_INTERVAL` | 10000 | `/health` poll interval (ms) |
| `MCP_UNHEALTHY_COOLDOWN_MS` | 60000 | How long a `reloading` instance waits before it is called unhealthy |
| `MCP_RELOAD_RETRY_MAX_MS` | 15000 | Retry budget while a reload is in flight (ms) |
| `MCP_PROJECT_API_PORT` | 27180 | Preferred ProjectApi port |
| `MCP_PROJECT_API_PORT_END` | 27189 | Last port ProjectApi will try |

## Error codes

| Code | Meaning |
|---|---|
| `no_instance` | No Editor is registered |
| `target_required` | Several Editors are registered and none was chosen |
| `target_not_found` | No Editor matches the given target |
| `unauthorized` | Missing or wrong bearer token |
| `tool_not_found` | No such tool; `GET /tools` lists them |
| `invalid_params` | Arguments failed to bind, or a value was rejected |
| `confirmation_required` | A destructive tool was called without `confirm: true` |
| `timeout` | The retry budget ran out |

## Development

```bash
npm test          # jest
npx tsc --noEmit  # types
npm run lint      # eslint
npm run build     # tsc
```

CI runs all four on every pull request, and additionally checks that the two packages agree on
their version and that the protocol version the Editor advertises matches its package.

## Migrating from v2

The handler system is gone: `src/handlers/`, `HandlerAdapter`, `HandlerDiscovery`, the
registries and the `Base*Handler` classes were all a second copy of definitions the Editor
already owned, and the two drifted. If you had written a TypeScript handler, rewrite it as an
`[McpTool]` method in C#; it will then be reachable from MCP clients and the CLI alike.

MCP resources are withdrawn. Their TypeScript implementations posted to an endpoint the Editor
never registered, so they had never worked; `project_assemblies` and `project_packages` cover
the same ground as tools.
