# @shiranui_isuzu/unity-mcp

The MCP server and CLI for [UnityMCP](https://github.com/isuzu-shiranui/UnityMCP).

This package has no knowledge of individual tools. The Editor publishes them at `GET /tools`
with a JSON Schema generated from its C# signatures. This server forwards that catalog. Adding
a tool is a change in the Unity package alone.

## Architecture

```
MCP client (Claude)                        terminal / scripts
        │ stdio                                    │
        ▼                                          │
  build/index.js                              build/cli.js
  (MCP server)                                (isuzu-unity-mcp)
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
npm link          # provides the isuzu-unity-mcp command

isuzu-unity-mcp setup   # register with installed agents and install the skill
```

`setup` writes to the MCP config of every supported agent it finds: Claude Code, Claude
Desktop, Codex, Cursor, and Gemini CLI. It installs the skill for those that have a skills
directory. It updates only configs that already exist. It does not create a config for a tool
that is not installed. It rewrites configs key by key, so other servers survive. Pass
`--agent <name>` to pick one agent, or `--no-skill` to skip skills.

```bash
isuzu-unity-mcp doctor          # what is installed, where, and what is stale
isuzu-unity-mcp uninstall       # lists what it would remove
isuzu-unity-mcp uninstall --yes # removes it
```

`uninstall` removes only the `isuzu-unity-mcp` entry from each agent config. It refuses to run
while an Editor is running, because that Editor would republish its descriptor right after.

## Usage

### As an MCP server

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

`isuzu-unity-mcp serve` starts the same server, so one binary covers both roles.

The Editor does not need to be running at startup. Until one appears, the server answers
`tools/list` from its cached catalog under the state root. Once it has a live catalog, it sends
`tools/list_changed`. Clients ask for the tool list as soon as they connect, which is often before
any Editor is open. The cached catalog lets the client see the tools in that case.

### As a CLI

```bash
isuzu-unity-mcp projects
isuzu-unity-mcp tools
isuzu-unity-mcp health
isuzu-unity-mcp jobs [id]

isuzu-unity-mcp call <tool> --json '{"key":"value"}'
isuzu-unity-mcp call <tool> --name value --other 3
isuzu-unity-mcp call execute_code --file snippet.cs

isuzu-unity-mcp call <tool> --project MyGame   # when several Editors are open
isuzu-unity-mcp call <tool> --raw              # print the whole envelope
```

The CLI talks to the Editor directly, not through this server. It works with no MCP client
running. Errors print to stderr and set a non-zero exit code.

`--file` sends `execute_code` snippets base64-encoded. Passing C# through a shell and a JSON
encoder loses the backslashes in its string literals. The failure then appears as a compile
error in generated source that the caller never sees.

### As an HTTP proxy

While the MCP server is running it exposes a small API for HTTP clients:

```bash
curl http://127.0.0.1:27180/projects
curl -X POST http://127.0.0.1:27180/proxy/MyProject/tools/play_mode_status \
  -H 'Content-Type: application/json' -d '{}'
```

The proxy supplies the bearer token, so requests through it need no credential handling. It
exists only while an MCP client has this server running. For a standalone path, use the CLI.

## Multi-instance behaviour

Every Editor publishes its own descriptor and binds the first free port from 27182, so
several can run at once.

With more than one running, the target is resolved in this order:

1. An explicit `target` (MCP) or `--project` (CLI). An exact project name or clientId wins
   over a substring match. An ambiguous substring is refused and the candidates are listed.
   Picking one silently could send a write to the wrong project.
2. The active client, if `unity_set_active_client` was called.
3. **The project containing the working directory.** A shell inside a project, or an MCP
   client opened in one, already identifies the project. Nested projects resolve to the
   deepest containing root.
4. Otherwise the call is refused and the candidates are listed.

`isuzu-unity-mcp projects` marks the entry step 3 would choose with `containsWorkingDirectory`.

Descriptors are checked for a live pid, so an Editor that crashed instead of quitting does not
linger as a phantom instance. A withdrawn descriptor unregisters its instance immediately,
without waiting for a health poll.

## Reload resilience

A domain reload takes the Editor's HTTP server down for a few seconds. The instance moves to
`reloading` and is not dropped. Requests retry within `MCP_RELOAD_RETRY_MAX_MS`. The Editor
keeps its descriptor and token across the reload, so the reconnect needs no new credential.

## Retry safety

Each tool declares its own idempotency in the catalog. Only `safe` calls are retried after a
post-handshake failure. Retrying an `unsafe` call could apply its side effect twice.

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

CI runs all four on every pull request. It also checks that the two packages agree on their
version, and that the protocol version the Editor advertises matches its package.

## Migrating from v2

The handler system is gone. `src/handlers/`, `HandlerAdapter`, `HandlerDiscovery`, the
registries and the `Base*Handler` classes have been removed. They were a second copy of
definitions the Editor already owned, and the two copies drifted apart. If you wrote a
TypeScript handler, rewrite it as an `[McpTool]` method in C#. It is then reachable from MCP
clients and the CLI alike.

MCP resources are removed. Their TypeScript implementations posted to an endpoint the Editor
never registered, so they never worked. The tools `project_assemblies` and `project_packages`
cover the same ground.
