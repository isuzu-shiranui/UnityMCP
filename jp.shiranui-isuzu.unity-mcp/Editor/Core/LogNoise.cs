using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Cuts a log down to what a reader can act on: the message, and the frames naming a file and
    /// a line.
    /// </summary>
    /// <remarks>
    /// Cutting by character count spends the budget on whatever came first, which under a test
    /// runner is the machinery that called the test rather than the test. A 500-character cut
    /// drops the located frame from a typical NUnit trace, severing it mid-path so neither file
    /// nor line survives.
    /// </remarks>
    internal static class LogNoise
    {
        /// <summary>
        /// A frame Unity printed: <c>Namespace.Type:Method (</c>, nested types included, or one of
        /// Mono's generated thunks. The thunks matter because a line the run does not recognise
        /// splits the trace in two, and each half then keeps its own quota of frames.
        /// </summary>
        private static readonly Regex Frame =
            new Regex(@"^(\(wrapper [\w -]+\)\s*)?[\w.<>+`|/]+:[\w<>$|.`]+\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// A frame that names where it ran. Matched from the end because a Windows path carries a
        /// drive letter, so the colon before the line number is not the only one on the line.
        /// </summary>
        private static readonly Regex Located =
            new Regex(@"\(at .+:\d+\)\s*$", RegexOptions.Compiled);

        /// <summary>The Debug.Log call itself, which is on every trace and names nothing.</summary>
        private static readonly Regex LoggingCall =
            new Regex(@"^UnityEngine\.(Debug|Logger):Log", RegexOptions.Compiled);

        /// <summary>
        /// The path a tool call arrives through, which is on every entry a snippet logged.
        /// </summary>
        /// <remarks>
        /// It names only the request the caller has just sent, and every frame of it names source,
        /// so it fills the four kept frames and pushes out whatever the entry was about: a
        /// one-line log came back as four frames of this and a count of the rest.
        /// </remarks>
        private static readonly Regex Delivery = new Regex(
            @"^UnityMCP\.Editor\.(Core\.(ToolInvoker|ToolCallRunner|McpHttpServer"
            + @"|McpStreamableHttpEndpoint|McpMainThreadDispatcher|FrameSequencer)"
            + @"|Handlers\.CodeExecutor)[\w<>/`+$.]*:"
            + @"|^UnityMCP\.Editor\.Tools\.EditorTools:ExecuteCode\b",
            RegexOptions.Compiled);

        /// <summary>What makes two otherwise identical lines differ: ids, times, counts.</summary>
        private static readonly Regex Varying =
            new Regex(@"[0-9a-f]{8,}|\d+\.\d+|\d+", RegexOptions.Compiled);

        /// <summary>
        /// A line reporting a problem is never folded away: two failures differing only by a
        /// number are two failures.
        /// </summary>
        private static readonly Regex Trouble =
            new Regex(@"error|exception|warning|fail|assert", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Frames kept when the trace names no source at all, as engine crashes do.</summary>
        private const int KeepWhenNoneLocated = 3;

        /// <summary>
        /// Located frames kept, nearest the failure first. In first-party code every frame names
        /// source, so keeping all of them costs more than the untrimmed trace did.
        /// </summary>
        private const int MaxLocated = 4;

        /// <summary>How many alike lines it takes before folding them is worth the note.</summary>
        private const int FoldFrom = 3;

        /// <summary>
        /// Path segments kept in a frame's location. An absolute path repeats the machine and the
        /// package root on every frame of every entry; three segments still name the file well
        /// enough to open it.
        /// </summary>
        private const int PathSegments = 3;

        /// <summary>The location at the end of a frame, so the path inside it can be shortened.</summary>
        private static readonly Regex Location =
            new Regex(@"\(at (?<path>.+):(?<line>\d+)\)\s*$", RegexOptions.Compiled);

        /// <summary>One message with its stack reduced to the frames that name source.</summary>
        public static string TrimStack(string message)
        {
            if (string.IsNullOrEmpty(message) || message.IndexOf('\n') < 0)
            {
                return message;
            }

            var lines = message.Replace("\r\n", "\n").Split('\n');
            var kept = TrimStacks(lines);
            var text = new StringBuilder();

            for (var i = 0; i < kept.Count; i++)
            {
                if (i > 0)
                {
                    text.Append('\n');
                }

                text.Append(kept[i]);
            }

            return text.ToString();
        }

        /// <summary>
        /// The same reduction over a list of lines, which is how the Editor log arrives: runs of
        /// frames are replaced by the ones naming source, plus a note counting the rest.
        /// </summary>
        public static List<string> TrimStacks(IReadOnlyList<string> lines)
        {
            var output = new List<string>(lines.Count);
            var run = new List<string>();

            void Flush()
            {
                if (run.Count == 0)
                {
                    return;
                }

                var kept = new List<string>();
                foreach (var frame in run)
                {
                    if (kept.Count == MaxLocated)
                    {
                        break;
                    }

                    if (Located.IsMatch(frame) && !LoggingCall.IsMatch(frame) && !Delivery.IsMatch(frame))
                    {
                        kept.Add(frame);
                    }
                }

                // Nothing named source, so there is no better frame to choose than the first few.
                // Where frames did name source and none survived the cut, they were all machinery
                // the caller already knows about, and standing them back in undoes the cut.
                if (kept.Count == 0 && !AnyLocated(run))
                {
                    for (var i = 0; i < run.Count && i < KeepWhenNoneLocated; i++)
                    {
                        kept.Add(run[i]);
                    }
                }

                foreach (var frame in kept)
                {
                    output.Add(Shorten(frame));
                }

                var hidden = run.Count - kept.Count;
                if (hidden > 0)
                {
                    output.Add("    (+" + hidden + " frames via " + Origins(run, kept) + ")");
                }

                run.Clear();
            }

            foreach (var line in lines)
            {
                if (Frame.IsMatch(line))
                {
                    run.Add(line);
                }
                else
                {
                    Flush();
                    output.Add(line);
                }
            }

            Flush();
            return output;
        }

        /// <summary>
        /// Lines whose shape repeats folded to their first occurrence, which is kept verbatim so
        /// the reader sees a real one rather than a pattern.
        /// </summary>
        /// <remarks>
        /// Each shape is built once and carried to the second pass. Rebuilding it there costs
        /// nearly half the work of the whole reduction: on the Editor's own Mono, 2000 lines took
        /// 27.3 ms rebuilding against 15.8 ms carrying, for 1.08 MB against 561 KB.
        /// </remarks>
        public static List<string> FoldRepeats(IReadOnlyList<string> lines, out int folded)
        {
            var shapes = new string[lines.Count];
            var counts = new Dictionary<string, int>();

            for (var i = 0; i < lines.Count; i++)
            {
                if (!Foldable(lines[i]))
                {
                    continue;
                }

                var shape = Varying.Replace(lines[i], "#");
                shapes[i] = shape;
                counts.TryGetValue(shape, out var seenSoFar);
                counts[shape] = seenSoFar + 1;
            }

            var output = new List<string>(lines.Count);
            var written = new HashSet<string>();
            folded = 0;

            for (var i = 0; i < lines.Count; i++)
            {
                var shape = shapes[i];

                // Null where the line was never foldable: a frame, or a line reporting a problem.
                if (shape == null || counts[shape] < FoldFrom)
                {
                    output.Add(lines[i]);
                    continue;
                }

                if (written.Add(shape))
                {
                    output.Add(lines[i] + "  [and " + (counts[shape] - 1) + " more like it]");
                }
                else
                {
                    folded++;
                }
            }

            return output;
        }

        /// <summary>A frame with its location cut back to the last few path segments.</summary>
        private static string Shorten(string frame)
        {
            var match = Location.Match(frame);

            if (!match.Success)
            {
                return frame;
            }

            var path = match.Groups["path"].Value.Replace('\\', '/');
            var segments = path.Split('/');

            if (segments.Length <= PathSegments)
            {
                return frame;
            }

            var tail = string.Join("/", segments, segments.Length - PathSegments, PathSegments);

            return frame.Substring(0, match.Index)
                   + "(at " + tail + ":" + match.Groups["line"].Value + ")";
        }

        private static bool AnyLocated(List<string> run)
        {
            foreach (var frame in run)
            {
                if (Located.IsMatch(frame))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Foldable(string line)
        {
            return !Frame.IsMatch(line) && !Trouble.IsMatch(line);
        }

        /// <summary>The assemblies a hidden run passed through, for the note that replaces it.</summary>
        private static string Origins(List<string> run, List<string> kept)
        {
            var names = new SortedSet<string>();

            foreach (var frame in run)
            {
                if (kept.Contains(frame))
                {
                    continue;
                }

                var name = frame.StartsWith("(wrapper ") ? "generated thunks" : frame;
                var end = name.IndexOfAny(new[] { '.', ':' });
                names.Add(end > 0 ? name.Substring(0, end) : name);
            }

            var text = new StringBuilder();
            var written = 0;

            foreach (var name in names)
            {
                if (written == 4)
                {
                    text.Append(", …");
                    break;
                }

                if (written > 0)
                {
                    text.Append(", ");
                }

                text.Append(name);
                written++;
            }

            return text.ToString();
        }
    }
}
