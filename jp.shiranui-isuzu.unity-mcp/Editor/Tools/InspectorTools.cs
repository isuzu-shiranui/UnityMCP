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
            "List the serialized properties available on a component, so you can discover the " +
            "property_path to pass to inspect_read or inspect_write. Omitting component_type " +
            "lists the components on the GameObject rather than one component's properties, and " +
            "with detail:'full' that is every component's properties together: one call for the " +
            "whole object rather than one for each component it carries.",
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
            [McpArg("detail", "How much to say about each component: summary is the type and " +
                              "index alone, standard adds whether it is enabled, full adds its " +
                              "serialized properties. Applies only when 'component_type' is " +
                              "omitted; naming a component returns that component's properties, " +
                              "which this does not change.")]
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
            "Write serialized properties on a component, or on the GameObject itself when " +
            "component_type is omitted. Use inspect_list first if you are unsure of the exact " +
            "property_path. Two things save round trips here and neither loosens what a write " +
            "reaches: 'values' sets several properties on the one component, and 'object_paths' " +
            "makes the same edit across several objects the way the Inspector edits a " +
            "multi-selection. Both are one undo step, and both write nothing at all if any path " +
            "fails to resolve, so nothing is left half applied. Setting up one ConfigurableJoint " +
            "took twenty-one calls without the first, and swapping a material across three " +
            "hundred objects took two hundred and ninety-nine without the second.",
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
            [McpArg("property_path", "Serialized property path, e.g. m_LocalPosition.x. " +
                                     "Several at once go in 'values' instead.")]
            string propertyPath = null,
            [McpArg("value", "New value; its JSON type must match the property's type. A reference " +
                             "property takes the name of what to point at: a scene path like " +
                             "'/Root/Child', an asset path under Assets/ or Packages/, or an " +
                             "instance id. A path naming a GameObject is narrowed to the component " +
                             "the field holds, so '/Root/Child' fills a Transform field. An empty " +
                             "string clears the reference.")]
            JToken value = null,
            [McpArg("values", "Several properties on this one component, as a JSON object of " +
                              "property_path to value. Written together under one undo step, and " +
                              "not written at all if any path fails to resolve. Alternative to " +
                              "'property_path' and 'value'.")]
            JObject values = null,
            [McpArg("object_paths", "Several objects to make the same edit on, the way the " +
                                    "Inspector edits a multi-selection: one undo step for all of " +
                                    "them, and nothing written if any path or component fails to " +
                                    "resolve. Swapping a material across three hundred objects " +
                                    "was three hundred calls that differed only in this. Up to " +
                                    "500; alternative to 'object_path'.")]
            string[] objectPaths = null,
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
                ("values", values),
                ("objectPaths", objectPaths == null ? null : new JArray(objectPaths)),
                ("instanceId", instanceId),
                ("objectPath", objectPath),
                ("componentType", componentType),
                ("componentIndex", componentIndex)));
        }
    }
}
