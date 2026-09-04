using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Tracks main-thread work that outlived its request's synchronous wait, so the caller can
    /// poll for the outcome instead of being told the call timed out.
    /// </summary>
    /// <remarks>
    /// A job is only created once the sync window expires, so fast calls — the overwhelming
    /// majority — allocate nothing here and still return their result inline.
    /// <para>
    /// A slow call answers with a job id rather than a timeout. A job id is not something a
    /// client retries; a timeout is, and it leaves the side effect queued, so the retry runs
    /// the work a second time.
    /// </para>
    /// </remarks>
    internal sealed class McpJobRegistry
    {
        /// <summary>How long a finished job stays queryable before it is evicted.</summary>
        private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(10);

        /// <summary>Hard cap so a pathological client cannot grow the registry without bound.</summary>
        private const int MaxEntries = 256;

        private readonly Dictionary<string, JobEntry> entries = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private long counter;

        /// <summary>
        /// Registers a still-running work item and returns its job id.
        /// </summary>
        /// <param name="label">Endpoint or tool name; becomes part of the readable id.</param>
        public string Track(McpMainThreadDispatcher.WorkItem item, string label)
        {
            var id = $"{Sanitize(label)}-{Interlocked.Increment(ref this.counter)}";
            var entry = new JobEntry(id, label, item);

            lock (this.gate)
            {
                this.entries[id] = entry;
                this.EvictLocked();
            }

            return entry.Id;
        }

        /// <summary>Looks up a job by id.</summary>
        public bool TryGet(string id, out JobEntry entry)
        {
            lock (this.gate)
            {
                return this.entries.TryGetValue(id ?? string.Empty, out entry);
            }
        }

        /// <summary>Renders every tracked job, newest first.</summary>
        public JArray ToJson()
        {
            lock (this.gate)
            {
                this.EvictLocked();

                return new JArray(this.entries.Values
                    .OrderByDescending(e => e.CreatedUtc)
                    .Select(e => e.ToJson())
                    .Cast<object>()
                    .ToArray());
            }
        }

        /// <summary>Number of jobs that have not finished.</summary>
        public int RunningCount
        {
            get
            {
                lock (this.gate)
                {
                    return this.entries.Values.Count(e => !e.Item.IsCompleted);
                }
            }
        }

        /// <summary>
        /// Drops finished jobs past their retention window, then trims oldest-first if the
        /// registry is still over the cap. Running jobs are never evicted — losing the handle
        /// to work that is still going to mutate the project is the one outcome worse than
        /// an oversized dictionary.
        /// </summary>
        private void EvictLocked()
        {
            var now = DateTime.UtcNow;

            var expired = this.entries.Values
                .Where(e => e.Item.IsCompleted && now - e.CompletedUtcOrNow > CompletedRetention)
                .Select(e => e.Id)
                .ToArray();

            foreach (var id in expired)
            {
                this.entries.Remove(id);
            }

            if (this.entries.Count <= MaxEntries)
            {
                return;
            }

            var surplus = this.entries.Values
                .Where(e => e.Item.IsCompleted)
                .OrderBy(e => e.CreatedUtc)
                .Take(this.entries.Count - MaxEntries)
                .Select(e => e.Id)
                .ToArray();

            foreach (var id in surplus)
            {
                this.entries.Remove(id);
            }
        }

        /// <summary>
        /// Turns an endpoint or tool name into something usable inside an id.
        /// Readable ids matter: an agent that has to quote <c>execute_code-7</c> back is far
        /// less likely to garble it than one quoting a raw GUID.
        /// </summary>
        private static string Sanitize(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return "job";
            }

            var builder = new StringBuilder(label.Length);
            foreach (var c in label)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }

            var sanitized = builder.ToString().Trim('_');
            return sanitized.Length == 0 ? "job" : sanitized;
        }

        /// <summary>One tracked job.</summary>
        internal sealed class JobEntry
        {
            private DateTime? completedUtc;
            private McpMainThreadDispatcher.WorkItem item;

            public JobEntry(string id, string label, McpMainThreadDispatcher.WorkItem item)
            {
                this.Id = id;
                this.Label = label;
                this.item = item;
                this.CreatedUtc = DateTime.UtcNow;
            }

            public string Id { get; }

            public string Label { get; }

            /// <summary>
            /// The item carrying the job's outcome. A tracked item that settles with a
            /// <see cref="DeferredToolResult"/> has only started its work, so the job follows
            /// the inner item instead of reporting the marker as a completed, empty result.
            /// </summary>
            public McpMainThreadDispatcher.WorkItem Item
            {
                get
                {
                    var current = this.item;

                    while (current.IsCompleted && current.Error == null && current.Result is DeferredToolResult deferred)
                    {
                        current = deferred.Item;
                    }

                    this.item = current;
                    return current;
                }
            }

            public DateTime CreatedUtc { get; }

            /// <summary>
            /// When the job finished, or now if it is still running. Latched on first
            /// observation after completion so retention is measured from the finish, not
            /// from whenever the registry last happened to look.
            /// </summary>
            public DateTime CompletedUtcOrNow
            {
                get
                {
                    if (!this.Item.IsCompleted)
                    {
                        return DateTime.UtcNow;
                    }

                    return this.completedUtc ??= DateTime.UtcNow;
                }
            }

            /// <summary>Job status as reported over HTTP.</summary>
            public string Status
            {
                get
                {
                    if (!this.Item.IsCompleted)
                    {
                        return "running";
                    }

                    if (this.Item.WasAbandoned)
                    {
                        return "cancelled";
                    }

                    return this.Item.Error != null ? "failed" : "completed";
                }
            }

            /// <summary>Summary form used by <c>GET /jobs</c>.</summary>
            public JObject ToJson()
            {
                return new JObject
                {
                    ["id"] = this.Id,
                    ["label"] = this.Label,
                    ["status"] = this.Status,
                    ["ageSec"] = (DateTime.UtcNow - this.CreatedUtc).TotalSeconds,
                };
            }

            /// <summary>Full form used by <c>GET /jobs/&lt;id&gt;</c>, including the result.</summary>
            public JObject ToDetailJson()
            {
                var payload = this.ToJson();

                if (!this.Item.IsCompleted)
                {
                    return payload;
                }

                if (this.Item.Error != null)
                {
                    payload["error"] = this.Item.Error.Message;
                }
                else if (this.Item.Result != null)
                {
                    payload["result"] = this.Item.Result;
                }

                return payload;
            }
        }
    }
}
