using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Newtonsoft.Json.Linq;

using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Marshals work from HTTP worker threads onto the Editor main thread.
    /// </summary>
    /// <remarks>
    /// Two properties the queue exists to hold:
    /// <list type="bullet">
    /// <item>A caller can <see cref="WorkItem.TryAbandon"/> an item, and the state machine
    /// guarantees an abandoned item that has not started will never start. A request that
    /// gives up while its action stays queued lands the side effect anyway, and a client that
    /// retries produces it twice.</item>
    /// <item>The lock covers only the dequeue, never the run. Held across the run, one slow
    /// action blocks every other worker from even enqueuing.</item>
    /// </list>
    /// </remarks>
    internal sealed class McpMainThreadDispatcher
    {
        /// <summary>Queued, not yet started. The only state from which abandoning works.</summary>
        private const int StatePending = 0;

        /// <summary>Started on the main thread; can no longer be abandoned.</summary>
        private const int StateRunning = 1;

        /// <summary>Abandoned before it started; the pump will skip it.</summary>
        private const int StateAbandoned = 2;

        /// <summary>
        /// How long a single <see cref="Pump"/> call may spend running queued work before
        /// yielding back to the Editor. A single long item still overruns this — it cannot be
        /// interrupted — but a backlog of short items will not freeze the UI for a whole frame.
        /// </summary>
        private const long PumpBudgetMs = 50;

        private readonly Queue<WorkItem> queue = new();
        private readonly object gate = new();

        /// <summary>
        /// Log lines produced on worker threads, flushed from <see cref="Pump"/>.
        /// <para>
        /// <c>UnityEngine.Debug.Log</c> is safe to call off the main thread but takes an
        /// internal lock, so logging directly from the request path couples every HTTP
        /// response to whatever the main thread is doing. Buffering keeps the request path
        /// free of Unity locks, which is the whole point of answering while the Editor is busy.
        /// </para>
        /// </summary>
        private readonly ConcurrentQueue<(string Message, bool IsError)> pendingLogs = new();

        /// <summary>
        /// Raised after an item is queued, from the submitting thread. The server uses it to
        /// wake the Editor main loop, which otherwise ticks about every 100 ms without focus.
        /// </summary>
        public Action WorkQueued { get; set; }

        /// <summary>
        /// Raised at the start of every <see cref="Pump"/>, on the main thread, whether or not
        /// anything is queued. The server uses it to record when the main thread last ran.
        /// </summary>
        public Action Pumped { get; set; }

        /// <summary>Number of items waiting to start.</summary>
        public int PendingCount
        {
            get
            {
                lock (this.gate)
                {
                    return this.queue.Count;
                }
            }
        }

        /// <summary>
        /// Queues work for the main thread and returns immediately.
        /// </summary>
        public WorkItem Submit(Func<JObject> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            var item = new WorkItem(work);

            lock (this.gate)
            {
                this.queue.Enqueue(item);
            }

            this.WorkQueued?.Invoke();

            return item;
        }

        /// <summary>
        /// Creates an item that belongs to no queue, for work whose result arrives from
        /// somewhere other than the pump — a sequence spread over several Editor frames.
        /// </summary>
        /// <remarks>
        /// It starts in the running state rather than pending, so
        /// <see cref="WorkItem.TryAbandon"/> refuses it and a job built from it reports
        /// <c>running</c>. Nothing else can start it, so a pending state would be a lie: there
        /// is no queue entry to cancel. The producer settles it with
        /// <see cref="WorkItem.Complete"/> or <see cref="WorkItem.Fail"/>.
        /// </remarks>
        public static WorkItem CreateDeferred() => WorkItem.CreateDeferred();

        /// <summary>
        /// Records a message to be written to the Unity console from the main thread.
        /// Safe to call from any thread.
        /// </summary>
        public void Log(string message)
        {
            this.pendingLogs.Enqueue((message, false));
        }

        /// <summary>
        /// Records an error to be written to the Unity console from the main thread.
        /// Safe to call from any thread.
        /// </summary>
        public void LogError(string message)
        {
            this.pendingLogs.Enqueue((message, true));
        }

        /// <summary>
        /// Drains the queue. Hooked to <c>EditorApplication.update</c>, so this runs on the
        /// main thread.
        /// </summary>
        public void Pump()
        {
            this.Pumped?.Invoke();

            while (this.pendingLogs.TryDequeue(out var entry))
            {
                if (entry.IsError)
                {
                    Debug.LogError(entry.Message);
                }
                else
                {
                    Debug.Log(entry.Message);
                }
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < PumpBudgetMs)
            {
                WorkItem item;

                lock (this.gate)
                {
                    if (this.queue.Count == 0)
                    {
                        return;
                    }

                    item = this.queue.Dequeue();
                }

                // Run outside the lock so a slow item cannot block other workers from enqueuing.
                item.Run();
            }
        }

        /// <summary>
        /// Fails every queued item. Called when the server stops so waiting requests get an
        /// immediate answer instead of blocking for their full timeout.
        /// </summary>
        public void DrainAndFail(string reason)
        {
            List<WorkItem> pending;

            lock (this.gate)
            {
                pending = new List<WorkItem>(this.queue);
                this.queue.Clear();
            }

            foreach (var item in pending)
            {
                item.FailBeforeStart(reason);
            }
        }

        /// <summary>
        /// One unit of main-thread work, plus the state needed to decide whether it may still
        /// be cancelled and to hand its outcome back to the waiting request.
        /// </summary>
        internal sealed class WorkItem
        {
            private readonly Func<JObject> work;
            private readonly ManualResetEventSlim completed = new(false);
            private int state = StatePending;

            /// <summary>Guards the outcome so a second <see cref="Complete"/> cannot overwrite it.</summary>
            private int settled;

            public WorkItem(Func<JObject> work)
            {
                this.work = work;
            }

            private WorkItem()
            {
                this.state = StateRunning;
            }

            internal static WorkItem CreateDeferred() => new();

            /// <summary>Result of the work, valid once <see cref="IsCompleted"/> is true.</summary>
            public JObject Result { get; private set; }

            /// <summary>Exception thrown by the work, if any.</summary>
            public Exception Error { get; private set; }

            /// <summary>True once the work has finished, failed, or been abandoned.</summary>
            public bool IsCompleted => this.completed.IsSet;

            /// <summary>True when the item was cancelled before it ever ran.</summary>
            public bool WasAbandoned => Volatile.Read(ref this.state) == StateAbandoned;

            /// <summary>
            /// Cancels the item if it has not started.
            /// </summary>
            /// <returns>
            /// True when the item is guaranteed never to run. False means it had already
            /// started, and the caller must assume its side effects will happen.
            /// </returns>
            public bool TryAbandon()
            {
                if (Interlocked.CompareExchange(ref this.state, StateAbandoned, StatePending) != StatePending)
                {
                    return false;
                }

                this.completed.Set();
                return true;
            }

            /// <summary>Blocks until the work finishes or the timeout elapses.</summary>
            public bool Wait(int timeoutMs)
            {
                return this.completed.Wait(timeoutMs);
            }

            /// <summary>
            /// Hands a deferred item its result and releases anyone waiting on it.
            /// A second call, or one after <see cref="Fail"/>, is ignored.
            /// </summary>
            public void Complete(JObject result)
            {
                if (Interlocked.CompareExchange(ref this.settled, 1, 0) != 0)
                {
                    return;
                }

                this.Result = result;
                this.completed.Set();
            }

            /// <summary>
            /// Fails a deferred item and releases anyone waiting on it.
            /// A second call, or one after <see cref="Complete"/>, is ignored.
            /// </summary>
            public void Fail(Exception error)
            {
                if (Interlocked.CompareExchange(ref this.settled, 1, 0) != 0)
                {
                    return;
                }

                this.Error = error;
                this.completed.Set();
            }

            /// <summary>Runs the work on the main thread. Called only by the pump.</summary>
            internal void Run()
            {
                if (Interlocked.CompareExchange(ref this.state, StateRunning, StatePending) != StatePending)
                {
                    // Abandoned while queued. Skipping here is what makes the "a timed-out
                    // request produces no side effect" guarantee hold.
                    return;
                }

                try
                {
                    this.Result = this.work();
                }
                catch (Exception e)
                {
                    this.Error = e;
                }
                finally
                {
                    this.completed.Set();
                }
            }

            /// <summary>Marks a still-queued item as failed because the server is going away.</summary>
            internal void FailBeforeStart(string reason)
            {
                if (Interlocked.CompareExchange(ref this.state, StateRunning, StatePending) != StatePending)
                {
                    return;
                }

                this.Error = new InvalidOperationException(reason);
                this.completed.Set();
            }
        }
    }
}
