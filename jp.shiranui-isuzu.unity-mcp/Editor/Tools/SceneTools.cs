using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>Inspecting and managing scenes.</summary>
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

        [McpTool(
            "scene_list",
            "List the scenes that are open, and the ones in the build settings. Read this before " +
            "opening anything: which scenes are loaded decides what the hierarchy tools can see.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject List()
        {
            var open = new JArray();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);

                open.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["index"] = i,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["rootCount"] = scene.isLoaded ? scene.rootCount : 0,
                    ["isActive"] = scene == SceneManager.GetActiveScene(),
                });
            }

            var inBuild = new JArray(EditorBuildSettings.scenes.Select((s, i) => (object)new JObject
            {
                ["path"] = s.path,
                ["enabled"] = s.enabled,
                ["buildIndex"] = i,
            }).ToArray());

            return new JObject
            {
                ["open"] = open,
                ["inBuildSettings"] = inBuild,
                ["activeScene"] = SceneManager.GetActiveScene().path,
            };
        }

        [McpTool(
            "scene_open",
            "Open a scene, replacing what is open or adding to it. Unsaved changes stop this " +
            "rather than being discarded.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Open(
            [McpArg("path", "Project path of the scene, e.g. Assets/Scenes/Main.unity.")]
            string path = null,
            [McpArg("additive", "Add to the open scenes instead of replacing them.")]
            bool additive = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var normalised = path.Replace('\\', '/');

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalised) == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No scene at '{path}'. Use asset_find with type 'Scene' to list them.");
            }

            // Opening on top of unsaved work would throw it away with nothing to undo. Refusing
            // and naming the scenes is recoverable; a silent discard is not.
            if (!additive)
            {
                var dirty = OpenScenes().Where(s => s.isDirty).Select(s => s.name).ToArray();

                if (dirty.Length > 0)
                {
                    throw new McpToolException(
                        "conflict",
                        $"Unsaved changes in: {string.Join(", ", dirty)}. Call scene_save first, or " +
                        "open additively.",
                        409);
                }
            }

            var scene = EditorSceneManager.OpenScene(
                normalised,
                additive ? OpenSceneMode.Additive : OpenSceneMode.Single);

            return new JObject
            {
                ["opened"] = true,
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["additive"] = additive,
                ["rootCount"] = scene.rootCount,
            };
        }

        [McpTool(
            "scene_save",
            "Save open scenes. Saves every dirty scene by default.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Save(
            [McpArg("path", "Save the active scene to this path instead, as a copy (Save As).")]
            string path = null)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var target = path.Replace('\\', '/');
                var parent = Path.GetDirectoryName(target)?.Replace('\\', '/');

                if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{parent}' does not exist. Create it with asset_create_folder first.");
                }

                var active = SceneManager.GetActiveScene();

                if (!EditorSceneManager.SaveScene(active, target))
                {
                    throw new McpToolException("tool_failed", $"Unity would not save '{active.name}' to '{target}'.");
                }

                return new JObject { ["saved"] = new JArray(target) };
            }

            var dirty = OpenScenes().Where(s => s.isDirty).ToArray();

            if (dirty.Length == 0)
            {
                return new JObject { ["saved"] = new JArray(), ["note"] = "Nothing was modified." };
            }

            if (!EditorSceneManager.SaveOpenScenes())
            {
                throw new McpToolException("tool_failed", "Unity would not save the open scenes.");
            }

            return new JObject
            {
                ["saved"] = new JArray(dirty.Select(s => (object)(string.IsNullOrEmpty(s.path) ? s.name : s.path)).ToArray()),
            };
        }

        [McpTool(
            "scene_create",
            "Create a new scene and open it.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Create(
            [McpArg("path", "Where to save it, e.g. Assets/Scenes/New.unity. Omit to leave it unsaved.")]
            string path = null,
            [McpArg("empty", "Create it without the default camera and light.")]
            bool empty = false,
            [McpArg("additive", "Add to the open scenes instead of replacing them.")]
            bool additive = false)
        {
            if (!additive)
            {
                var dirty = OpenScenes().Where(s => s.isDirty).Select(s => s.name).ToArray();

                if (dirty.Length > 0)
                {
                    throw new McpToolException(
                        "conflict",
                        $"Unsaved changes in: {string.Join(", ", dirty)}. Call scene_save first, or " +
                        "create additively.",
                        409);
                }
            }

            var scene = EditorSceneManager.NewScene(
                empty ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects,
                additive ? NewSceneMode.Additive : NewSceneMode.Single);

            if (string.IsNullOrWhiteSpace(path))
            {
                return new JObject
                {
                    ["created"] = true,
                    ["name"] = scene.name,
                    ["path"] = null,
                    ["note"] = "Not saved to disk. Call scene_save with a path to keep it.",
                };
            }

            var target = path.Replace('\\', '/');
            var parent = Path.GetDirectoryName(target)?.Replace('\\', '/');

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                throw new McpToolException(
                    "not_found",
                    $"'{parent}' does not exist. Create it with asset_create_folder first.");
            }

            if (!EditorSceneManager.SaveScene(scene, target))
            {
                throw new McpToolException("tool_failed", $"Unity would not save the new scene to '{target}'.");
            }

            return new JObject { ["created"] = true, ["name"] = scene.name, ["path"] = target };
        }

        private static Scene[] OpenScenes()
        {
            var scenes = new Scene[SceneManager.sceneCount];

            for (var i = 0; i < scenes.Length; i++)
            {
                scenes[i] = SceneManager.GetSceneAt(i);
            }

            return scenes;
        }
    }
}
