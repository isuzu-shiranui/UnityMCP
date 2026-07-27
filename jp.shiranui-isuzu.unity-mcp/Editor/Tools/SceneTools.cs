using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>Scene hierarchy inspection.</summary>
    internal static class SceneTools
    {
        [McpTool(
            "scene_browse_hierarchy",
            "Walk the open scenes' GameObject hierarchy, optionally filtered by name, component " +
            "type, or tag. Prefer narrowing with a filter and a small limit over fetching the " +
            "whole tree: a full hierarchy dump is large and mostly irrelevant to any one question.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject BrowseHierarchy(
            [McpArg("name", "Only include objects whose name contains this text.")]
            string name = null,
            [McpArg("component", "Only include objects carrying a component of this type.")]
            string component = null,
            [McpArg("tag", "Only include objects with this tag.")]
            string tag = null,
            [McpArg("max_depth", "How deep to descend from each root.")]
            int maxDepth = 5,
            [McpArg("active_only", "Skip inactive GameObjects.")]
            bool activeOnly = false,
            [McpArg("scene_index", "Restrict to a single open scene by index; omit for all scenes.")]
            int? sceneIndex = null,
            [McpArg("limit", "Maximum entries to return.")]
            int? limit = null,
            [McpArg("offset", "Entries to skip, for paging.")]
            int offset = 0,
            [McpArg("fields", "Comma-separated field whitelist, to keep responses small.")]
            string fields = null)
        {
            return SceneHierarchy.Browse(ToolArgs.Of(
                ("name", name),
                ("component", component),
                ("tag", tag),
                ("maxDepth", maxDepth),
                ("activeOnly", activeOnly),
                ("sceneIndex", sceneIndex),
                ("limit", limit),
                ("offset", offset),
                ("fields", fields)));
        }
    }
}
