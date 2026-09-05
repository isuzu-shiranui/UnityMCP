# Migrating from v3

This page lists the names and commands a v3 user replaces when moving to v4. [Back to the README](../../README.en.md)

v4 contains breaking changes.

| v3 | v4 |
|---|---|
| `isuzu-unity-mcp <cmd>` | `isuzu-unity-cli <cmd>` |
| `npm i -g @shiranui_isuzu/unity-mcp` | an install script (`install.ps1` / `install.sh`), or `dotnet tool install -g IsuzuUnityCli` |
| `{"command":"node","args":[".../build/index.js"]}` | `{"type":"http","url":"http://127.0.0.1:<port>/mcp","headers":{"Authorization":"Bearer <token>"}}`, or `claude mcp add --transport http` |
| `target` parameter to select an Editor | one URL per project (`target` is gone) |
| `unity_list_clients` | `isuzu-unity-cli projects` |
| skill `isuzu-unity-mcp` | skill `isuzu-unity-cli` (`setup` removes the old folder) |
| Preferences npm installer window | Preferences Install button, shown while the CLI is missing |

## Steps

1. Update the package to 4.0.0 in the Package Manager.
2. Remove the v3 CLI with `npm uninstall -g @shiranui_isuzu/unity-mcp`.
3. Install `isuzu-unity-cli` as described in the [README](../../README.en.md).
4. Re-register your MCP clients with `isuzu-unity-cli setup --mcp`. Delete old entries that point at a node command by hand.
5. Replace curl-based procedures with `isuzu-unity-cli call`, or go through a registered MCP client.

## Removed APIs

- The public interfaces `IMcpCommandHandler` and `IMcpResourceHandler` are gone. Rewrite the handler as a static method with `[McpTool]` (see [Adding a tool](../../README.en.md#adding-a-tool) in the README).
- The v2 HTTP routes `/command`, `/resource`, `/read_logs`, `/execute_code`, `/browse_hierarchy`, `/capture_screenshot`, `/play_mode`, `/inspect` and `/hlsl/errors` are gone.
- The settings `clientInstallationPath` and per-handler enable/disable are gone. `/health` no longer reports `handlers[]` or `resources[]`; it reports `mcpUrl`, `preferredPort`, `portMismatch` and `toolCount` instead.
- The environment variables `MCP_DESCRIPTOR_INTERVAL`, `MCP_HEALTH_INTERVAL`, `MCP_RELOAD_RETRY_MAX_MS` and `MCP_PROJECT_API_PORT`, and the `/proxy` route, are gone.
- The `code_execute` MCP prompt is gone.

The full list of changes is in the [CHANGELOG](../../jp.shiranui-isuzu.unity-mcp/CHANGELOG.md).
