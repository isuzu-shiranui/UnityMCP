using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// The text a user pastes into an MCP client to reach this Editor, per client.
    /// </summary>
    /// <remarks>
    /// Pure functions of the URL and token so the Settings window can render them and a test
    /// can check they parse. The CLI's <c>setup --mcp</c> writes the same shapes.
    /// </remarks>
    internal static class McpClientConfigSnippets
    {
        public const string ServerName = "isuzu-unity";

        /// <summary>The id VS Code prompts under; the CLI writes the same one.</summary>
        private const string TokenInputId = "isuzu-unity-token";

        /// <summary>`claude mcp add` invocation for Claude Code.</summary>
        public static string ClaudeCodeCommand(string mcpUrl, string token) =>
            $"claude mcp add --transport http {ServerName} {mcpUrl} --header \"Authorization: Bearer {token}\"";

        /// <summary>
        /// JSON for Cursor (`~/.cursor/mcp.json`) and Claude Code's `.mcp.json`. Cursor infers the
        /// transport from `url` and rejects some values of `type`, so the key is left out.
        /// </summary>
        public static string CursorJson(string mcpUrl, string token)
        {
            var root = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    [ServerName] = new JObject
                    {
                        ["url"] = mcpUrl,
                        ["headers"] = new JObject { ["Authorization"] = $"Bearer {token}" },
                    },
                },
            };

            return root.ToString(Formatting.Indented);
        }

        /// <summary>JSON for Gemini CLI (`~/.gemini/settings.json`), which names the key `httpUrl`.</summary>
        public static string GeminiJson(string mcpUrl, string token)
        {
            var root = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    [ServerName] = new JObject
                    {
                        ["httpUrl"] = mcpUrl,
                        ["headers"] = new JObject { ["Authorization"] = $"Bearer {token}" },
                    },
                },
            };

            return root.ToString(Formatting.Indented);
        }

        /// <summary>TOML block for Codex (`~/.codex/config.toml`).</summary>
        public static string CodexToml(string mcpUrl, string token) =>
            $"[mcp_servers.{ServerName}]\n" +
            $"url = {JsonConvert.ToString(mcpUrl)}\n" +
            $"http_headers = {{ Authorization = {JsonConvert.ToString($"Bearer {token}")} }}\n";

        /// <summary>
        /// JSON for VS Code (`.vscode/mcp.json`), whose root key is `servers`. That file usually
        /// lives in the repository, so the token is a prompt rather than a literal: anyone who can
        /// read it could otherwise run code inside the Editor.
        /// </summary>
        public static string VsCodeJson(string mcpUrl, string token)
        {
            var root = new JObject
            {
                ["servers"] = new JObject
                {
                    [ServerName] = new JObject
                    {
                        ["type"] = "http",
                        ["url"] = mcpUrl,
                        ["headers"] = new JObject { ["Authorization"] = $"Bearer ${{input:{TokenInputId}}}" },
                    },
                },
                ["inputs"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = TokenInputId,
                        ["type"] = "promptString",
                        ["description"] = "Unity MCP bearer token",
                        ["password"] = true,
                    },
                },
            };

            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Claude Desktop cannot open a local HTTP server, so it launches the CLI's stdio bridge.
        /// </summary>
        public static string ClaudeDesktopJson(string cliPath, string projectName)
        {
            var root = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    [ServerName] = new JObject
                    {
                        ["command"] = cliPath,
                        ["args"] = new JArray("mcp-stdio", "--project", projectName),
                    },
                },
            };

            return root.ToString(Formatting.Indented);
        }
    }
}
