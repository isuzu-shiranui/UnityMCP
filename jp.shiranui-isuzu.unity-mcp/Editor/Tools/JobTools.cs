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
                    $"No job '{jobId}'. Jobs are kept for ten minutes after they finish, and none survive a " +
                    "domain reload; if the Editor recompiled or entered Play Mode since, the work was interrupted.",
                    404);
            }

            return server.JobDetail(entry);
        }
    }
}
