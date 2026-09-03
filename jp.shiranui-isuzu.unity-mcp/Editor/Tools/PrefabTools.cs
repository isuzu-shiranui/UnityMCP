using System;
using System.IO;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Making prefabs, placing them, and pushing instance edits back.
    /// </summary>
    internal static class PrefabTools
    {
        [McpTool(
            "prefab_create",
            "Save a scene object as a prefab asset. The scene object becomes an instance of it " +
            "unless asked otherwise.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Prefab")]
        public static JObject Create(
            [McpArg("object_path", "Hierarchy path, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null,
            [McpArg("path", "Where to save it, e.g. Assets/Prefabs/Enemy.prefab.")]
            string path = null,
            [McpArg("connect", "Leave the scene object linked to the new prefab.")]
            bool connect = true,
            [McpArg("overwrite", "Replace an existing prefab at that path.")]
            bool overwrite = false)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var target = RequireAssetPath(path, ".prefab");

            if (!overwrite && AssetDatabase.LoadAssetAtPath<GameObject>(target) != null)
            {
                throw new McpToolException(
                    "conflict",
                    $"A prefab already exists at '{target}'. Pass overwrite to replace it — doing so " +
                    "rewrites every instance of it in every scene.",
                    409);
            }

            var prefab = connect
                ? PrefabUtility.SaveAsPrefabAssetAndConnect(go, target, InteractionMode.UserAction, out var saved)
                : PrefabUtility.SaveAsPrefabAsset(go, target, out saved);

            if (!saved || prefab == null)
            {
                throw new McpToolException(
                    "tool_failed",
                    $"Unity would not save '{go.name}' as a prefab. An object that is already part of " +
                    "another prefab's contents cannot be saved on its own.");
            }

            return new JObject
            {
                ["path"] = target,
                ["guid"] = AssetDatabase.AssetPathToGUID(target),
                ["connected"] = connect,
                ["source"] = ObjectResolve.PathOf(go),
            };
        }

        [McpTool(
            "prefab_instantiate",
            "Place a prefab into the open scene.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Instantiate Prefab")]
        public static JObject Instantiate(
            [McpArg("path", "Project path of the prefab asset.")]
            string path = null,
            [McpArg("parent_path", "Hierarchy path of the parent; omit for the scene root.")]
            string parentPath = null,
            [McpArg("parent_instance_id", "Parent by instance id.")]
            long? parentInstanceId = null,
            [McpArg("name", "Name for the instance. Defaults to the prefab's name.")]
            string name = null,
            [McpArg("position", "Local position, as {x, y, z}.")]
            JObject position = null,
            [McpArg("rotation", "Local euler angles, as {x, y, z}.")]
            JObject rotation = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var normalised = path.Replace('\\', '/');
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(normalised);

            if (asset == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No prefab at '{path}'. asset_find with type 'Prefab' will list them.");
            }

            Transform parent = null;

            if (!string.IsNullOrWhiteSpace(parentPath) || parentInstanceId.HasValue)
            {
                parent = ObjectResolve.Object(parentPath, parentInstanceId, "parent_path").transform;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);

            if (instance == null)
            {
                throw new McpToolException("tool_failed", $"Unity would not instantiate '{normalised}'.");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                instance.name = name;
            }

            Undo.RegisterCreatedObjectUndo(instance, "MCP Instantiate Prefab");

            if (position != null)
            {
                instance.transform.localPosition = ReadVector(position, instance.transform.localPosition, "position");
            }

            if (rotation != null)
            {
                instance.transform.localEulerAngles =
                    ReadVector(rotation, instance.transform.localEulerAngles, "rotation");
            }

            Selection.activeGameObject = instance;

            return EditorNotes.SceneChange(new JObject
            {
                ["name"] = instance.name,
                ["path"] = ObjectResolve.PathOf(instance),
                ["instanceId"] = EntityIdCompat.WireIdOf(instance),
                ["prefab"] = normalised,
            });
        }

        [McpTool(
            "prefab_apply",
            "Push a prefab instance's overrides back into the prefab asset. This changes every other " +
            "instance too, so check prefab_status first if that matters.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Apply(
            [McpArg("object_path", "Hierarchy path of the instance, from scene_browse_hierarchy.")]
            string objectPath = null,
            [McpArg("instance_id", "Instance id, instead of a path.")]
            long? instanceId = null)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);

            if (root == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{ObjectResolve.PathOf(go)}' is not part of a prefab instance.");
            }

            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);

            // Applying is not on the undo stack and rewrites an asset every instance shares.
            // Reporting the count is the only warning a caller gets.
            var overrides = PrefabUtility.GetObjectOverrides(root).Count
                            + PrefabUtility.GetAddedComponents(root).Count
                            + PrefabUtility.GetAddedGameObjects(root).Count;

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);

            return new JObject
            {
                ["applied"] = true,
                ["instance"] = ObjectResolve.PathOf(root),
                ["prefab"] = assetPath,
                ["overridesApplied"] = overrides,
                ["note"] = "Not undoable, and every instance of this prefab now carries the change.",
            };
        }

        private static string RequireAssetPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var target = path.Replace('\\', '/');

            if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                target += extension;
            }

            var parent = Path.GetDirectoryName(target)?.Replace('\\', '/');

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                throw new McpToolException(
                    "not_found",
                    $"'{parent}' does not exist. Create it with asset_create_folder first.");
            }

            return target;
        }

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
    }
}
