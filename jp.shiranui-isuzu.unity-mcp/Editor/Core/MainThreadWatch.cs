using System;
using System.Threading;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Records when the Editor main thread last ran and reports how long it has been away while
    /// work was waiting for it.
    /// </summary>
    /// <remarks>
    /// A modal dialog blocks the main thread inside its own message loop, so the dispatcher never
    /// pumps and every queued call stays "running" with no further sign of what is wrong. This
    /// is read from worker threads, which keep answering, so the stall can be reported alongside
    /// the job.
    /// <para>
    /// An idle main thread is not a stall: the gap counts only while the dispatcher has queued
    /// work or a job is running. An unfocused Editor without the loop waker ticks about every
    /// 100 ms, so gaps up to <see cref="StallThresholdMs"/> are reported as zero.
    /// </para>
    /// </remarks>
    internal sealed class MainThreadWatch
    {
        /// <summary>Gaps at or below this are the Editor's own tick interval, not a stall.</summary>
        public const long StallThresholdMs = 1000;

        /// <summary>Above this the running-call text says the main thread has not run.</summary>
        public const long ReportThresholdMs = 5000;

        private readonly Func<long> utcNowTicks;
        private long lastPumpTicks;

        public MainThreadWatch()
            : this(() => DateTime.UtcNow.Ticks)
        {
        }

        public MainThreadWatch(Func<long> utcNowTicks)
        {
            this.utcNowTicks = utcNowTicks ?? throw new ArgumentNullException(nameof(utcNowTicks));
        }

        /// <summary>Called from the main thread each time it runs.</summary>
        public void MarkPumped()
        {
            Volatile.Write(ref this.lastPumpTicks, this.utcNowTicks());
        }

        /// <summary>
        /// Milliseconds since the main thread last ran, or zero when nothing is waiting for it,
        /// it has never run, or the gap is within the Editor's own tick interval.
        /// </summary>
        public long StalledMs(bool hasPendingWork)
        {
            var last = Volatile.Read(ref this.lastPumpTicks);

            if (!hasPendingWork || last == 0)
            {
                return 0;
            }

            var gap = (this.utcNowTicks() - last) / TimeSpan.TicksPerMillisecond;
            return gap > StallThresholdMs ? gap : 0;
        }

        /// <summary>
        /// The sentence appended to every "still running" answer, or null when there is nothing
        /// to add: a visible dialog first, otherwise a main thread absent for longer than
        /// <see cref="ReportThresholdMs"/>.
        /// </summary>
        public static string RunningNotice(EditorDialogs.DialogInfo dialog, long stalledMs)
        {
            if (dialog != null)
            {
                var text = $"The Editor is showing a dialog \"{dialog.Title}\"";

                var message = OneLine(dialog.Message);
                if (message.Length > 0)
                {
                    text += $" ({Trim(message, 200)})";
                }

                if (dialog.Buttons != null && dialog.Buttons.Length > 0)
                {
                    text += $" with buttons {string.Join(" / ", dialog.Buttons)}";
                }

                return text + ". Nothing proceeds until it is answered; use editor_dialog_press or answer it in the Editor.";
            }

            if (stalledMs > ReportThresholdMs)
            {
                return $"The Editor main thread has not run for {stalledMs / 1000} s; " +
                       "it may be showing a dialog this tool cannot see, importing, or compiling.";
            }

            return null;
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string Trim(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
