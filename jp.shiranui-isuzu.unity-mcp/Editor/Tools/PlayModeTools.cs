using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Play mode control, one tool per action.
    /// </summary>
    /// <remarks>
    /// One tool per action rather than one taking an action name, so that <see cref="Status"/>
    /// can be Safe, and therefore retryable, while the actions that change the play state stay
    /// Unsafe. A single tool would have to take the stricter classification of the two.
    /// </remarks>
    internal static class PlayModeTools
    {
        [McpTool(
            "play_mode_status",
            "Report whether the Editor is currently playing, paused, or compiling, and the frame " +
            "it is on. pending tracks play/stop through reload and the first play frame; refused gives the latest refusal. Playing and running are not the same: an Editor without focus stops " +
            "ticking, so 'isPlaying' stays true while nothing advances. Read 'frameCount' twice " +
            "to tell the difference, and use play_mode_step to advance it by hand.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Status()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "status")));
        }

        [McpTool(
            "play_mode_play",
            "Enter play mode. Takes effect on the next Editor frame. Unless Enter Play Mode " +
            "Settings has Reload Domain turned off, this reloads the domain and the MCP connection " +
            "briefly drops and reconnects.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Play([McpArg("paused", "Start paused after one completed frame.")] bool paused = false)
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "play"), ("paused", paused)));
        }

        [McpTool(
            "play_mode_stop",
            "Leave play mode. Takes effect on the next Editor frame. Unless Enter Play Mode " +
            "Settings has Reload Domain turned off, this reloads the domain and the MCP connection " +
            "briefly drops and reconnects.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Stop()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "stop")));
        }

        [McpTool(
            "play_mode_pause",
            "Pause play mode. Outside play mode the call fails with an error saying so.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Pause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "pause")));
        }

        [McpTool(
            "play_mode_unpause",
            "Resume a paused play mode. Outside play mode the call fails with an error saying so.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Unpause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "unpause")));
        }

        [McpTool(
            "play_mode_step",
            "Advance play mode, pausing first if needed. The reply carries the frame it reached " +
            "and how far into the run that is, so anything measured in seconds needs no second " +
            "call, and 'paths' reads whatever else you were going to look at while the frame is " +
            "still that one. Outside play mode the call fails with an error saying so.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Step(
            [McpArg("count", "How many frames to advance, 1 to 1000. Watching something happen a " +
                             "frame at a time is a call for each frame; a slow animation took " +
                             "1,255 of them.")]
            int? count = null,
            [McpArg("paths", "Read these while the frame is still the one just reached, in the " +
                             "same form reflect_read takes: '@scene:/Turnstile/Transform/" +
                             "localEulerAngles'. Watching something over time is a step and a " +
                             "look, over and over, and twenty-one steps once came with " +
                             "forty-six reads behind them.")]
            string[] paths = null,
            [McpArg("animators", "Scene paths of objects whose Animator to report on at the frame " +
                                 "reached: the state each layer is in, how far through it is, " +
                                 "whether it is in transition, and the parameter values. This is " +
                                 "what animator_inspect returns under 'runtime', without the " +
                                 "controller asset around it, because a state's progress is not a " +
                                 "property 'paths' can read. Twenty-one steps came with " +
                                 "twenty-one animator_inspect calls behind them.")]
            string[] animators = null,
            [McpArg("seconds", "Step until Time.time advances by this positive duration; alternative to count.")] double? seconds = null,
            [McpArg("max_frames", "Maximum stepped frames, 1 to 100000; default 2000.")] int maxFrames = 2000,
            [McpArg("changes", "Record bare values after every frame for 1 to 20 paths. Multiple changes within one frame appear once. At most 200 changes per path; beyond that retain the first 199 and last and mark truncated.")] bool changes = false)
        {
            return PlayModeControl.Control(ToolArgs.Of(
                ("action", "step"),
                ("count", count),
                ("seconds", seconds),
                ("max_frames", maxFrames),
                ("changes", changes),
                ("paths", paths == null ? null : new JArray(paths)),
                ("animators", animators == null ? null : new JArray(animators))));
        }
    }
}
