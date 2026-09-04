using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// A tool returns this to say its answer is not ready yet: the real result arrives on
    /// <see cref="Item"/> once later Editor frames have run.
    /// </summary>
    /// <remarks>
    /// It derives from <see cref="JObject"/> because <c>ToolInvoker</c> passes an object-shaped
    /// return value through untouched, so the marker survives the invoker and reaches
    /// <see cref="ToolCallRunner"/> without a second return channel. That also means one which
    /// escaped the runner would reach a client as an empty JSON object rather than as an error,
    /// so the runner must unwrap every path a tool result can take.
    /// </remarks>
    internal sealed class DeferredToolResult : JObject
    {
        public DeferredToolResult(McpMainThreadDispatcher.WorkItem item)
        {
            this.Item = item;
        }

        /// <summary>The item carrying the result the caller actually asked for.</summary>
        public McpMainThreadDispatcher.WorkItem Item { get; }
    }
}
