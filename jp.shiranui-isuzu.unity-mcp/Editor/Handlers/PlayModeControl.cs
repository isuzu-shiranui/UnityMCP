using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using UnityEditor;

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
                        var status = GetStatus();
                        status["message"] = "Already in play mode";
                        return status;
                    }
                    OnTheNextFrame(() => EditorApplication.isPlaying = true, "play_mode_play");
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "play",
                        ["message"] = "Play mode will start on next frame. Connection may be interrupted during domain reload."
                    };

                case "stop":
                    if (!EditorApplication.isPlaying)
                    {
                        var status = GetStatus();
                        status["message"] = "Not in play mode";
                        return status;
                    }
                    OnTheNextFrame(() => EditorApplication.isPlaying = false, "play_mode_stop");
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "stop",
                        ["message"] = "Play mode will stop on next frame. Connection may be interrupted during domain reload."
                    };

                case "pause":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot pause outside of play mode" };
                    }
                    EditorApplication.isPaused = true;
                    return GetStatus();

                case "unpause":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot unpause outside of play mode" };
                    }
                    EditorApplication.isPaused = false;
                    return GetStatus();

                case "step":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot step outside of play mode" };
                    }
                    if (!EditorApplication.isPaused)
                    {
                        EditorApplication.isPaused = true;
                    }
                    EditorApplication.Step();
                    return GetStatus();

                default:
                    return new JObject { ["error"] = $"Unknown action: {action}" };
            }
        }

        private static JObject GetStatus()
        {
            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling
            };
        }
        /// <summary>
        /// Runs <paramref name="action"/> on the next Editor frame, keeping the Editor ticking
        /// until it has.
        /// </summary>
        /// <remarks>
        /// The work is deferred so the HTTP response is written before entering or leaving play
        /// mode reloads the domain and drops the connection. <c>EditorApplication.delayCall</c>
        /// looks like the way to do that and is not: an Editor without focus stops ticking once
        /// the request that woke it is answered, and the callback waits for a frame that never
        /// arrives. A sequence is what the loop waker watches.
        /// </remarks>
        private static void OnTheNextFrame(Action action, string label)
        {
            FrameSequencer.Run(Steps(action), label);
        }

        private static IEnumerator<FrameStep> Steps(Action action)
        {
            yield return FrameStep.Wait();

            action();

            yield return FrameStep.Done(new JObject { ["ok"] = true });
        }
    }
}
