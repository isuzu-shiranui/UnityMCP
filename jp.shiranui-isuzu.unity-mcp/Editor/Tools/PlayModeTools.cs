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
    /// v2 exposed a single <c>/play_mode</c> endpoint taking an untyped <c>action</c> string,
    /// which forced the whole endpoint to be classified Unsafe even though reading the status
    /// has no side effects at all. Splitting it lets <see cref="Status"/> be Safe — and
    /// therefore retryable — while the mutating actions stay Unsafe.
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
            "Enter play mode. Takes effect on the next Editor frame and triggers a domain reload, " +
            "so the MCP connection will briefly drop and reconnect.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Play()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "play")));
        }

        [McpTool(
            "play_mode_stop",
            "Leave play mode. Takes effect on the next Editor frame and triggers a domain reload, " +
            "so the MCP connection will briefly drop and reconnect.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Stop()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "stop")));
        }

        [McpTool(
            "play_mode_pause",
            "Pause play mode. Fails if the Editor is not currently playing.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Pause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "pause")));
        }

        [McpTool(
            "play_mode_unpause",
            "Resume a paused play mode. Fails if the Editor is not currently playing.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Unpause()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "unpause")));
        }

        [McpTool(
            "play_mode_step",
            "Advance play mode by a single frame, pausing first if needed. " +
            "Fails if the Editor is not currently playing.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Step()
        {
            return PlayModeControl.Control(ToolArgs.Of(("action", "step")));
        }
    }
}
