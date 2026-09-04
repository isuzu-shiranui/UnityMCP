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
    /// One tool per mode rather than one tool taking a <c>mode</c> string. Split this way the
    /// two read tools can be Safe while only the write is Unsafe, and each carries just the
    /// parameters it uses instead of a union of all three.
    /// </remarks>
    internal static class InspectorTools
    {
        [McpTool(
            "inspect_read",
            "Read one serialized property from a component, or from the GameObject itself when " +
            "component_type is omitted. Identify the object by either instance_id or object_path.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Read(
            [McpArg("property_path", "Serialized property path, e.g. m_LocalPosition.x.")]
            string propertyPath,
            [McpArg("instance_id", "Target object instance id; alternative to object_path.")]
            long? instanceId = null,
            [McpArg("object_path", "Scene path of the target GameObject, as scene_browse_hierarchy reports it, e.g. /Root/Child.")]
            string objectPath = null,
            [McpArg("component_type", "Component type name; omit for the GameObject itself.")]
            string componentType = null,
            [McpArg("component_index", "Which component to use when several share the type.")]
            int componentIndex = 0)
        {
            return InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("propertyPath", propertyPath),
                ("instanceId", instanceId),
                ("objectPath", objectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex)));
        }

        [McpTool(
            "inspect_list",
            "List the serialized properties available on a component, or on the GameObject itself " +
            "when component_type is omitted, so you can discover the property_path to pass to " +
            "inspect_read or inspect_write.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject List(
            [McpArg("instance_id", "Target object instance id; alternative to object_path.")]
            long? instanceId = null,
            [McpArg("object_path", "Scene path of the target GameObject, as scene_browse_hierarchy reports it, e.g. /Root/Child.")]
            string objectPath = null,
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
                ("objectPath", objectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex),
                ("offset", offset),
                ("limit", limit),
                ("fields", fields),
                ("detail", detail)));
        }

        [McpTool(
            "inspect_write",
            "Write one serialized property on a component, or on the GameObject itself when " +
            "component_type is omitted. Use inspect_list first if you are unsure of the exact " +
            "property_path. A property that holds a reference to another object, such as a sprite, " +
            "a material or an event target, cannot be set here; assign those with execute_code.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Inspector Write",
            // 'value' is whatever JSON the property's type needs, which the schema can only call
            // "any". A scalar and a vector side by side say more than the sentence can.
            Examples = new[]
            {
                @"{""object_path"":""/Player"",""component_type"":""Transform"",""property_path"":""m_LocalPosition.x"",""value"":2.5}",
                @"{""object_path"":""/Player"",""component_type"":""Transform"",""property_path"":""m_LocalScale"",""value"":{""x"":2,""y"":2,""z"":2}}",
            })]
        public static JObject Write(
            [McpArg("property_path", "Serialized property path, e.g. m_LocalPosition.x.")]
            string propertyPath,
            [McpArg("value", "New value; its JSON type must match the property's type.")]
            JToken value,
            [McpArg("instance_id", "Target object instance id; alternative to object_path.")]
            long? instanceId = null,
            [McpArg("object_path", "Scene path of the target GameObject, as scene_browse_hierarchy reports it, e.g. /Root/Child.")]
            string objectPath = null,
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
                ("objectPath", objectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex)));
        }
    }
}
