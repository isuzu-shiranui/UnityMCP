using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Answering "is the picture right", and "what is drawing it".
    /// </summary>
    internal static class RenderTools
    {
        /// <summary>Every camera in the open scenes, inactive ones included.</summary>
        /// <remarks>
        /// The overload without a FindObjectsSortMode is absent up to 6000.3 and present from
        /// 6000.5, where the one taking it becomes obsolete. Which of the two 6000.4 has is not
        /// established, so the older branch suppresses the warning the way the engine suppresses
        /// it around its own calls; moving the guard down a version instead would fail to compile
        /// wherever the replacement is not there yet.
        /// </remarks>
        private static Camera[] AllCameras()
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
#else
#pragma warning disable CS0618
            return UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Cameras reported. Each runs to about a kilobyte with its matrices, and a cinematic
        /// scene keeps one per shot.
        /// </summary>
        private const int MaxCameras = 50;

        /// <summary>Frames one call may sample. Beyond this the caller is waiting, not measuring.</summary>
        private const int MaxProfiledFrames = 300;

        /// <summary>Markers one call may name, each of which costs a recorder for the window.</summary>
        private const int MaxMarkers = 20;

        [McpTool(
            "render_profile_frame",
            "Where a frame's time and its garbage go, sampled over a window of frames. This is the " +
            "answer to 'why is it slow': CPU total, main thread, render thread and GPU, each as a " +
            "median and a worst frame, plus the bytes allocated per frame. Counts of draw calls " +
            "and triangles are not here - render_stats reports the last frame's. Name markers to " +
            "sample your own ProfilerMarkers alongside. The numbers are worth reading in play " +
            "mode; in edit mode they describe the Editor drawing its own windows, and the reply " +
            "says which it measured so one is not read as the other.",
            Idempotency = McpIdempotency.Safe,
            MaxResultSizeChars = 60000)]
        public static object ProfileFrame(
            [McpArg("frames", "How many frames to sample. A longer window smooths a spike out of " +
                              "the median; the worst frame is reported either way.")]
            int frames = 30,
            [McpArg("markers", "ProfilerMarker names to sample as well, e.g. 'MySystem.Tick'.")]
            string[] markers = null)
        {
            if (frames < 1 || frames > MaxProfiledFrames)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'frames' takes 1 to {MaxProfiledFrames}; {frames} is outside that. "
                    + "A longer window is a longer wait, not a better measurement.");
            }

            if (markers != null && markers.Length > MaxMarkers)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'markers' takes at most {MaxMarkers}; {markers.Length} were given.");
            }

            return new DeferredToolResult(
                FrameSequencer.Run(ProfileSequence(frames, markers), "render_profile_frame"));
        }

        private static IEnumerator<FrameStep> ProfileSequence(int frames, string[] markers)
        {
            var profiler = new FrameProfiler(frames, markers);

            try
            {
                var missing = profiler.Unavailable();

                // The recorders collect from the frame they start, so the window has to pass before
                // anything is read; reading now returns a window of nothing.
                for (var i = 0; i < frames; i++)
                {
                    yield return FrameStep.Wait();
                }

                var counters = profiler.Read();

                var result = new JObject
                {
                    ["requestedFrames"] = frames,
                    ["playing"] = EditorApplication.isPlaying,
                    ["counters"] = counters,
                };

                var notes = new JArray();

                if (!EditorApplication.isPlaying)
                {
                    notes.Add("Edit mode: these are the Editor's own frames, not the game's. "
                        + "play_mode_play first for figures that describe the game.");
                }

                if (profiler.Silent.Count > 0)
                {
                    result["silent"] = new JArray(profiler.Silent);
                    notes.Add("The frame timing manager reported nothing, so "
                        + string.Join(", ", profiler.Silent) + " are left out rather than given as "
                        + "zero. It needs Frame Timing Stats on in Player Settings"
                        + (FrameProfiler.FrameTimingSettingOn ? " - it is on here" : " - it is off here")
                        + ", and in the Editor that is necessary without being enough. A built "
                        + "player is where these read true.");
                }

                if (missing.Count > 0)
                {
                    result["unavailable"] = new JArray(missing);
                }

                if (notes.Count > 0)
                {
                    result["notes"] = notes;
                }

                yield return FrameStep.Done(result);
            }
            finally
            {
                profiler.Dispose();
            }
        }

        /// <summary>Objects one call may hide, each of which is resolved and walked for renderers.</summary>
        private const int MaxHidden = 50;

        /// <summary>Frames one call may let pass between changing the scene and capturing it.</summary>
        private const int MaxSettleFrames = 60;

        [McpTool(
            "render_capture_ab",
            "Capture the frame as it is, capture it again with the named objects hidden, and report "
            + "what changed - together with how much changes on its own. Two captures of an "
            + "unchanged scene are not identical: animation, water, particles and temporal "
            + "anti-aliasing all move between frames, and a difference smaller than that movement "
            + "means nothing. The reply reports the noise alongside the change so the two can be "
            + "told apart, which looking at the two pictures cannot do. The objects are put back "
            + "before the call answers, and also if it is cancelled or the domain reloads - which "
            + "is the other reason to use this rather than hiding them from execute_code, where "
            + "nothing restores them if the Editor stops.",
            Idempotency = McpIdempotency.Unsafe,
            MaxResultSizeChars = 60000)]
        public static object CaptureAb(
            [McpArg("hide", "Hierarchy paths of the objects to hide for the second capture. Every "
                            + "Renderer on them and under them is switched off.", Required = true)]
            string[] hide = null,
            [McpArg("save_path_prefix", "Where the three PNGs go. '_before', '_repeat' and '_after' "
                                        + "are appended. Required: three pictures inline would cost "
                                        + "about as much as the rest of the session.", Required = true)]
            string savePathPrefix = null,
            [McpArg("view", "'game' renders through a scene camera, 'scene' through the scene view's.")]
            string view = "game",
            [McpArg("camera", "Path or name of the camera to render through. Defaults to the main one.")]
            string camera = null,
            [McpArg("max_size", "Longest edge, when width and height are not given.")]
            int maxSize = 1024,
            [McpArg("width", "Width to capture at.")]
            int? width = null,
            [McpArg("height", "Height to capture at.")]
            int? height = null,
            [McpArg("settle_frames", "Editor frames to let pass between captures. Anything driven by "
                                     + "a coroutine, a simulation or a temporal effect needs at least "
                                     + "a frame to catch up, and it is also the window the noise is "
                                     + "measured over, so it cannot be zero.")]
            int settleFrames = 2,
            [McpArg("threshold", "Largest per-channel difference, 0-255, at or below which a pixel "
                                 + "counts as unchanged.")]
            int threshold = 2,
            [McpArg("grid", "Report the difference over a grid this many cells across. Clamped to 1-32.")]
            int grid = 8)
        {
            if (hide == null || hide.Length == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "'hide' takes the hierarchy paths of the objects to switch off for the second "
                    + "capture. With none there is no second capture to make, and capture_screenshot "
                    + "is what takes a single picture.");
            }

            if (hide.Length > MaxHidden)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'hide' takes at most {MaxHidden} objects; {hide.Length} were given.");
            }

            if (string.IsNullOrWhiteSpace(savePathPrefix))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'save_path_prefix' is where the three PNGs go, for example "
                    + "'Temp/foam'. They are not returned inline because three pictures in one "
                    + "reply cost more than the answer is worth.");
            }

            if (settleFrames < 1 || settleFrames > MaxSettleFrames)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'settle_frames' takes 1 to {MaxSettleFrames}; {settleFrames} is outside that. "
                    + "At zero the two captures of the unchanged scene are taken in the same frame, "
                    + "so they always match and the noise reads as none whatever the scene is doing.");
            }

            // Resolved before the sequence starts so an unknown path is refused in this reply
            // rather than a frame later, where it arrives as a failed job.
            var renderers = Hidden(hide);

            return new DeferredToolResult(
                FrameSequencer.Run(
                    AbSequence(renderers, savePathPrefix, view, camera, maxSize, width, height, settleFrames, threshold, grid),
                    "render_capture_ab"));
        }

        /// <summary>Every Renderer on the named objects and under them.</summary>
        private static Renderer[] Hidden(string[] paths)
        {
            var found = new List<Renderer>();

            foreach (var path in paths)
            {
                var target = ObjectResolve.Object(path, null, "hide", null);

                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                {
                    if (!found.Contains(renderer))
                    {
                        found.Add(renderer);
                    }
                }
            }

            if (found.Count == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "None of the objects in 'hide' carries a Renderer, on itself or under it, so "
                    + "hiding them would change nothing. Name the object that draws, which "
                    + "scene_browse_hierarchy reports with its components.");
            }

            return found.ToArray();
        }

        private static IEnumerator<FrameStep> AbSequence(
            Renderer[] renderers, string prefix, string view, string camera, int maxSize,
            int? width, int? height, int settleFrames, int threshold, int grid)
        {
            var restore = new List<KeyValuePair<Renderer, bool>>();

            try
            {
                var before = Shoot(prefix + "_before.png", view, camera, maxSize, width, height);

                for (var i = 0; i < settleFrames; i++)
                {
                    yield return FrameStep.Wait();
                }

                var repeat = Shoot(prefix + "_repeat.png", view, camera, maxSize, width, height);

                Hide(renderers, restore);

                for (var i = 0; i < settleFrames; i++)
                {
                    yield return FrameStep.Wait();
                }

                var after = Shoot(prefix + "_after.png", view, camera, maxSize, width, height);

                // Both pairs are the same number of frames apart, so the two numbers are
                // comparable. Measuring the change against the first capture instead would span
                // twice the window and count twice the movement as change.
                var noise = Compare(before, repeat, threshold, grid);
                var change = Compare(repeat, after, threshold, grid);

                var noisePixels = (long)noise["changedPixels"];
                var changePixels = (long)change["changedPixels"];

                var result = new JObject
                {
                    ["before"] = before,
                    ["repeat"] = repeat,
                    ["after"] = after,
                    ["hiddenRenderers"] = renderers.Length,
                    ["noise"] = noise,
                    ["change"] = change,
                    ["measuredOver"] = $"{settleFrames} frame(s); noise is before against repeat, "
                                       + "change is repeat against after",
                    ["changeOverNoise"] = Math.Round((double)changePixels / Math.Max(1L, noisePixels), 2),
                };

                if (changePixels <= noisePixels)
                {
                    result["note"] = "Hiding these objects changed no more of the picture than two "
                                     + "captures of the unchanged scene differ by, so this answers "
                                     + "nothing about them. Something in the scene is moving between "
                                     + "frames; stop it, or compare a part of the picture the "
                                     + "movement does not reach.";
                }

                yield return FrameStep.Done(result);
            }
            finally
            {
                Show(restore);
            }
        }

        /// <summary>
        /// Switches every renderer off, recording what each one was.
        /// </summary>
        /// <remarks>
        /// The renderers are resolved a frame or more before this runs, so one of them can already
        /// be gone by the time it does.
        /// </remarks>
        internal static void Hide(Renderer[] renderers, List<KeyValuePair<Renderer, bool>> restore)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                restore.Add(new KeyValuePair<Renderer, bool>(renderer, renderer.enabled));
                renderer.enabled = false;
            }
        }

        /// <summary>
        /// Puts back exactly what <see cref="Hide"/> recorded.
        /// </summary>
        /// <remarks>
        /// Switching everything back on instead would turn on a renderer the scene had off, which
        /// is a change to the project that outlives the call and is not undoable. A renderer
        /// destroyed while the call was in flight compares equal to null and is skipped.
        /// </remarks>
        internal static void Show(List<KeyValuePair<Renderer, bool>> restore)
        {
            foreach (var entry in restore)
            {
                if (entry.Key != null)
                {
                    entry.Key.enabled = entry.Value;
                }
            }
        }

        /// <summary>Takes one capture and answers with the path it was written to.</summary>
        private static string Shoot(string path, string view, string camera, int maxSize, int? width, int? height)
        {
            var shot = ScreenshotCapture.Capture(ToolArgs.Of(
                ("view", view),
                ("camera", camera),
                ("maxSize", maxSize),
                ("width", width),
                ("height", height),
                ("savePath", path)));

            // The camera-based path reports a missing camera or an unusable size as a key on an
            // otherwise successful reply; unread, it would come back as a comparison of two files
            // that were never written.
            if (shot["error"] != null)
            {
                throw new McpToolException("tool_failed", (string)shot["error"], 500);
            }

            return (string)shot["path"];
        }

        [McpTool(
            "render_capture_buffer",
            "Save one of the render pipeline's intermediate targets as a picture: the depth, "
            + "normals, motion or opaque texture. This is what answers 'what does the effect "
            + "actually see', which the finished frame cannot show - a foam or fog or outline pass "
            + "reads the depth texture, and that is not always the silhouette on screen. Depth "
            + "comes back as grey shading plus the nearest and farthest distance in metres, so a "
            + "pixel can be read as a number. A buffer the pipeline is not producing is refused by "
            + "name, with the setting that turns it on; captured anyway it would be a picture of "
            + "zeroes, which for depth reads as an empty scene. Scriptable pipelines only.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject CaptureBuffer(
            [McpArg("buffer", "Which target: depth, normals, motion or opaque.", Required = true)]
            string buffer = null,
            [McpArg("view", "'game' renders through a scene camera, 'scene' through the scene view's.")]
            string view = "game",
            [McpArg("camera", "Path or name of the camera to render through. Defaults to the main one.")]
            string camera = null,
            [McpArg("max_size", "Longest edge of the picture, when width and height are not given. "
                                + "The capture is always made at the size the pipeline chose, which "
                                + "the reply reports as capturedAt when it differs.")]
            int maxSize = 1024,
            [McpArg("width", "Width of the picture.")]
            int? width = null,
            [McpArg("height", "Height of the picture.")]
            int? height = null,
            [McpArg("save_path", "Write the PNG here instead of returning it inline. Pass the path "
                                 + "it answers with to render_compare.")]
            string savePath = null)
        {
            return CameraBufferCapture.Capture(buffer, view, camera, maxSize, width, height, savePath);
        }

        [McpTool(
            "render_compare",
            "Compare two captured images and report how they differ, in numbers. Use this instead of " +
            "looking at both pictures: capture with save_path, toggle the thing under test, capture " +
            "again, then compare. Absolute colours are post-tonemap and not worth trusting, so what " +
            "this reports is change — how many pixels moved, by how much, and where. The bounding " +
            "box and the per-cell grid are only present when at least one pixel changed. Both " +
            "images have to be the same size.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Compare(
            [McpArg("before", "Path to the first PNG, from capture_screenshot's save_path.", Required = true)]
            string before = null,
            [McpArg("after", "Path to the second PNG.", Required = true)]
            string after = null,
            [McpArg("threshold", "Largest per-channel difference, 0-255, at or below which a pixel " +
                                 "counts as unchanged. Only red, green and blue are compared; alpha " +
                                 "is ignored.")]
            int threshold = 2,
            [McpArg("grid", "Report the difference over a grid this many cells across, to localise " +
                            "it. Clamped to 1-32.")]
            int grid = 8)
        {
            var a = LoadPng(before, "before");
            Texture2D b;

            try
            {
                b = LoadPng(after, "after");
            }
            catch
            {
                // Without this the first texture outlives every failed call. A caller comparing
                // against a path that does not exist yet — polling for a capture, say — would
                // leak one texture per attempt and never learn why memory grew.
                UnityEngine.Object.DestroyImmediate(a);
                throw;
            }

            if (a.width != b.width || a.height != b.height)
            {
                // Read the sizes before destroying: interpolating them afterwards throws
                // MissingReferenceException and the caller gets tool_failed instead of the
                // explanation.
                var sizes = $"{a.width}x{a.height} and {b.width}x{b.height}";

                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);

                throw new McpToolException(
                    "invalid_params",
                    $"The images are different sizes ({sizes}). " +
                    "Capture both at the same size, or the comparison means nothing.");
            }

            try
            {
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();

                var cells = Math.Max(1, Math.Min(grid, 32));
                var cellCounts = new int[cells * cells];

                long changed = 0;
                long sumDelta = 0;
                var maxDelta = 0;
                int minX = a.width, minY = a.height, maxX = -1, maxY = -1;

                for (var i = 0; i < pa.Length; i++)
                {
                    var dr = Math.Abs(pa[i].r - pb[i].r);
                    var dg = Math.Abs(pa[i].g - pb[i].g);
                    var db = Math.Abs(pa[i].b - pb[i].b);
                    var delta = Math.Max(dr, Math.Max(dg, db));

                    if (delta <= threshold)
                    {
                        continue;
                    }

                    changed++;
                    sumDelta += delta;
                    maxDelta = Math.Max(maxDelta, delta);

                    var x = i % a.width;
                    var y = i / a.width;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    cellCounts[(y * cells / a.height) * cells + (x * cells / a.width)]++;
                }

                var total = (long)a.width * a.height;

                var result = new JObject
                {
                    ["width"] = a.width,
                    ["height"] = a.height,
                    ["totalPixels"] = total,
                    ["changedPixels"] = changed,
                    ["changedRatio"] = total == 0 ? 0d : Math.Round((double)changed / total, 6),
                    ["identical"] = changed == 0,
                    ["meanDelta"] = changed == 0 ? 0d : Math.Round((double)sumDelta / changed, 2),
                    ["maxDelta"] = maxDelta,
                    ["threshold"] = threshold,
                };

                if (changed > 0)
                {
                    result["boundingBox"] = new JObject
                    {
                        ["x"] = minX,
                        ["y"] = minY,
                        ["width"] = maxX - minX + 1,
                        ["height"] = maxY - minY + 1,
                    };

                    // A grid of counts is a picture of where the change is, in a few dozen numbers
                    // rather than a few hundred kilobytes. Rows run top to bottom.
                    var rows = new JArray();

                    for (var row = cells - 1; row >= 0; row--)
                    {
                        var cols = new JArray();
                        var high = Span(row, a.height, cells);

                        for (var col = 0; col < cells; col++)
                        {
                            // Each cell's own pixel count. A single floor(w/cells)*floor(h/cells)
                            // is the smallest cell, and the pixels left over by that division are
                            // spread across the others by the binning above — so every larger cell
                            // divided by it came out above 1, which a ratio cannot be.
                            var wide = Span(col, a.width, cells);
                            var pixels = Math.Max(1, wide * high);

                            cols.Add(Math.Round((double)cellCounts[row * cells + col] / pixels, 3));
                        }

                        rows.Add(cols);
                    }

                    result["gridChangedRatio"] = rows;
                }

                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        /// <summary>How many pixels fall in one row or column of the grid.</summary>
        /// <remarks>
        /// The same split the binning does: a pixel lands in cell <c>index</c> when
        /// <c>index == pixel * cells / total</c>, so the cell starts at the first pixel for which
        /// that holds. Derived from the binning rather than assumed, because a denominator that
        /// disagrees with it is what produced ratios above 1.
        /// </remarks>
        private static int Span(int index, int total, int cells)
        {
            var start = (index * total + cells - 1) / cells;
            var end = ((index + 1) * total + cells - 1) / cells;

            return end - start;
        }

        [McpTool(
            "render_stats",
            "Report what the last drawn frame cost: draw calls, SetPass calls, triangles, " +
            "vertices, shadow casters and how much batching collapsed. This is the number to " +
            "take before and after a change that is meant to make a scene cheaper, so the claim " +
            "rests on a measurement rather than on the shape of the fix. It covers the whole " +
            "Game view across every open scene, not one object, and it is the last frame Unity " +
            "drew: nothing redraws a Game view that is closed, or one sitting behind another tab, " +
            "which 'gameView' in the reply says. A first reading after bringing one forward can " +
            "still predate the change, so take two. A reading taken after advancing several " +
            "frames can cover more than one of them: stepping five frames and reading gave 80 " +
            "draw calls where a single step gives 16, so two readings are only comparable when " +
            "the same number of frames was stepped before each. Step one frame before reading " +
            "when the figure has to mean one frame. " +
            "Frame and render times are not reported; Unity marks them obsolete and they read as " +
            "nonsense in the Editor.",
            Idempotency = McpIdempotency.Safe,
            Group = "rendering")]
        public static JObject RenderStats()
        {
            return new JObject
            {
                ["drawCalls"] = UnityStats.drawCalls,
                ["setPassCalls"] = UnityStats.setPassCalls,
                ["triangles"] = UnityStats.triangles,
                ["vertices"] = UnityStats.vertices,
                ["shadowCasters"] = UnityStats.shadowCasters,

                // What batching took off the draw call count, and by which route. A scene whose
                // cost sits in draw calls is usually one where none of these moved.
                ["batching"] = Batching(),

                ["renderTextures"] = new JObject
                {
                    ["count"] = UnityStats.renderTextureCount,
                    ["bytes"] = UnityStats.renderTextureBytes,
                    ["changes"] = UnityStats.renderTextureChanges,
                },

                ["screen"] = UnityStats.screenRes,

                // Whether these figures can be current at all. Nothing redraws a Game view that
                // is not there, and nothing redraws one sitting behind another tab either: a
                // reading taken then was 24 draw calls where bringing the view forward gave 592.
                // Open was not enough to say, so both are said.
                ["gameView"] = GameViewState(),
            };
        }

        /// <summary>Whether a Game view is there, and whether it is the one being drawn.</summary>
        /// <remarks>
        /// Frontmost is read off the dock rather than from focus: a docked view keeps focus while
        /// another tab covers it, so focus says yes for a view that has not repainted in minutes.
        /// The dock's own field is internal, so a version that moves it leaves 'frontmost' out
        /// rather than guessing.
        /// </remarks>
        private static JObject GameViewState()
        {
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || window.GetType().Name != "GameView")
                {
                    continue;
                }

                var state = new JObject { ["open"] = true };
                var frontmost = Frontmost(window);

                if (frontmost.HasValue)
                {
                    state["frontmost"] = frontmost.Value;

                    if (!frontmost.Value)
                    {
                        state["note"] = "The Game view is behind another tab, so it is not being "
                                        + "redrawn and these figures are from whenever it last was. "
                                        + "Bring it forward and read again.";
                    }
                }

                return state;
            }

            return new JObject
            {
                ["open"] = false,
                ["note"] = "No Game view is open, so nothing is being drawn and these figures are "
                           + "left over from whenever one last was.",
            };
        }

        /// <summary>Whether this window is the visible tab of its dock, or null when unknowable.</summary>
        private static bool? Frontmost(EditorWindow window)
        {
            try
            {
                var parent = typeof(EditorWindow)
                    .GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(window);

                var actual = parent?.GetType()
                    .GetProperty("actualView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(parent) as EditorWindow;

                return actual == null ? (bool?)null : actual == window;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The batching counters this Unity version has.</summary>
        /// <remarks>
        /// Read by name rather than compiled against: srpBatcherDrawCalls is on UnityStats in
        /// 6000.5 and not in 6000.0, and naming it directly stops the package building on the
        /// older one. A version without a counter simply does not report it.
        /// </remarks>
        private static JObject Batching()
        {
            var batching = new JObject();

            foreach (var name in new[]
                     {
                         "dynamicBatches", "dynamicBatchedDrawCalls",
                         "staticBatches", "staticBatchedDrawCalls",
                         "instancedBatches", "instancedBatchedDrawCalls",
                         "srpBatcherDrawCalls",
                     })
            {
                var property = typeof(UnityStats).GetProperty(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (property != null && property.GetValue(null) is int count)
                {
                    batching[name] = count;
                }
            }

            return batching;
        }

        [McpTool(
            "render_pipeline_info",
            "Report what is actually drawing: the render pipeline asset in force, colour space, MSAA " +
            "sample count, graphics API, shadow and batching settings, and quality level. Read this " +
            "first when a shader behaves differently than expected — the quality level's pipeline " +
            "override is a common surprise. HDR is not a project-wide setting and is not reported " +
            "here; render_camera_info gives it per camera.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject PipelineInfo()
        {
            var quality = QualitySettings.renderPipeline;
            var graphics = GraphicsSettings.defaultRenderPipeline;
            var active = quality != null ? quality : graphics;

            return new JObject
            {
                ["activePipeline"] = active == null ? "Built-in" : active.GetType().Name,
                ["activePipelineAsset"] = active == null ? null : (JToken)AssetDatabase.GetAssetPath(active),
                // Two places can name a pipeline and the quality level wins. Reporting both is the
                // difference between "my URP settings do nothing" being a mystery and being obvious.
                ["defaultPipelineAsset"] = graphics == null ? null : (JToken)AssetDatabase.GetAssetPath(graphics),
                ["qualityPipelineAsset"] = quality == null ? null : (JToken)AssetDatabase.GetAssetPath(quality),
                ["qualityLevel"] = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()),
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["graphicsApi"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDevice"] = SystemInfo.graphicsDeviceName,
                ["shaderLevel"] = SystemInfo.graphicsShaderLevel,
                ["supportsComputeShaders"] = SystemInfo.supportsComputeShaders,
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["anisotropicFiltering"] = QualitySettings.anisotropicFiltering.ToString(),
                ["shadowResolution"] = QualitySettings.shadowResolution.ToString(),
                ["shadowDistance"] = QualitySettings.shadowDistance,
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
#if UNITY_2023_1_OR_NEWER
                ["batchingStatic"] = PlayerSettings.GetStaticBatchingForPlatform(EditorUserBuildSettings.activeBuildTarget),
#else
                ["batchingStatic"] = JValue.CreateNull(),
#endif
            };
        }

        [McpTool(
            "render_camera_info",
            "Report the cameras and their matrices. The view and projection matrices are here so a " +
            "value read off a screenshot can be checked against one computed on the CPU — screenshot " +
            "colours are post-tonemap and cannot settle an argument on their own. Every camera in " +
            "the open scenes is reported, disabled ones included; read each entry's enabled field " +
            "to tell which are drawing.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject CameraInfo(
            [McpArg("name", "Only report cameras whose name contains this text, ignoring case. " +
                            "Without it every camera in the open scenes is reported, inactive " +
                            "ones included.")]
            string name = null,
            [McpArg("include_matrices", "Include the view and projection matrices, row-major.")]
            bool includeMatrices = true)
        {
            var cameras = AllCameras()
                .Where(c => string.IsNullOrWhiteSpace(name)
                            || c.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(c => c.isActiveAndEnabled)
                .ThenBy(c => c.depth)
                .ToArray();

            if (cameras.Length == 0)
            {
                throw new McpToolException(
                    "not_found",
                    string.IsNullOrWhiteSpace(name)
                        ? "No cameras in the open scenes."
                        : $"No camera named '{name}'.");
            }

            var shown = cameras.Take(MaxCameras).ToArray();

            var list = new JArray(shown.Select(c =>
            {
                var entry = new JObject
                {
                    ["name"] = c.name,
                    ["path"] = ObjectResolve.PathOf(c.gameObject),
                    ["enabled"] = c.isActiveAndEnabled,
                    ["depth"] = c.depth,
                    ["orthographic"] = c.orthographic,
                    ["fieldOfView"] = c.fieldOfView,
                    ["nearClipPlane"] = c.nearClipPlane,
                    ["farClipPlane"] = c.farClipPlane,
                    ["cullingMask"] = c.cullingMask,
                    ["clearFlags"] = c.clearFlags.ToString(),
                    ["allowHDR"] = c.allowHDR,
                    ["allowMSAA"] = c.allowMSAA,
                    ["targetTexture"] = c.targetTexture == null ? null : (JToken)c.targetTexture.name,
                    ["pixelWidth"] = c.pixelWidth,
                    ["pixelHeight"] = c.pixelHeight,
                };

                if (includeMatrices)
                {
                    entry["worldToCameraMatrix"] = Matrix(c.worldToCameraMatrix);
                    entry["projectionMatrix"] = Matrix(c.projectionMatrix);
                    // What the shader actually receives: the platform flips and depth range are
                    // applied here, and forgetting that is why a CPU replica disagrees.
                    entry["gpuProjectionMatrix"] = Matrix(
                        GL.GetGPUProjectionMatrix(c.projectionMatrix, c.targetTexture != null));
                }

                return (object)entry;
            }).ToArray());

            // Measured rather than asserted: whether FindObjectsByType surfaces the Scene View's
            // own cameras depends on their hide flags, which have moved between Unity versions.
            var sceneViewCameras = SceneView.GetAllSceneCameras();

            return new JObject
            {
                ["count"] = cameras.Length,
                ["cameras"] = list,
                ["sceneViewCameraIncluded"] = cameras.Any(c => Array.IndexOf(sceneViewCameras, c) >= 0),
            };
        }

        private static JArray Matrix(Matrix4x4 m)
        {
            var rows = new JArray();

            for (var r = 0; r < 4; r++)
            {
                rows.Add(new JArray(
                    (object)Math.Round(m[r, 0], 6),
                    (object)Math.Round(m[r, 1], 6),
                    (object)Math.Round(m[r, 2], 6),
                    (object)Math.Round(m[r, 3], 6)));
            }

            return rows;
        }

        private static Texture2D LoadPng(string path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", $"'{argumentName}' is required.");
            }

            var full = Path.GetFullPath(path);

            if (!File.Exists(full))
            {
                throw new McpToolException(
                    "not_found",
                    $"No file at '{path}'. Capture one with capture_screenshot and its save_path argument.");
            }

            var bytes = File.ReadAllBytes(full);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            try
            {
                if (!texture.LoadImage(bytes))
                    throw new McpToolException("invalid_params", $"'{path}' is not an image Unity can read.");
                return texture;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }
    }
}
