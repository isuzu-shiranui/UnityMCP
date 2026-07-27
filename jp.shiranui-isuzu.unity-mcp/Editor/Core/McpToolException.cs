using System;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Thrown when a tool call fails in a way the caller can act on — a missing argument,
    /// an uncoercible value, a destructive call without confirmation.
    /// <para>
    /// <see cref="Code"/> and <see cref="HttpStatus"/> flow straight into the response
    /// envelope, so the model receives an error it can correct rather than a stack trace.
    /// This mirrors the existing <c>McpScreenshotException</c> pattern.
    /// </para>
    /// </summary>
    internal sealed class McpToolException : Exception
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
