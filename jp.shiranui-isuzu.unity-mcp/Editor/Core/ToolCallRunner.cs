using System;
using System.Diagnostics;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Runs one tool call on the thread its descriptor asks for and reports how it ended.
    /// </summary>
    /// <remarks>
    /// Shared by the REST route (<c>POST /tools/&lt;name&gt;</c>) and the MCP endpoint so the two
    /// cannot disagree about threading or about when a call turns into a job.
    /// <para>
    /// Tools declaring <c>MainThread = false</c> run inline on the calling worker thread and never
    /// touch the dispatcher queue, which is what keeps them answering while the Editor main thread
    /// is blocked. Everything else is submitted to the dispatcher and waited on for the sync window;
    /// a call that outlives the window keeps running and is tracked as a job.
    /// </para>
    /// <para>
    /// A tool whose work spans several Editor frames returns a <see cref="DeferredToolResult"/>
    /// rather than its answer. The runner then waits out what is left of the same sync window on
    /// the deferred item, so such a tool costs the caller no extra latency budget and turns into a
    /// job under exactly the same rule as any other slow call.
    /// </para>
    /// </remarks>
    internal sealed class ToolCallRunner
    {
        private readonly McpMainThreadDispatcher dispatcher;
        private readonly McpJobRegistry jobs;
        private readonly Func<int> syncWaitMs;

        public ToolCallRunner(McpMainThreadDispatcher dispatcher, McpJobRegistry jobs, Func<int> syncWaitMs)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            this.syncWaitMs = syncWaitMs ?? throw new ArgumentNullException(nameof(syncWaitMs));
        }

        public ToolCallOutcome Run(McpToolDescriptor descriptor, JObject arguments)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            arguments ??= new JObject();

            var window = this.syncWaitMs();
            var elapsed = Stopwatch.StartNew();

            if (!descriptor.MainThread)
            {
                JObject result;

                try
                {
                    result = ToolInvoker.Invoke(descriptor, arguments);
                }
                catch (Exception e)
                {
                    return ToolCallOutcome.Failed(e);
                }

                return result is DeferredToolResult inline
                    ? this.AwaitDeferred(inline, descriptor, window, elapsed)
                    : ToolCallOutcome.Completed(result);
            }

            var item = this.dispatcher.Submit(() => ToolInvoker.Invoke(descriptor, arguments));

            if (!item.Wait(window))
            {
                return ToolCallOutcome.Running(this.jobs.Track(item, descriptor.Name));
            }

            if (item.Error != null)
            {
                return ToolCallOutcome.Failed(item.Error);
            }

            return item.Result is DeferredToolResult deferred
                ? this.AwaitDeferred(deferred, descriptor, window, elapsed)
                : ToolCallOutcome.Completed(item.Result);
        }

        /// <summary>
        /// Waits out whatever remains of the sync window for the real result.
        /// </summary>
        /// <remarks>
        /// The budget is what is left of the one window the call was given, measured from before
        /// the tool started. A fresh window here would let a deferred call hold the caller's
        /// request for twice the timeout it was promised.
        /// </remarks>
        private ToolCallOutcome AwaitDeferred(
            DeferredToolResult deferred,
            McpToolDescriptor descriptor,
            int window,
            Stopwatch elapsed)
        {
            var remaining = window - (int)elapsed.ElapsedMilliseconds;

            if (remaining < 0)
            {
                remaining = 0;
            }

            if (!deferred.Item.Wait(remaining))
            {
                return ToolCallOutcome.Running(this.jobs.Track(deferred.Item, descriptor.Name));
            }

            if (deferred.Item.Error != null)
            {
                return ToolCallOutcome.Failed(deferred.Item.Error);
            }

            // A deferred item may itself settle with a marker, so the unwrapping repeats until
            // a real result or a still-pending item is reached.
            return deferred.Item.Result is DeferredToolResult inner
                ? this.AwaitDeferred(inner, descriptor, window, elapsed)
                : ToolCallOutcome.Completed(deferred.Item.Result);
        }
    }

    /// <summary>How a tool call ended: finished, threw, or is still running as a job.</summary>
    internal sealed class ToolCallOutcome
    {
        public enum Kind
        {
            Completed,
            Failed,
            Running,
        }

        public Kind State { get; }

        /// <summary>The tool's result. Set only for <see cref="Kind.Completed"/>.</summary>
        public JObject Result { get; }

        /// <summary>The exception the tool threw. Set only for <see cref="Kind.Failed"/>.</summary>
        public Exception Error { get; }

        /// <summary>The job tracking the call. Set only for <see cref="Kind.Running"/>.</summary>
        public string JobId { get; }

        private ToolCallOutcome(Kind state, JObject result, Exception error, string jobId)
        {
            this.State = state;
            this.Result = result;
            this.Error = error;
            this.JobId = jobId;
        }

        public static ToolCallOutcome Completed(JObject result) => new(Kind.Completed, result ?? new JObject(), null, null);

        public static ToolCallOutcome Failed(Exception error) => new(Kind.Failed, null, error, null);

        public static ToolCallOutcome Running(string jobId) => new(Kind.Running, null, null, jobId);
    }
}
