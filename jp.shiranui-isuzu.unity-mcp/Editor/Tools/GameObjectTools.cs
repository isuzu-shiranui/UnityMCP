using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Creating, moving and removing GameObjects.
    /// </summary>
    /// <remarks>
    /// Every mutation here goes through <c>Undo</c> and declares an <c>UndoGroup</c>, so one
    /// call is one Ctrl+Z. That is the difference between letting an agent edit a scene and
    /// letting an agent edit a scene you can back out of, and it is why these exist as tools
    /// rather than as advice to write the equivalent through execute_code — arbitrary C# is not
    /// on the undo stack and cannot be taken back.
    /// </remarks>
    internal static class GameObjectTools
    {
        [McpTool(
            "gameobject_create",
            "Create a GameObject, optionally as a primitive and optionally parented to an existing " +
            "object. Returns the path to address it by later. The new object becomes the Editor's " +
            "selection.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create GameObject")]
        public static JObject Create(
            [McpArg("name", "Name for the new object. Defaults to the primitive's name, or 'GameObject'.")]
            string name = null,
            [McpArg("primitive", "Cube, Sphere, Capsule, Cylinder, Plane or Quad. Omit for an empty object.")]
            string primitive = null,
            [McpArg("parent_path", "Hierarchy path of the parent; omit to create at the scene root.")]
            string parentPath = null,
            [McpArg("parent_instance_id", "Parent by instance id.")]
            long? parentInstanceId = null,
            [McpArg("position", "Local position, as {x, y, z}.")]
            JObject position = null,
            [McpArg("rotation", "Local euler angles, as {x, y, z}.")]
            JObject rotation = null,
            [McpArg("scale", "Local scale, as {x, y, z}.")]
            JObject scale = null)
        {
            GameObject go;

            if (string.IsNullOrWhiteSpace(primitive))
            {
                go = new GameObject(string.IsNullOrWhiteSpace(name) ? "GameObject" : name);
            }
            else
            {
                if (!Enum.TryParse<PrimitiveType>(primitive, true, out var type))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{primitive}' is not a primitive. Use one of: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}.");
                }

                go = GameObject.CreatePrimitive(type);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    go.name = name;
                }
            }

            Undo.RegisterCreatedObjectUndo(go, "MCP Create GameObject");

            if (!string.IsNullOrWhiteSpace(parentPath) || parentInstanceId.HasValue)
            {
                var parent = ObjectResolve.Object(parentPath, parentInstanceId, "parent_path", "parent_instance_id");
                Undo.SetTransformParent(go.transform, parent.transform, "MCP Create GameObject");
            }

            ApplyTransform(go.transform, position, rotation, scale);
            Selection.activeGameObject = go;

            return Describe(go, transform: true);
        }

        [McpTool(
            "gameobject_delete",
            "Delete a GameObject and its children. Undoable, so this does not need confirming.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Delete GameObject")]
        public static JObject Delete(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var path = ObjectResolve.PathOf(go);
            var name = go.name;

            // DestroyImmediate would put it beyond recovery. This is the one call that makes
            // the difference between an agent's mistake being an inconvenience and being lost
            // work.
            Undo.DestroyObjectImmediate(go);

            return EditorNotes.SceneChange(new JObject
            {
                ["deleted"] = true,
                ["name"] = name,
                ["path"] = path,
            });
        }

        [McpTool(
            "gameobject_reparent",
            "Move a GameObject under a different parent, or to the scene root. World position is " +
            "preserved by default.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Reparent GameObject")]
        public static JObject Reparent(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("parent_path", "Path of the new parent. Omit to move it to the scene root.")]
            string parentPath = null,
            [McpArg("parent_instance_id", "New parent by instance id.")]
            long? parentInstanceId = null,
            [McpArg("sibling_index", "Position among the new parent's children; omit to append.")]
            int? siblingIndex = null,
            [McpArg("keep_world_position", "Keep the object where it is in world space. When false " +
                                          "the object is moved to the new parent's origin: local " +
                                          "position is zeroed and local rotation is cleared.")]
            bool keepWorldPosition = true)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);

            Transform parent = null;

            if (!string.IsNullOrWhiteSpace(parentPath) || parentInstanceId.HasValue)
            {
                var parentGo = ObjectResolve.Object(parentPath, parentInstanceId, "parent_path", "parent_instance_id");

                if (parentGo.transform.IsChildOf(go.transform))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{ObjectResolve.PathOf(parentGo)}' is inside '{ObjectResolve.PathOf(go)}'; " +
                        "an object cannot be parented to its own descendant.");
                }

                parent = parentGo.transform;
            }

            Undo.SetTransformParent(go.transform, parent, "MCP Reparent GameObject");

            if (!keepWorldPosition)
            {
                Undo.RecordObject(go.transform, "MCP Reparent GameObject");
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
            }

            if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
            }

            return Describe(go, transform: true);
        }

        [McpTool(
            "gameobject_duplicate",
            "Duplicate a GameObject under the same parent. The copy is a plain GameObject: even " +
            "when the original is a prefab instance the copy is not linked to the prefab, so later " +
            "edits to the prefab asset will not reach it and prefab_apply will refuse it. Use " +
            "prefab_instantiate when the copy has to stay linked. The copy becomes the Editor's " +
            "selection.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Duplicate GameObject")]
        public static JObject Duplicate(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("name", "Name for the copy. Defaults to Unity's own numbering.")]
            string name = null)
        {
            var source = ObjectResolve.Object(objectPath, instanceId);
            var copy = UnityEngine.Object.Instantiate(source, source.transform.parent);

            copy.name = string.IsNullOrWhiteSpace(name)
                ? GameObjectUtility.GetUniqueNameForSibling(source.transform.parent, source.name)
                : name;

            Undo.RegisterCreatedObjectUndo(copy, "MCP Duplicate GameObject");
            Selection.activeGameObject = copy;

            return Describe(copy);
        }

        [McpTool(
            "gameobject_set_transform",
            "Set position, rotation or scale. Only the parts given are changed, so this can nudge " +
            "one axis without restating the rest.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Set Transform")]
        public static JObject SetTransform(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("position", "Position, as {x, y, z}. Any missing axis is left alone.")]
            JObject position = null,
            [McpArg("rotation", "Euler angles, as {x, y, z}.")]
            JObject rotation = null,
            [McpArg("scale", "Scale, as {x, y, z}.")]
            JObject scale = null,
            [McpArg("world", "Treat position and rotation as world space rather than local.")]
            bool world = false)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);

            Undo.RecordObject(go.transform, "MCP Set Transform");
            ApplyTransform(go.transform, position, rotation, scale, world);

            return Describe(go, transform: true);
        }

        [McpTool(
            "gameobject_set_active",
            "Activate or deactivate a GameObject.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Set Active")]
        public static JObject SetActive(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("active", "Whether the object should be active.")]
            bool active = true)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);

            Undo.RecordObject(go, "MCP Set Active");
            go.SetActive(active);

            return Describe(go);
        }

        [McpTool(
            "gameobject_add_component",
            "Add a component by type name. Unqualified names are resolved against the loaded " +
            "assemblies, so 'Rigidbody' and 'UnityEngine.Rigidbody' both work.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Component")]
        public static JObject AddComponent(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("component_type", "Type name of the component to add.")]
            string componentType = null)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var type = FindComponentType(componentType);

            var added = Undo.AddComponent(go, type);

            if (added == null)
            {
                throw new McpToolException(
                    "tool_failed",
                    $"Unity refused to add {type.Name} to '{go.name}'. A RequireComponent dependency " +
                    "may be missing, or the component may already be present and disallow duplicates.");
            }

            return Describe(go, components: true);
        }

        [McpTool(
            "gameobject_remove_component",
            "Remove a component by type name. Base types match, so 'Renderer' finds a " +
            "MeshRenderer; pass index when the object carries several of the same type.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Remove Component")]
        public static JObject RemoveComponent(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("component_type", "Type name of the component to remove. Short or fully " +
                                      "qualified, and a base type matches a derived component.")]
            string componentType = null,
            [McpArg("index", "Which one, when the object carries several of the same type.")]
            int index = 0)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var component = ObjectResolve.Component(go, componentType, index);

            if (component is Transform)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Transform cannot be removed from a GameObject.");
            }

            Undo.DestroyObjectImmediate(component);

            return Describe(go, components: true);
        }

        private static Type FindComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new McpToolException("invalid_params", "'component_type' is required.");
            }

            var exact = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeTypes)
                .Where(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract)
                .Where(t => t.FullName == typeName || t.Name == typeName)
                .OrderBy(t => t.FullName == typeName ? 0 : 1)
                .ToArray();

            if (exact.Length == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"No component type named '{typeName}' is loaded. Use the full name if it is " +
                    "ambiguous, and check the script compiled with compile_status.");
            }

            // Several assemblies can define the same short name. Reporting that is more useful
            // than picking one and leaving the caller to wonder which they got.
            if (exact.Length > 1 && exact[0].FullName != typeName)
            {
                var candidates = string.Join(", ", exact.Take(6).Select(t => t.FullName));

                throw new McpToolException(
                    "invalid_params",
                    $"'{typeName}' is ambiguous: {candidates}. Pass the full name.");
            }

            return exact[0];
        }

        private static Type[] SafeTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static void ApplyTransform(
            Transform transform,
            JObject position,
            JObject rotation,
            JObject scale,
            bool world = false)
        {
            if (position != null)
            {
                var current = world ? transform.position : transform.localPosition;
                var next = ReadVector(position, current, "position");

                if (world)
                {
                    transform.position = next;
                }
                else
                {
                    transform.localPosition = next;
                }
            }

            if (rotation != null)
            {
                var current = world ? transform.eulerAngles : transform.localEulerAngles;
                var next = ReadVector(rotation, current, "rotation");

                if (world)
                {
                    transform.eulerAngles = next;
                }
                else
                {
                    transform.localEulerAngles = next;
                }
            }

            if (scale != null)
            {
                transform.localScale = ReadVector(scale, transform.localScale, "scale");
            }
        }

        /// <summary>
        /// Reads {x, y, z}, leaving out axes alone rather than zeroing them.
        /// </summary>
        private static Vector3 ReadVector(JObject source, Vector3 fallback, string argumentName)
        {
            float Axis(string key, float current)
            {
                var token = source[key];

                if (token == null || token.Type == JTokenType.Null)
                {
                    return current;
                }

                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{argumentName}.{key}' must be a number, not {token.Type}.");
                }

                return token.Value<float>();
            }

            return new Vector3(Axis("x", fallback.x), Axis("y", fallback.y), Axis("z", fallback.z));
        }

        /// <summary>
        /// Describes the object a call acted on.
        /// </summary>
        /// <remarks>
        /// Only what the operation was about. Adding the parent, all three transform vectors,
        /// every component name and the child count to every reply costs a few hundred
        /// characters of unasked-for detail per call, and unlike the tool catalogue that is
        /// paid again every time — on gameobject_set_active the caller only wants to know that
        /// it worked. The transform and the component list are added by the tools that change
        /// them; scene_browse_hierarchy and inspect_read answer the rest when it is the actual
        /// question.
        /// </remarks>
        private static JObject Describe(GameObject go, bool transform = false, bool components = false)
        {
            var t = go.transform;

            var result = new JObject
            {
                ["name"] = go.name,
                ["path"] = ObjectResolve.PathOf(go),
                ["instanceId"] = EntityIdCompat.WireIdOf(go),
                ["active"] = go.activeSelf,
            };

            if (transform)
            {
                result["localPosition"] = Vector(t.localPosition);
                result["localEulerAngles"] = Vector(t.localEulerAngles);
                result["localScale"] = Vector(t.localScale);
            }

            if (components)
            {
                result["components"] = new JArray(go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => (object)c.GetType().Name)
                    .ToArray());
            }

            return EditorNotes.SceneChange(result);
        }

        private static JObject Vector(Vector3 v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        }
    }
}
