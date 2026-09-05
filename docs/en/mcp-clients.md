# Connecting MCP clients

This page gives the configuration for each MCP client and the protocol facts about the endpoint the Editor publishes. [Back to the README](../../README.en.md)

The Editor publishes MCP at `http://127.0.0.1:<port>/mcp` over Streamable HTTP. There is no separate server to run. The port and token live in the Editor's descriptor.

`isuzu-unity-cli doctor` prints the real URL under "Running Editors", and the port is part of that URL. It never prints the token. When you need the token, open the Editor's Preferences > Unity MCP page and press Copy on the Bearer token row under Connection.

## Automatic registration

```bash
isuzu-unity-cli setup --mcp --agent claude-code --scope project
```

`--agent` accepts `claude-code`, `claude-desktop`, `codex`, `cursor`, `gemini` or `vscode`. `claude-desktop` works on Windows and macOS only, since Claude Desktop does not ship for Linux.

`--scope user|project` applies to Claude Code. Project scope writes `.mcp.json` with `${UNITY_MCP_TOKEN}`, never a raw token.

`--no-skill` skips the skill install. `--project <name>` targets a specific project. A running Editor is required.

While the CLI is not on PATH, the Editor's Preferences > Unity MCP page shows an Install button. It opens a terminal and runs the install script. It installs the CLI itself. It does not register a client, and it disappears once the CLI is found.

## Per-client configuration

The blocks below show where each client expects the entry and what shape it takes. For one with the real values already in it, pick a client on the Editor's Preferences > Unity MCP page and use Show or Copy.

Six configurations are offered. Three of them are the Claude Code command, the `.mcp.json` shared by Cursor and Claude Code, and the Codex `config.toml`. The other three are the Gemini CLI `settings.json`, the VS Code `.vscode/mcp.json` and the Claude Desktop stdio bridge.

Four of the six carry the URL and the token. The VS Code one carries a prompt reference instead, because `.vscode/mcp.json` usually lives in the repository. The Claude Desktop one uses no token, only the path to the CLI and the project name.

Claude Code:

```bash
claude mcp add --transport http isuzu-unity http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"
```

Cursor (`~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "isuzu-unity": {
      "url": "http://127.0.0.1:<port>/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.isuzu-unity]
url = "http://127.0.0.1:<port>/mcp"
http_headers = { Authorization = "Bearer <token>" }
```

Gemini CLI (`~/.gemini/settings.json`) uses `httpUrl` in place of `url`.

VS Code (`.vscode/mcp.json`), root key `servers`:

```json
{
  "servers": {
    "isuzu-unity": {
      "type": "http",
      "url": "http://127.0.0.1:<port>/mcp",
      "headers": { "Authorization": "Bearer ${input:isuzu-unity-token}" }
    }
  },
  "inputs": [
    {
      "id": "isuzu-unity-token",
      "type": "promptString",
      "description": "Unity MCP bearer token",
      "password": true
    }
  ]
}
```

## Claude Desktop

Claude Desktop installs from the extension bundle `isuzu-unity-cli.mcpb`. Download it from [Releases](https://github.com/isuzu-shiranui/UnityMCP/releases) and double-click it.

Dragging it onto the Claude Desktop window does the same. So does choosing it under Settings > Extensions > Advanced settings > Install Extension.

The installer asks for a project name. You can leave it empty when only one Unity project is open. With several open, enter the name shown in the Editor title bar.

The Product Name from Player Settings works as well, and `isuzu-unity-cli projects` prints these names. The Unity Editor must be open with the package installed while you use it.

The bundle carries the Windows, macOS (Apple Silicon and Intel) and Linux executables. It needs no other runtime. The manifest declares `win32`, `darwin` and `linux` as supported platforms. Three points are worth knowing.

- The bundle is self-signed. Claude Desktop logs a warning about an unsigned extension when it installs. The extension works regardless.
- Cowork cannot use local extensions.
- The Microsoft Store build of Claude Desktop has a known issue where the .mcpb preview closes. If that happens, extract the .mcpb as a zip and point Settings > Extensions > Advanced settings > Install Unpacked Extension at the folder.

To configure it by hand instead, write a stdio bridge into `claude_desktop_config.json`. Claude Desktop cannot connect to a local HTTP endpoint directly.

```json
{
  "mcpServers": {
    "isuzu-unity": {
      "command": "<path to isuzu-unity-cli>",
      "args": ["mcp-stdio", "--project", "<project name>"]
    }
  }
}
```

## ChatGPT

ChatGPT's normal chat accepts only MCP servers published over HTTPS on the internet. It cannot reach a local Editor directly.

Codex users register the stdio bridge in one line:

```bash
codex mcp add isuzu-unity -- isuzu-unity-cli mcp-stdio --project <project name>
```

Codex also connects to Streamable HTTP directly. `isuzu-unity-cli setup --mcp --agent codex` writes the Codex configuration above, so that route works as well.

For the normal chat, OpenAI's [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) is the supported path. Run [openai/tunnel-client](https://github.com/openai/tunnel-client) with `--mcp-command` pointing at the stdio bridge, which is `isuzu-unity-cli mcp-stdio --project <project name>`. Then attach the tunnel to your workspace on the OpenAI Platform.

There are third-party tunnels as well, such as ngrok or cloudflared. Exposing the Editor endpoint that way is not recommended without authentication in front of it. The endpoint executes C# inside Unity.

## Protocol facts

- The endpoint is stateless and has no session id.
- Protocol revisions 2025-11-25, 2025-06-18 and 2025-03-26 are supported.
- `tools/list` carries annotations. Safe tools carry `readOnlyHint` and `idempotentHint`, and destructive tools carry `destructiveHint`.
- `tools/call` returns `structuredContent` alongside text. A tool-level error comes back as an `isError` result the model can read, not as a transport error.
- GET and DELETE answer 405. A request with a foreign `Origin` answers 403.
- There is no `tools/list_changed`. If a package change or a defined-tool change adds or removes tools, reconnect the client.
- Append `?group=diagnostics,authoring` to the MCP URL and `tools/list` returns only those groups. Calls themselves are never filtered.
