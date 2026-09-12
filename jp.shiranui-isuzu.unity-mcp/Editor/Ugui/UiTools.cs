using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Ugui
{
    /// <summary>
    /// Answering "why does this button do nothing when I click it".
    /// </summary>
    internal static class UiTools
    {
        /// <summary>
        /// Hits listed. A point in a busy scene is covered by a handful of elements; a listing
        /// longer than this is a sign the point is wrong rather than an answer worth reading.
        /// </summary>
        private const int DefaultLimit = 20;

        [McpTool(
            "ui_hit_test",
            "What a click at a screen point actually reaches, and what is in the way. This is the " +
            "answer to 'the button does nothing': the reply lists every uGUI element under the " +
            "point in the order the event system sees them, so the first one is what takes the " +
            "click, and names what covers the element you meant. It also lists elements under the " +
            "point that cannot be hit at all, each with the reason — Raycast Target off, an " +
            "inactive object, a CanvasGroup with Blocks Raycasts off, or a Canvas with no " +
            "GraphicRaycaster. Pass 'object_path' to aim at an element and be told why it is not " +
            "being clicked; pass 'position' to ask what is at a point. This needs play mode, " +
            "because EventSystem.current is only set while the game runs.",
            Idempotency = McpIdempotency.Safe,
            MaxResultSizeChars = 60000)]
        public static JObject HitTest(
            [McpArg("position", "Screen point as [x, y], with the origin at the bottom left. " +
                                "Omit to use the centre of 'object_path', or the centre of the screen.")]
            double[] position = null,
            [McpArg("object_path", "The element you expected the click to reach, e.g. " +
                                   "'/Canvas/Menu/Play'. The reply says what covers it.")]
            string objectPath = null,
            [McpArg("instance_id", "Address that element by instance id instead.")]
            long? instanceId = null,
            [McpArg("normalized", "Read 'position' as fractions of the screen, 0..1, rather than pixels.")]
            bool normalized = false,
            [McpArg("reasons", "Also list the elements under the point that cannot be hit, with why.")]
            bool reasons = true,
            [McpArg("limit", "How many hits to list.")]
            int limit = DefaultLimit)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new McpToolException(
                    "conflict",
                    "uGUI raycasting needs play mode: EventSystem.current is only set while the "
                    + "game runs, and outside it every point reports nothing under it. "
                    + "Call play_mode_play first.",
                    409);
            }

            if (limit <= 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'limit' must be at least 1; {limit} would list nothing.");
            }

            var wanted = objectPath != null || instanceId.HasValue
                ? ObjectResolve.Object(objectPath, instanceId)
                : null;

            var point = Point(position, normalized, wanted);
            return UiHitTest.Run(point, wanted, limit, reasons);
        }

        /// <exception cref="McpToolException"><c>invalid_params</c> when it is not two numbers.</exception>
        internal static Vector2 Point(double[] position, bool normalized, GameObject wanted)
        {
            if (position == null)
            {
                return wanted != null
                    ? UiHitTest.CentreOf(wanted)
                    : new Vector2(Screen.width / 2f, Screen.height / 2f);
            }

            if (position.Length != 2)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'position' takes two numbers, [x, y]; {position.Length} were given.");
            }

            var point = new Vector2((float)position[0], (float)position[1]);

            return normalized
                ? new Vector2(point.x * Screen.width, point.y * Screen.height)
                : point;
        }
    }
}
