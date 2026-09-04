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
    /// One tool for reading entries, not several differing only in paging vocabulary.
    /// Oversized, redundant tool sets are a leading cause of models picking the wrong call.
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
            Idempotency = McpIdempotency.Safe,
            // Reading the console is the first step of nearly every diagnosis here, so paying a
            // tool-search round trip for it every time costs more than the context it occupies.
            AlwaysLoad = true,
            // A stack trace per entry adds up; truncating the batch loses the earliest error,
            // which is usually the one that caused the rest.
            MaxResultSizeChars = 200000)]
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
            "Return how many entries the console holds, split into errorCount, warningCount and " +
            "logCount, with the total. Cheaper than reading entries when you only need to know " +
            "whether something failed.",
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

        // console_set_filter is deliberately not a tool. Setting the console's search filter
        // is Editor UI state that persists, and it silently narrows every later read: with a
        // filter set, console_read_logs went from 21 entries to 1, and console_get_count
        // reported errorCount 0 alongside logCount 23. An agent checking for errors would
        // conclude there were none. A tool whose lasting effect is to hide the diagnostics
        // is worth less than the catalog space it occupies. The console.setFilter command
        // endpoint remains for anyone driving the Editor UI on purpose.
    }
}
