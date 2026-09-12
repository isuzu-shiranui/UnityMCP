using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Whether the Editor can answer anything right now.
    /// </summary>
    /// <remarks>
    /// Every other tool here runs on the Editor's main thread, so while an import or a modal
    /// dialog holds that thread, none of them come back — including the ones that would say why.
    /// Importing a project of ten thousand Live2D assets put twenty-five calls in the queue and
    /// answered none of them, and from the caller's side that is indistinguishable from a tool
    /// that hangs. This one is read off the queue and the counters instead, so it answers in
    /// milliseconds exactly when nothing else will.
    /// </remarks>
    internal static class EditorStateTools
    {
        [McpTool(
            "editor_state",
            "Whether the Editor can answer anything right now, read without the main thread that " +
            "every other tool needs. Ask this first when a call has not come back: an import, a " +
            "compile or a modal dialog holds the main thread, and while it does, every tool that " +
            "needs it queues instead of answering — including the ones that would report the " +
            "problem. 'queueDepth' climbing while 'reqCount' stands still is a main thread that " +
            "has stopped serving; 'mainThread' names the dialog when one is holding it. Nothing " +
            "here is worth polling in a loop: read it once to decide between waiting and giving " +
            "up.",
            Idempotency = McpIdempotency.Safe,
            Group = McpToolGroups.Diagnostics,
            // The whole point: answered off the queue, so it comes back while the main thread
            // is the thing that cannot.
            MainThread = false)]
        public static JObject State()
        {
            if (!McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
            {
                throw new McpToolException(
                    "not_found",
                    "The MCP server is not running in this Editor, so there is no state to read.",
                    503);
            }

            return server.HealthPayload();
        }
    }
}
