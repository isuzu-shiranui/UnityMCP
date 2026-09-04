using System;
using System.Reflection;
using System.Threading;

using UnityEditor;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Wakes the Editor main loop while requests are waiting for it.
    /// </summary>
    /// <remarks>
    /// An Editor without focus runs <c>EditorApplication.update</c> about every 100 ms, whatever
    /// the Interaction Mode preference says, so every call that needs the main thread waited
    /// up to that long. <c>EditorApplication.SignalTick</c> wakes the loop from any thread;
    /// called every 16 ms it brings the interval down to a few milliseconds. It is internal, so
    /// it is reached by reflection and the pump is a no-op on an Editor that lacks it.
    /// <para>
    /// The pump runs only while work is queued, plus a short grace period so a client making
    /// several calls in a row does not pay the wake-up each time. <see cref="AlwaysOn"/> keeps
    /// it running for the whole session instead, for hosts where a fully idle Editor stops
    /// accepting connections at all.
    /// </para>
    /// </remarks>
    internal sealed class EditorLoopWaker : IDisposable
    {
        public const int IntervalMs = 16;
        private const int GraceMs = 250;

        private static readonly MethodInfo SignalTickMethod = FindSignalTick();

        /// <summary>True when this Editor exposes the wake-up call.</summary>
        public static bool Available => SignalTickMethod != null;

        /// <summary>
        /// Asks for the parameterless overload by name and signature. This runs in the static
        /// initializer, so a lookup that could throw would take the server constructor with it.
        /// </summary>
        private static MethodInfo FindSignalTick()
        {
            try
            {
                return typeof(EditorApplication).GetMethod(
                    "SignalTick",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        private readonly Action signal;
        private readonly Func<bool> hasPendingWork;
        private readonly object gate = new();
        private Thread thread;
        private bool alwaysOn;
        private bool disposed;
        private long lastDemandTicks;

        /// <summary>Uses the Editor's own wake-up call.</summary>
        public EditorLoopWaker(Func<bool> hasPendingWork)
            : this(hasPendingWork, Available ? (Action)(() => SignalTickMethod.Invoke(null, null)) : null)
        {
        }

        /// <summary>Uses <paramref name="signal"/> in place of the Editor call; null means unavailable.</summary>
        public EditorLoopWaker(Func<bool> hasPendingWork, Action signal)
        {
            this.hasPendingWork = hasPendingWork ?? throw new ArgumentNullException(nameof(hasPendingWork));
            this.signal = signal;
        }

        /// <summary>Whether the pump can do anything on this Editor.</summary>
        public bool IsAvailable => this.signal != null;

        /// <summary>Whether a pump thread is currently running.</summary>
        public bool IsRunning
        {
            get
            {
                lock (this.gate)
                {
                    return this.thread != null && this.thread.IsAlive;
                }
            }
        }

        /// <summary>Keep ticking for the whole session rather than only while work is queued.</summary>
        public bool AlwaysOn
        {
            get
            {
                lock (this.gate)
                {
                    return this.alwaysOn;
                }
            }
            set
            {
                lock (this.gate)
                {
                    this.alwaysOn = value;
                }

                if (value)
                {
                    this.Demand();
                }
            }
        }

        /// <summary>
        /// Called when work is queued: wakes the loop now and makes sure a pump thread is
        /// running to keep waking it until the queue drains.
        /// </summary>
        public void Demand()
        {
            if (this.signal == null)
            {
                return;
            }

            Interlocked.Exchange(ref this.lastDemandTicks, Environment.TickCount);

            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                if (this.thread == null || !this.thread.IsAlive)
                {
                    this.thread = new Thread(this.Run) { IsBackground = true, Name = "McpEditorLoopWaker" };
                    this.thread.Start();
                }
            }

            this.TrySignal();
        }

        private void Run()
        {
            while (true)
            {
                lock (this.gate)
                {
                    if (this.disposed)
                    {
                        return;
                    }

                    var idleFor = unchecked(Environment.TickCount - (int)Interlocked.Read(ref this.lastDemandTicks));
                    if (!this.alwaysOn && !this.hasPendingWork() && idleFor > GraceMs)
                    {
                        this.thread = null;
                        return;
                    }
                }

                this.TrySignal();
                Thread.Sleep(IntervalMs);
            }
        }

        private void TrySignal()
        {
            try
            {
                this.signal();
            }
            catch (Exception)
            {
                // The Editor is shutting down or reloading; the next tick will not be needed.
            }
        }

        public void Dispose()
        {
            lock (this.gate)
            {
                this.disposed = true;
                this.thread = null;
            }
        }
    }
}
