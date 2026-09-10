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
                        var status = GetStatus();
                        status["message"] = "Already in play mode";
                        return status;
                    }
                    OnTheNextFrame(() => EditorApplication.isPlaying = true, PlayLabel);
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
                    var wanted = parameters["count"]?.Value<int>() ?? 1;

                    // Before the play-mode check: a count of 5000 is wrong whether or not anything
                    // is running, and answering "not playing" would send the caller to start play
                    // mode and hit the same wall again.
                    if (wanted < 1 || wanted > MaxStep)
                    {
                        throw new McpToolException(
                            "invalid_params",
                            $"'count' takes 1 to {MaxStep} frames. Watching something happen a "
                            + "frame at a time cost 1,255 calls once, which is why it takes more "
                            + "than one; a run this long comes back as a job.");
                    }

                    if (!EditorApplication.isPlaying)
                    {
                        return NotPlaying("step");
                    }

                    if (!EditorApplication.isPaused)
                    {
                        EditorApplication.isPaused = true;
                    }

                    var from = Time.frameCount;

                    // Step is synchronous: the frame is over by the time it returns, so a loop
                    // here really does advance that many. Measured at about 2.5 ms a frame.
                    for (var i = 0; i < wanted; i++)
                    {
                        EditorApplication.Step();
                    }

                    var stepped = GetStatus();
                    stepped["steppedFrames"] = Time.frameCount - from;

                    // Read here rather than in a call of its own. Watching something over time is
                    // a step and a look, over and over: twenty-one steps came with forty-six
                    // reads behind them, and every one of those was a round trip spent asking
                    // where the thing had got to.
                    if (parameters["paths"] is JArray watching && watching.Count > 0)
                    {
                        stepped["reads"] = Tools.ReflectTools.Many(
                            watching.Select(t => t.ToString()).ToArray());
                    }

                    // What a state machine is doing is not a property, so 'paths' cannot reach it
                    // and the look after each step was a whole animator_inspect.
                    if (parameters["animators"] is JArray animators && animators.Count > 0)
                    {
                        stepped["animators"] = Animators(animators.Select(t => t.ToString()));
                    }

                    return stepped;

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
            if (FrameSequencer.IsRunning(PlayLabel) || EditorApplication.isPlayingOrWillChangePlaymode)
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
