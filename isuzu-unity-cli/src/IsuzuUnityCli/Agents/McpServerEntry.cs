using System.Text.Json.Nodes;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Agents;

/// <summary>
/// Renders the server entry each agent expects. The shapes differ more than they look:
/// Gemini names the field <c>httpUrl</c>, Cursor wants no <c>type</c> at all, and VS Code reads
/// its config from a file that is usually committed, so it gets a prompt placeholder rather
/// than the token.
/// </summary>
public static class McpServerEntry
{
    public const string TokenInputId = "isuzu-unity-token";

    /// <summary>The header VS Code substitutes from its prompt, and never the token itself.</summary>
    public const string VsCodeTokenReference = "Bearer ${input:" + TokenInputId + "}";

    /// <summary>The header a project-scoped entry carries, so no token is written under the project root.</summary>
    public const string EnvironmentTokenReference = "Bearer ${UNITY_MCP_TOKEN}";

    public static string BearerFor(InstanceDescriptor descriptor) => "Bearer " + descriptor.Token;

    /// <summary>The path to the entry inside the agent's config, ready for <see cref="JsonConfigEditor"/>.</summary>
    public static IReadOnlyList<string> PathFor(AgentTarget target, string projectRoot)
    {
        return target.Name switch
        {
            "claude-code" => ["projects", ClaudeCodeProjectKey(projectRoot), "mcpServers", AgentCatalog.ServerName],
            "vscode" => ["servers", AgentCatalog.ServerName],
            _ => ["mcpServers", AgentCatalog.ServerName],
        };
    }

    /// <summary>
    /// The key Claude Code files a project under.
    /// </summary>
    /// <remarks>
    /// Claude Code writes forward slashes on every platform, and looks the project up by the key
    /// it wrote. <c>Path.GetFullPath</c> hands back backslashes on Windows, and an entry filed
    /// under that key is one Claude Code never reads: the write succeeds, the config gains a
    /// project key holding nothing but this entry, and the server never appears.
    /// </remarks>
    public static string ClaudeCodeProjectKey(string projectRoot)
    {
        return projectRoot.Replace('\\', '/');
    }

    /// <summary>
    /// The key a Windows build wrote before <see cref="ClaudeCodeProjectKey"/> existed, or null
    /// when there is no separate one to clean up.
    /// </summary>
    public static IReadOnlyList<string>? SupersededClaudeCodePath(AgentTarget target, string projectRoot)
    {
        if (target.Name != "claude-code" || !projectRoot.Contains('\\'))
        {
            return null;
        }

        return ["projects", projectRoot, "mcpServers", AgentCatalog.ServerName];
    }

    public static JsonNode For(AgentTarget target, InstanceDescriptor descriptor, string executablePath)
    {
        return target.Name switch
        {
            "claude-code" => Http(descriptor.McpUrlOrDefault, BearerFor(descriptor), includeType: true),
            "claude-desktop" => Stdio(executablePath, descriptor.ProjectName),
            "cursor" => Http(descriptor.McpUrlOrDefault, BearerFor(descriptor), includeType: false),
            "gemini" => Gemini(descriptor.McpUrlOrDefault, BearerFor(descriptor)),
            "vscode" => Http(descriptor.McpUrlOrDefault, VsCodeTokenReference, includeType: true),
            _ => Http(descriptor.McpUrlOrDefault, BearerFor(descriptor), includeType: true),
        };
    }

    public static JsonObject Http(string mcpUrl, string authorization, bool includeType)
    {
        var entry = new JsonObject();

        if (includeType)
        {
            entry["type"] = "http";
        }

        entry["url"] = mcpUrl;
        entry["headers"] = new JsonObject { ["Authorization"] = authorization };
        return entry;
    }

    public static JsonObject Gemini(string mcpUrl, string authorization)
    {
        return new JsonObject
        {
            ["httpUrl"] = mcpUrl,
            ["headers"] = new JsonObject { ["Authorization"] = authorization },
        };
    }

    public static JsonObject Stdio(string executablePath, string projectName)
    {
        return new JsonObject
        {
            ["command"] = executablePath,
            ["args"] = new JsonArray("mcp-stdio", "--project", projectName),
        };
    }

    /// <summary>The whole of a project-scoped <c>.mcp.json</c>, which Claude Code reads per repository.</summary>
    public static JsonObject ProjectScopedConfig(string mcpUrl)
    {
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [AgentCatalog.ServerName] = Http(mcpUrl, EnvironmentTokenReference, includeType: true),
            },
        };
    }

    /// <summary>The prompt VS Code shows for the token, so the committed file never holds one.</summary>
    public static JsonObject TokenInput()
    {
        return new JsonObject
        {
            ["id"] = TokenInputId,
            ["type"] = "promptString",
            ["description"] = "Unity MCP bearer token",
            ["password"] = true,
        };
    }

    /// <summary>Adds the token prompt to a VS Code config, replacing an earlier one of ours.</summary>
    public static void EnsureTokenInput(JsonObject root)
    {
        if (root["inputs"] is not JsonArray inputs)
        {
            inputs = new JsonArray();
            root["inputs"] = inputs;
        }

        for (var i = inputs.Count - 1; i >= 0; i--)
        {
            if (inputs[i] is JsonObject existing && Text(existing["id"]) == TokenInputId)
            {
                inputs.RemoveAt(i);
            }
        }

        // Through the interface: JsonArray.Add<T> is the generic overload, which the trimmer
        // rejects because it can serialise arbitrary types.
        ((IList<JsonNode?>)inputs).Add(TokenInput());
    }

    public static void RemoveTokenInput(JsonObject root)
    {
        if (root["inputs"] is not JsonArray inputs)
        {
            return;
        }

        for (var i = inputs.Count - 1; i >= 0; i--)
        {
            if (inputs[i] is JsonObject existing && Text(existing["id"]) == TokenInputId)
            {
                inputs.RemoveAt(i);
            }
        }

        if (inputs.Count == 0)
        {
            root.Remove("inputs");
        }
    }

    /// <summary>The body of the Codex table, without its header line.</summary>
    public static string TomlBody(InstanceDescriptor descriptor)
    {
        return $"url = {TomlConfigEditor.Quote(descriptor.McpUrlOrDefault)}\n" +
               $"http_headers = {{ Authorization = {TomlConfigEditor.Quote(BearerFor(descriptor))} }}\n";
    }

    public static string TomlTableName() => "mcp_servers." + AgentCatalog.ServerName;

    /// <summary>The URL and Authorization header of a rendered JSON entry, whatever shape it is in.</summary>
    public static (string? Url, string? Authorization) Describe(JsonNode? entry)
    {
        if (entry is not JsonObject obj)
        {
            return (null, null);
        }

        var url = Text(obj["url"]) ?? Text(obj["httpUrl"]);
        var authorization = Text((obj["headers"] as JsonObject)?["Authorization"]);
        return (url, authorization);
    }

    /// <summary>These files are hand-edited, so a field that is not a string is read as absent.</summary>
    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
