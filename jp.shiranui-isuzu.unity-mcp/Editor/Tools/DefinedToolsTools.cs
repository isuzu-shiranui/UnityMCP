using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Shows which JSON tool definitions loaded and why the others did not.
    /// </summary>
    internal static class DefinedToolsTools
    {
        [McpTool(
            "definitions_list",
            "List the tools defined by JSON files: the directories they are read from, each loaded " +
            "tool with its kind and file, and every file that was refused with the reason. " +
            "Use this when a defined tool is missing from the tool list.",
            Idempotency = McpIdempotency.Safe,
            MainThread = false,
            Group = McpToolGroups.Diagnostics)]
        public static JObject List()
        {
            if (!McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
            {
                throw new McpToolException("server_unavailable", "The MCP server is not running.", 503);
            }

            return server.DescribeDefinedTools();
        }
    }
}
