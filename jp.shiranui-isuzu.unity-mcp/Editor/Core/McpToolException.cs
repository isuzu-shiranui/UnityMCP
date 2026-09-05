using System;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Handlers that report failure as <c>{"error": "message"}</c> rather than by throwing.
    /// </summary>
    /// <remarks>
    /// Both transports have to read this the same way. A result carrying <c>error</c> is a
    /// failure, and a caller told otherwise acts on a message that says the opposite.
    /// </remarks>
    internal static class HandlerErrorResult
    {
        /// <summary>The message a handler reported this way, or null when the result is not one.</summary>
        /// <remarks>
        /// A payload carrying <c>status</c> is reporting on something else's outcome rather than
        /// failing itself. <c>job_status</c> answers with the job's own detail, and a failed job's
        /// detail carries the failure message under <c>error</c>: read as a handler's failure it
        /// turns a successful fetch into a failed call and throws away the status, the id and the
        /// label the caller asked for.
        /// </remarks>
        public static string Message(Newtonsoft.Json.Linq.JObject result)
        {
            if (result == null || result["result"] != null || result["status"] != null)
            {
                return null;
            }

            var error = result["error"];

            return error != null && error.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? error.ToString()
                : null;
        }
    }

    /// <summary>
    /// Thrown when a tool call fails in a way the caller can act on — a missing argument,
    /// an uncoercible value, a destructive call without confirmation.
    /// <para>
    /// <see cref="Code"/> and <see cref="HttpStatus"/> flow straight into the response
    /// envelope, so the model receives an error it can correct rather than a stack trace.
    /// </para>
    /// <para>
    /// Not sealed: <c>ToolInvoker</c> turns every other exception into <c>tool_failed</c> with a
    /// 500, and a 500 on a Safe tool is retried for the whole budget. A handler that wants its
    /// own code to reach the caller has to derive from this.
    /// </para>
    /// </summary>
    internal class McpToolException : Exception
    {
        /// <summary>Machine-readable error code, e.g. <c>invalid_params</c>.</summary>
        public string Code { get; }

        /// <summary>HTTP status to report.</summary>
        public int HttpStatus { get; }

        public McpToolException(string code, string message, int httpStatus = 400)
            : base(message)
        {
            this.Code = code;
            this.HttpStatus = httpStatus;
        }
    }
}
