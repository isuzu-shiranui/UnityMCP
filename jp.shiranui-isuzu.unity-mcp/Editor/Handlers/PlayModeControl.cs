using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    internal static class PlayModeControl
    {
        public static JObject Control(JObject parameters)
        {
            var action = parameters["action"]?.ToString();
            if (string.IsNullOrEmpty(action))
            {
                return new JObject { ["error"] = "action parameter is required" };
            }

            switch (action)
            {
                case "status":
                    return GetStatus();

                case "play":
                    if (EditorApplication.isPlaying)
                    {
                        PlayModeRequest.Clear();
                        EditorApplication.isPaused = parameters["paused"]?.Value<bool>() ?? false;
                        var status = GetStatus();
                        status["message"] = "Already in play mode";
                        return status;
                    }
                    PlayModeRequest.Begin("play", parameters["paused"]?.Value<bool>() ?? false);
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "play",
                        ["paused"] = parameters["paused"]?.Value<bool>() ?? false,
                        ["message"] = "Play mode will start on next frame. Connection may be interrupted during domain reload."
                    };

                case "stop":
                    if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        PlayModeRequest.Clear();
                        var status = GetStatus();
                        status["message"] = "Not in play mode";
                        return status;
                    }
                    PlayModeRequest.Begin("stop");
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "stop",
                        ["message"] = "Play mode will stop on next frame. Connection may be interrupted during domain reload."
                    };

                case "pause":
                    if (!EditorApplication.isPlaying)
                    {
                        return NotPlaying("pause");
                    }
                    EditorApplication.isPaused = true;
                    return GetStatus();

                case "unpause":
                    if (!EditorApplication.isPlaying)
                    {
                        return NotPlaying("unpause");
                    }
                    EditorApplication.isPaused = false;
                    return GetStatus();

                case "step":
                    return Step(parameters);

                default:
                    return new JObject { ["error"] = $"Unknown action: {action}" };
            }
        }

        /// <summary>The label the deferred entry into play mode runs under.</summary>
        private const string PlayLabel = "play_mode_play";

        /// <summary>Each object's live Animator, keyed by the path it was asked for.</summary>
        /// <remarks>
        /// One that cannot be read costs its own entry, the way a batched read does: a list of
        /// objects the caller did not hand-check is the point of passing several.
        /// </remarks>
        private static JObject Animators(IEnumerable<string> objectPaths)
        {
            var reported = new JObject();

            foreach (var objectPath in objectPaths)
            {
                try
                {
                    var live = Tools.AnimatorInspectTools.LiveState(objectPath);

                    reported[objectPath] = live ?? new JObject
                    {
                        ["objectPath"] = objectPath,
                        ["error"] = "Nothing here is running an Animator with a controller on it.",
                    };
                }
                catch (McpToolException e)
                {
                    reported[objectPath] = new JObject
                    {
                        ["objectPath"] = objectPath,
                        ["error"] = e.Message,
                    };
                }
            }

            return reported;
        }

        /// <summary>
        /// Why the Editor is not playing, said to a caller who may have just asked it to play.
        /// </summary>
        /// <remarks>
        /// Entering play mode happens a frame after the request is answered, so a caller acting on
        /// that answer arrives here while isPlaying still reads false, and is told it never asked.
        /// <c>EditorApplication.isPlayingOrWillChangePlaymode</c> does not cover this window: it
        /// turns true only once the change has been applied, which is the frame after. The queued
        /// sequence is what marks the window, and it covers a request made from the Editor's own
        /// toolbar as well.
        /// </remarks>
        private static JObject NotPlaying(string verb)
        {
            if (PlayModeRequest.Pending == "play" || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new JObject
                {
                    ["error"] = $"Cannot {verb} yet: play mode was asked for and begins on the next "
                        + "Editor frame. play_mode_status says isPlaying true once it has.",
                };
            }

            return new JObject
            {
                ["error"] = $"Cannot {verb} outside of play mode. play_mode_play starts it.",
            };
        }

        /// <summary>
        /// The most frames one call will advance.
        /// </summary>
        /// <remarks>
        /// About 2.5 ms each, so the cap is a couple of seconds of the main thread. Past the
        /// synchronous window the call comes back as a job, which is the existing answer for work
        /// that takes a while, so the cap is about not holding the Editor rather than about time.
        /// </remarks>
        private const int MaxStep = 1000;

        private static JObject GetStatus()
        {
            return new JObject
            {
                ["pending"] = PlayModeRequest.Pending,
                ["refused"] = PlayModeRequest.Refused,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,

                // How far into the run this is. Without it, anything measured in seconds needed a
                // second call through reflect_read to ask Time.time.
                ["time"] = EditorApplication.isPlaying ? (double)Time.time : 0d,

                // Playing and running are not the same thing. An Editor without focus stops
                // ticking, so isPlaying stays true while nothing advances, and a caller waiting
                // for a frame to happen waits forever with no way to tell. Two readings of this
                // answer it: unchanged means the Editor is not being driven, and play_mode_step
                // is how to advance it by hand.
                ["frameCount"] = Time.frameCount,
            };
        }
        private static JObject Step(JObject parameters)
        {
            var count = parameters["count"]?.Value<int>() ?? 1;
            var seconds = parameters["seconds"]?.Value<double>();
            var maxFrames = parameters["max_frames"]?.Value<int>() ?? 2000;
            var changes = parameters["changes"]?.Value<bool>() ?? false;
            var paths = (parameters["paths"] as JArray)?.Select(t => t.ToString()).Distinct().ToArray();
            // Validate before checking play mode: an invalid request must not send the caller to
            // start play mode only to encounter the same invalid arguments on the next call.
            if (count < 1 || count > MaxStep)
                throw new McpToolException("invalid_params", "count must be between 1 and 1000.");
            if (seconds.HasValue && (parameters["count"] != null || seconds <= 0 || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value)))
                throw new McpToolException("invalid_params", "seconds must be finite and positive, and cannot be combined with count.");
            if (maxFrames < 1 || maxFrames > 100000)
                throw new McpToolException("invalid_params", "max_frames must be between 1 and 100000.");
            if (changes && (paths == null || paths.Length == 0 || paths.Length > 20))
                throw new McpToolException("invalid_params", "changes needs paths, with 1 to 20 paths.");
            if (!EditorApplication.isPlaying) return NotPlaying("step");
            EditorApplication.isPaused = true;
            var from = Time.frameCount;
            var startTime = (double)Time.time;
            var logs = new JObject();
            var roots = new Dictionary<string, UnityEngine.Object>();
            void Sample(int frame)
            {
                foreach (var path in paths)
                {
                    if (logs[path]?["error"] != null) continue;
                    try
                    {
                        if (!roots.ContainsKey(path))
                            roots[path] = Tools.ReflectTools.ResolveRoot(Tools.ReflectTools.SplitPath(path), out _, out _, out _) as UnityEngine.Object;
                        var root = roots[path];
                        var read = !ReferenceEquals(root, null) && root == null ? JValue.CreateNull()
                            : Tools.ReflectTools.Many(new[] { path })[path];
                        var error = read is JObject obj ? (string)obj["error"] : null;
                        var value = read is JObject objValue ? objValue["value"] : read;
                        logs[path] = StepChangeLog.Append((JObject)logs[path], value, frame, Time.time, error);
                    }
                    catch (Exception e)
                    {
                        logs[path] = StepChangeLog.Append((JObject)logs[path], null, frame, Time.time, e.InnerException?.Message ?? e.Message);
                    }
                }
            }
            if (changes) Sample(0);
            var limit = seconds.HasValue ? maxFrames : Math.Min(count, maxFrames);
            var steps = 0;
            // Step is synchronous: the frame has finished when it returns, so sampling here sees
            // each completed frame and the loop advances the requested number of frames.
            for (; steps < limit && EditorApplication.isPlaying;)
            {
                EditorApplication.Step();
                steps++;
                if (changes) Sample(steps);
                if (seconds.HasValue && (double)Time.time - startTime >= seconds.Value) break;
            }
            var result = GetStatus();
            result["steppedFrames"] = EditorApplication.isPlaying ? Time.frameCount - from : steps;
            result["stopReason"] = !EditorApplication.isPlaying ? "play_mode_ended"
                : seconds.HasValue ? ((double)Time.time - startTime >= seconds.Value ? "seconds" : "max_frames")
                : steps >= count ? "count" : "max_frames";
            if (changes) result["changes"] = logs;
            // Read before returning so the reported state belongs to the final stepped frame.
            else if (paths != null && paths.Length > 0) result["reads"] = Tools.ReflectTools.Many(paths);
            // Animator state-machine progress is not a property that the paths reader can reach.
            if (parameters["animators"] is JArray animators && animators.Count > 0)
                result["animators"] = Animators(animators.Select(t => t.ToString()));
            return result;
        }
    }
}
