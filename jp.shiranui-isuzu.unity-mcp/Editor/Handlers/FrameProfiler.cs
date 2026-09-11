using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using Unity.Profiling;

using UnityEditor;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Where a frame's time and its garbage went, sampled over a window of frames.
    /// </summary>
    /// <remarks>
    /// Counts of draw calls, batches and triangles are not here; render_stats already reports the
    /// last frame's. What this adds is time, which nothing else in the tool set can answer.
    /// </remarks>
    internal sealed class FrameProfiler : System.IDisposable
    {
        /// <summary>
        /// What a frame is judged by. Named rather than discovered: 6,195 counters are available in
        /// an Editor, and a reply that carried them would answer nothing.
        /// </summary>
        /// <remarks>
        /// <c>Accumulates</c> separates what a frame spends from what the process is holding.
        /// Summing thirty frames of "GC Used Memory" reports twenty-five gigabytes of a heap that
        /// never grew, so a total is only offered where adding frames together means something.
        /// </remarks>
        private static readonly (string Category, string Counter, string Key, bool Nanoseconds, bool Accumulates)[] Watched =
        {
            ("Render", "CPU Total Frame Time", "cpuFrameMs", true, false),
            ("Memory", "GC Allocated In Frame", "gcAllocatedBytes", false, true),
            ("Memory", "GC Allocation In Frame Count", "gcAllocations", false, true),
            ("Memory", "GC Used Memory", "gcUsedBytes", false, false),
            ("Memory", "Total Used Memory", "totalUsedBytes", false, false),
        };

        /// <summary>
        /// Counters fed by the frame timing manager. Each is a valid recorder that samples zero on
        /// every frame unless the manager is feeding it, which in an Editor it usually is not:
        /// turning on Frame Timing Stats is necessary and, in the Editor, not sufficient. A zero
        /// here is indistinguishable from a frame that cost nothing, so a run of them is reported
        /// as silence rather than as a measurement.
        /// </summary>
        private static readonly (string Category, string Counter, string Key)[] FrameTimed =
        {
            ("Render", "CPU Main Thread Frame Time", "mainThreadMs"),
            ("Render", "CPU Render Thread Frame Time", "renderThreadMs"),
            ("Render", "GPU Frame Time", "gpuFrameMs"),
        };

        /// <summary>Counters that were asked for but never reported anything but zero.</summary>
        public List<string> Silent { get; } = new();

        public static bool FrameTimingSettingOn => PlayerSettings.enableFrameTimingStats;

        private readonly List<(string Key, bool Nanoseconds, bool Accumulates, ProfilerRecorder Recorder)> recorders = new();
        private readonly HashSet<string> frameTimed = new();

        public FrameProfiler(int frames, IReadOnlyList<string> markers)
        {
            foreach (var (category, counter, key, nanoseconds, accumulates) in Watched)
            {
                this.recorders.Add((key, nanoseconds, accumulates,
                    ProfilerRecorder.StartNew(new ProfilerCategory(category), counter, frames)));
            }

            foreach (var (category, counter, key) in FrameTimed)
            {
                this.recorders.Add((key, true, false,
                    ProfilerRecorder.StartNew(new ProfilerCategory(category), counter, frames)));
                this.frameTimed.Add(key);
            }

            if (markers == null)
            {
                return;
            }

            // A named marker is looked up across every category, because a caller who knows the name
            // of a marker rarely knows which category the Editor files it under.
            foreach (var marker in markers)
            {
                this.recorders.Add((marker, true, false,
                    ProfilerRecorder.StartNew(ProfilerCategory.Scripts, marker, frames)));
            }
        }

        /// <summary>Counters that resolved to nothing in this Editor, so the reply can say so.</summary>
        public List<string> Unavailable()
        {
            var missing = new List<string>();

            foreach (var (key, _, _, recorder) in this.recorders)
            {
                if (!recorder.Valid)
                {
                    missing.Add(key);
                }
            }

            return missing;
        }

        public JObject Read()
        {
            var result = new JObject();

            foreach (var (key, nanoseconds, accumulates, recorder) in this.recorders)
            {
                if (!recorder.Valid || recorder.Count == 0)
                {
                    continue;
                }

                var samples = new List<long>(recorder.Count);
                long sum = 0;

                for (var i = 0; i < recorder.Count; i++)
                {
                    var sample = recorder.GetSample(i).Value;
                    samples.Add(sample);
                    sum += sample;
                }

                samples.Sort();

                var median = Scaled(samples[samples.Count / 2], nanoseconds);
                var worst = Scaled(samples[samples.Count - 1], nanoseconds);

                // A frame-timing counter the manager is not feeding reports zero on every frame,
                // and a handful of stray nanoseconds rounds to the same zero. Either way it says
                // nothing about the frame, so it is named as silent rather than printed as 0.0.
                if (this.frameTimed.Contains(key) && worst.Value<double>() == 0d)
                {
                    this.Silent.Add(key);
                    continue;
                }

                var entry = new JObject
                {
                    ["frames"] = samples.Count,
                    ["median"] = median,
                    ["worst"] = worst,
                };

                if (accumulates)
                {
                    entry["total"] = Scaled(sum, nanoseconds);
                }

                result[key] = entry;
            }

            return result;
        }

        /// <summary>Nanoseconds are what the time counters carry; nobody reads a frame in them.</summary>
        private static JToken Scaled(long value, bool nanoseconds) =>
            nanoseconds ? new JValue(System.Math.Round(value / 1_000_000.0, 3)) : new JValue(value);

        public void Dispose()
        {
            foreach (var (_, _, _, recorder) in this.recorders)
            {
                recorder.Dispose();
            }

            this.recorders.Clear();
        }
    }
}
