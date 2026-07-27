using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Serialized property access, one tool per mode.
    /// </summary>
    /// <remarks>
    /// v2's single <c>/inspect</c> endpoint took a <c>mode</c> string covering read, list and
    /// write. Splitting them lets the two read modes be Safe while only the write is Unsafe,
    /// and lets each carry just the parameters it actually uses instead of a union of all three.
    /// </remarks>
    internal static class InspectorTools
    {
        [McpTool(
            "inspect_read",
            "Read one serialized property from a component. Identify the object by either " +
            "instance_id or game_object_path.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Read(
            [McpArg("property_path", "Serialized property path, e.g. m_LocalPosition.x.")]
            string propertyPath,
            [McpArg("instance_id", "Target object instance id; alternative to game_object_path.")]
            int? instanceId = null,
            [McpArg("game_object_path", "Scene path of the target GameObject, e.g. Root/Child.")]
            string gameObjectPath = null,
            [McpArg("component_type", "Component type name; omit for the GameObject itself.")]
            string componentType = null,
            [McpArg("component_index", "Which component to use when several share the type.")]
            int componentIndex = 0)
        {
            return InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("propertyPath", propertyPath),
                ("instanceId", instanceId),
                ("gameObjectPath", gameObjectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex)));
        }

        [McpTool(
            "inspect_list",
            "List the serialized properties available on a component, so you can discover the " +
            "property_path to pass to inspect_read or inspect_write.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject List(
            [McpArg("instance_id", "Target object instance id; alternative to game_object_path.")]
            int? instanceId = null,
            [McpArg("game_object_path", "Scene path of the target GameObject, e.g. Root/Child.")]
            string gameObjectPath = null,
            [McpArg("component_type", "Component type name; omit for the GameObject itself.")]
            string componentType = null,
            [McpArg("component_index", "Which component to use when several share the type.")]
            int componentIndex = 0,
            [McpArg("offset", "Properties to skip, for paging.")]
            int offset = 0,
            [McpArg("limit", "Maximum properties to return.")]
            int? limit = null,
            [McpArg("fields", "Comma-separated field whitelist, to keep responses small.")]
            string fields = null,
            [McpArg("detail", "Level of per-property detail: standard or full.")]
            string detail = "standard")
        {
            return InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("instanceId", instanceId),
                ("gameObjectPath", gameObjectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex),
                ("offset", offset),
                ("limit", limit),
                ("fields", fields),
                ("detail", detail)));
        }

        [McpTool(
            "inspect_write",
            "Write one serialized property on a component. Use inspect_list first if you are " +
            "unsure of the exact property_path.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Inspector Write")]
        public static JObject Write(
            [McpArg("property_path", "Serialized property path, e.g. m_LocalPosition.x.")]
            string propertyPath,
            [McpArg("value", "New value; its JSON type must match the property's type.")]
            JToken value,
            [McpArg("instance_id", "Target object instance id; alternative to game_object_path.")]
            int? instanceId = null,
            [McpArg("game_object_path", "Scene path of the target GameObject, e.g. Root/Child.")]
            string gameObjectPath = null,
            [McpArg("component_type", "Component type name; omit for the GameObject itself.")]
            string componentType = null,
            [McpArg("component_index", "Which component to use when several share the type.")]
            int componentIndex = 0)
        {
            return InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("propertyPath", propertyPath),
                ("value", value),
                ("instanceId", instanceId),
                ("gameObjectPath", gameObjectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex)));
        }
    }
}
