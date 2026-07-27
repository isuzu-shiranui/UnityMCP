using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Unity console access.
    /// </summary>
    /// <remarks>
    /// v2 shipped two overlapping ways to read console entries — <c>/read_logs</c> and
    /// <c>console.getLogs</c> — differing only in paging vocabulary. Only the richer one is
    /// promoted to a tool here; oversized, redundant tool sets are a leading cause of models
    /// picking the wrong call. The <c>console.getLogs</c> command endpoint still exists for
    /// clients that have not migrated.
    /// <para>
    /// When the console reports nothing but you expect output, reach for
    /// <c>editor_log_tail</c> instead: it reads the log file directly and keeps working while
    /// the Editor is too busy to answer.
    /// </para>
    /// </remarks>
    internal static class ConsoleTools
    {
        private static readonly ConsoleCommandHandler Handler = new();

        [McpTool(
            "console_read_logs",
            "Read entries from the Unity console, newest first, with optional severity filtering. " +
            "Reflects what the Editor console currently holds; if it reports zero entries but you " +
            "expect output, confirm with editor_log_tail before concluding nothing was logged.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject ReadLogs(
            [McpArg("limit", "Maximum entries to return.")]
            int limit = 50,
            [McpArg("offset", "Entries to skip, for paging.")]
            int offset = 0,
            [McpArg("type", "Severity filter: all, error, warning, or log.")]
            string type = "all",
            [McpArg("fields", "Comma-separated field whitelist, to keep responses small.")]
            string fields = null)
        {
            return LogReader.ReadLogs(ToolArgs.Of(
                ("limit", limit),
                ("offset", offset),
                ("type", type),
                ("fields", fields)));
        }

        [McpTool(
            "console_get_count",
            "Return how many error, warning, and info entries the console holds. " +
            "Cheaper than reading entries when you only need to know whether something failed.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject GetCount()
        {
            return Handler.Execute("getCount", new JObject());
        }

        [McpTool(
            "console_clear",
            "Clear the Unity console. Useful before triggering an action so that any entries " +
            "afterwards are known to come from it.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Clear()
        {
            return Handler.Execute("clear", new JObject());
        }

        [McpTool(
            "console_set_filter",
            "Set the console's search filter text. Affects what the Editor UI displays.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject SetFilter(
            [McpArg("filter", "Filter text; pass an empty string to clear it.")]
            string filter = "")
        {
            return Handler.Execute("setFilter", ToolArgs.Of(("filter", filter)));
        }
    }
}
