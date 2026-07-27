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
    /// Replaces the v2 pattern of an <c>Action</c> queue drained inside the lock, paired with
    /// a bare <c>ManualResetEvent.WaitOne(10000)</c> at the call site. That had two defects
    /// this type is built to remove:
    /// <list type="bullet">
    /// <item>A timed-out request returned 504 but left its action queued, so the side effect
    /// still landed later. A client that retried on the 504 executed the work twice. Here a
    /// caller can <see cref="WorkItem.TryAbandon"/> an item, and the state machine guarantees
    /// an abandoned item that has not started will never start.</item>
    /// <item>The queue lock was held while running each action, so one slow action blocked
    /// every other worker from even enqueuing. Here the lock covers only the dequeue.</item>
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

            return item;
        }

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

            public WorkItem(Func<JObject> work)
            {
                this.work = work;
            }

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
