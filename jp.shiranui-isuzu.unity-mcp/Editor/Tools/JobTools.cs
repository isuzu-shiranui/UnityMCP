using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Lets an MCP client follow a call that outlived the sync window. The REST client has
    /// <c>GET /jobs/&lt;id&gt;</c> for this; MCP has only tools, so the same lookup is a tool.
    /// </summary>
    internal static class JobTools
    {
        [McpTool(
            "job_status",
            "Fetch the state and result of a job id returned by a tool call that was still running. " +
            "Poll this instead of repeating the original call: the work is in progress and repeating it would run it twice. " +
            "Status is running, completed, failed or cancelled; completed carries the result.",
            Idempotency = McpIdempotency.Safe,
            MainThread = false)]
        public static JObject Status([McpArg("job_id", "The job id from the earlier response.")] string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new McpToolException("invalid_params", "'job_id' is required.");
            }

            if (!McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
            {
                throw new McpToolException("server_unavailable", "The MCP server is not running.", 503);
            }

            if (!server.Jobs.TryGet(jobId, out var entry))
            {
                throw new McpToolException(
                    "job_not_found",
                    $"No job '{jobId}'. A record is kept for ten minutes after a job finishes and none " +
                    "survives a domain reload, so a job that ran to completion and one that a reload cut " +
                    "short both end up here and cannot be told apart from this side. Read back whatever " +
                    "the work would have changed. A job whose own work reloads the domain - a settings " +
                    "write that saves assets, a recompile, entering Play Mode - reaches this every time.",
                    404);
            }

            return server.JobDetail(entry);
        }
    }
}
