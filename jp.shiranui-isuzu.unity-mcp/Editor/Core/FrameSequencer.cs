using System;
using System.Collections.Generic;
using System.Threading;

using Newtonsoft.Json.Linq;

using UnityEditor;

using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Runs a tool across several Editor frames, advancing one step per
    /// <c>EditorApplication.update</c>.
    /// </summary>
    /// <remarks>
    /// Some work cannot finish inside a single call: an event posted into the Editor is only
    /// consumed when the Editor next ticks, so a tool that posts one and then blocks the main
    /// thread waiting for the effect starves the very tick it is waiting for. Such a tool
    /// returns a <see cref="DeferredToolResult"/> instead and lets this type drive the rest.
    /// <para>
    /// One update handler drives every active sequence. It is registered when the first
    /// sequence starts and removed when the last one ends, so an idle Editor pays nothing and
    /// handlers registered by callers before a run always observe a tick before the step that
    /// follows it. Both the handler and the sequence list are static state, so a domain reload
    /// discards them; callers cancel first (see <see cref="CancelAll"/>) or a waiting request
    /// blocks for its whole window.
    /// </para>
    /// </remarks>
    internal static class FrameSequencer
    {
        private static readonly List<Sequence> Active = new();
        private static readonly object Gate = new();
        private static int activeCount;
        private static bool hooked;

        /// <summary>Number of sequences still advancing. Readable from any thread.</summary>
        public static int ActiveCount => Volatile.Read(ref activeCount);

        /// <summary>
        /// Starts <paramref name="steps"/> and returns the item that will carry its result.
        /// </summary>
        /// <remarks>
        /// Main thread only: it subscribes to <c>EditorApplication.update</c>, and the steps
        /// themselves run on the main thread.
        /// </remarks>
        /// <param name="label">Tool name, used when reporting a sequence that misbehaves.</param>
        public static McpMainThreadDispatcher.WorkItem Run(IEnumerator<FrameStep> steps, string label)
        {
            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }

            var item = McpMainThreadDispatcher.CreateDeferred();
            var sequence = new Sequence(steps, item, label);

            lock (Gate)
            {
                Active.Add(sequence);
                Volatile.Write(ref activeCount, Active.Count);

                if (!hooked)
                {
                    EditorApplication.update += Tick;
                    hooked = true;
                }
            }

            return item;
        }

        /// <summary>
        /// Fails every active sequence. Called when the server stops and before a domain
        /// reload, so a waiting request gets an answer instead of blocking for its full window
        /// against state that is about to be discarded.
        /// </summary>
        public static void CancelAll(string reason)
        {
            Sequence[] cancelled;

            lock (Gate)
            {
                if (Active.Count == 0)
                {
                    return;
                }

                cancelled = Active.ToArray();
                Active.Clear();
                Volatile.Write(ref activeCount, 0);
                Unhook();
            }

            foreach (var sequence in cancelled)
            {
                Dispose(sequence);
                sequence.Item.Fail(new McpToolException("cancelled", reason, 409));
            }
        }

        private static void Tick()
        {
            Sequence[] snapshot;

            lock (Gate)
            {
                if (Active.Count == 0)
                {
                    return;
                }

                snapshot = Active.ToArray();
            }

            foreach (var sequence in snapshot)
            {
                Advance(sequence);
            }
        }

        private static void Advance(Sequence sequence)
        {
            bool moved;

            try
            {
                moved = sequence.Steps.MoveNext();
            }
            catch (Exception e)
            {
                Finish(sequence, null, e);
                return;
            }

            if (moved && !sequence.Steps.Current.IsDone)
            {
                return;
            }

            // Reaching a Done step and running out of steps both end the sequence; only the
            // first carries a payload.
            Finish(sequence, moved ? sequence.Steps.Current.Result : null, null);
        }

        private static void Finish(Sequence sequence, JObject result, Exception error)
        {
            lock (Gate)
            {
                if (!Active.Remove(sequence))
                {
                    // CancelAll already took it and settled the item.
                    return;
                }

                Volatile.Write(ref activeCount, Active.Count);

                if (Active.Count == 0)
                {
                    Unhook();
                }
            }

            Dispose(sequence);

            if (error != null)
            {
                sequence.Item.Fail(error);
            }
            else
            {
                sequence.Item.Complete(result ?? new JObject { ["ok"] = true });
            }
        }

        /// <summary>
        /// Caller holds <see cref="Gate"/>. Removing the handler from inside its own invocation
        /// is safe: the delegate list is snapshotted before the call.
        /// </summary>
        private static void Unhook()
        {
            if (hooked)
            {
                EditorApplication.update -= Tick;
                hooked = false;
            }
        }

        /// <summary>
        /// Disposes the iterator, which is what runs the <c>finally</c> blocks a sequence uses
        /// to put back whatever it changed. Skipping it on the cancel path would leave the
        /// Editor holding a modifier key or a captured mouse.
        /// </summary>
        private static void Dispose(Sequence sequence)
        {
            try
            {
                sequence.Steps.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FrameSequencer] '{sequence.Label}' threw while cleaning up: {e.Message}");
            }
        }

        private sealed class Sequence
        {
            public Sequence(IEnumerator<FrameStep> steps, McpMainThreadDispatcher.WorkItem item, string label)
            {
                this.Steps = steps;
                this.Item = item;
                this.Label = label;
            }

            public IEnumerator<FrameStep> Steps { get; }

            public McpMainThreadDispatcher.WorkItem Item { get; }

            public string Label { get; }
        }
    }

    /// <summary>One step of a <see cref="FrameSequencer"/> run.</summary>
    internal readonly struct FrameStep
    {
        private FrameStep(bool isDone, JObject result)
        {
            this.IsDone = isDone;
            this.Result = result;
        }

        /// <summary>True when this step ends the sequence.</summary>
        public bool IsDone { get; }

        /// <summary>The tool's answer. Null unless <see cref="IsDone"/> is true.</summary>
        public JObject Result { get; }

        /// <summary>Gives up the rest of this frame; the sequence resumes on the next tick.</summary>
        public static FrameStep Wait() => new(false, null);

        /// <summary>Ends the sequence, answering the caller with <paramref name="result"/>.</summary>
        public static FrameStep Done(JObject result) => new(true, result);
    }
}
