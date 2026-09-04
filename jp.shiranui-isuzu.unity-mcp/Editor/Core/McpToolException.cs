using System;

namespace UnityMCP.Editor.Core
{
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
