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
            "Report whether the Editor is currently playing, paused, or compiling.",
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
        public static JObject Play()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "play")));
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
            "Pause play mode. Outside play mode this does nothing and answers with an error field " +
            "explaining why, rather than failing the call; read the reply instead of assuming it " +
            "took effect.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Pause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "pause")));
        }

        [McpTool(
            "play_mode_unpause",
            "Resume a paused play mode. Outside play mode this does nothing and answers with an " +
            "error field explaining why, rather than failing the call; read the reply instead of " +
            "assuming it took effect.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Unpause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "unpause")));
        }

        [McpTool(
            "play_mode_step",
            "Advance play mode by a single frame, pausing first if needed. Outside play mode this " +
            "does nothing and answers with an error field explaining why, rather than failing the " +
            "call; read the reply instead of assuming it took effect.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Step()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "step")));
        }
    }
}
