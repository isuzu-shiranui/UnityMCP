using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Fields that point at something that is no longer there.
    /// </summary>
    /// <remarks>
    /// A reference the target has been deleted out from under keeps the identifier it was given
    /// and loses the object, and nothing else says so: the hierarchy is intact, the component is
    /// there, and every property reads back. It surfaces at run time as a null the code did not
    /// expect, which is far from the field that lost its target.
    /// <para>
    /// The pair of conditions is what separates it from a field nobody filled in. An unset field
    /// has no identifier either, so testing the object alone reports every empty slot in the
    /// scene as damage.
    /// </para>
    /// </remarks>
    internal static class BrokenReferenceTools
    {
        /// <summary>
        /// How long the asset walk runs before it reports where it stopped.
        /// </summary>
        /// <remarks>
        /// Loading every asset in a production project is minutes of main thread, and a caller
        /// that asked a question about one folder should not pay for the rest of the project to
        /// find that out.
        /// </remarks>
        private const int DefaultSeconds = 20;

        [McpTool(
            "asset_broken_references",
            "Find fields that point at something no longer there: a reference whose target was " +
            "deleted keeps its id and loses the object, while a field nobody filled in has " +
            "neither, so the two are told apart rather than every empty slot being reported. " +
            "Components whose script Unity cannot resolve are reported alongside them. Nothing " +
            "else in a reply says a reference is broken — the hierarchy is intact and every " +
            "property reads back — so this is the only way to see it before it is a null at run " +
            "time. The asset walk loads what it inspects and is bounded by 'max_assets' and " +
            "'max_seconds'; the reply says which bound stopped it, so a partial answer is never " +
            "mistaken for a clean project.",
            Idempotency = McpIdempotency.Safe,
            Group = McpToolGroups.Diagnostics,
            MaxResultSizeChars = 60000)]
        public static JObject BrokenReferences(
            [McpArg("scope", "'scene' walks the open scenes, 'assets' walks prefabs and other " +
                             "assets under 'folder', and 'both' does each in turn.")]
            string scope = "scene",
            [McpArg("folder", "Restrict the asset walk to this folder, e.g. Assets/Prefabs. " +
                              "Ignored by the scene walk.")]
            string folder = "Assets",
            [McpArg("limit", "Maximum broken references to report.")]
            int limit = 100,
            [McpArg("max_assets", "Maximum assets to load during an asset walk.")]
            int maxAssets = 2000,
            [McpArg("max_seconds", "How long the asset walk may run before it stops and says so.")]
            int maxSeconds = DefaultSeconds)
        {
            var wanted = (scope ?? "scene").Trim().ToLowerInvariant();

            if (wanted != "scene" && wanted != "assets" && wanted != "both")
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'scope' takes 'scene', 'assets' or 'both'; '{scope}' is none of them.");
            }

            if (limit < 1)
            {
                throw new McpToolException("invalid_params", "'limit' has to be at least 1.");
            }

            var found = new JArray();
            var scanned = 0;
            var stopped = (string)null;

            if (wanted == "scene" || wanted == "both")
            {
                scanned += ScanScenes(found, limit, ref stopped);
            }

            if (stopped == null && (wanted == "assets" || wanted == "both"))
            {
                ScanAssets(found, folder, limit, maxAssets, maxSeconds, ref scanned, ref stopped);
            }

            var result = new JObject
            {
                ["scope"] = wanted,
                ["broken"] = found,
                ["count"] = found.Count,
                ["objectsScanned"] = scanned,
            };

            if (stopped != null)
            {
                // Which bound ended the walk, because a short list from a walk that stopped early
                // and a short list from a clean project read the same otherwise.
                result["stoppedBecause"] = stopped;
            }

            return result;
        }

        /// <summary>Walks the open scenes, returning how many objects were looked at.</summary>
        private static int ScanScenes(JArray found, int limit, ref string stopped)
        {
            var scanned = 0;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);

                if (!scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        scanned++;
                        Inspect(transform.gameObject, Path(transform), scene.path, found, limit, ref stopped);

                        if (stopped != null)
                        {
                            return scanned;
                        }
                    }
                }
            }

            return scanned;
        }

        /// <summary>Walks assets under a folder, loading each one to look inside it.</summary>
        private static void ScanAssets(
            JArray found, string folder, int limit, int maxAssets, int maxSeconds,
            ref int scanned, ref string stopped)
        {
            var root = string.IsNullOrWhiteSpace(folder) ? "Assets" : folder.Replace('\\', '/').TrimEnd('/');

            if (!AssetDatabase.IsValidFolder(root))
            {
                throw new McpToolException(
                    "not_found",
                    $"'{root}' is not a folder in this project. Pass a path under Assets/, or " +
                    "leave 'folder' out to walk all of Assets.");
            }

            var clock = Stopwatch.StartNew();
            var paths = AssetDatabase.FindAssets(string.Empty, new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(p => !AssetDatabase.IsValidFolder(p))
                .ToArray();

            var loaded = 0;

            foreach (var path in paths)
            {
                if (loaded >= maxAssets)
                {
                    stopped = $"asset_limit: stopped after loading {loaded} assets of {paths.Length} under '{root}'";
                    return;
                }

                if (clock.Elapsed.TotalSeconds >= maxSeconds)
                {
                    stopped = $"time_limit: stopped after {maxSeconds}s having loaded {loaded} assets of {paths.Length} under '{root}'";
                    return;
                }

                loaded++;

                // Scenes are not loadable as assets and their contents are only reachable once
                // open, which the scene scope already covers.
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset == null)
                    {
                        continue;
                    }

                    scanned++;

                    if (asset is GameObject prefab)
                    {
                        foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                        {
                            Inspect(transform.gameObject, path + "/" + Path(transform), path, found, limit, ref stopped);

                            if (stopped != null)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    Record(asset, path, asset.GetType().Name, path, found, limit, ref stopped);

                    if (stopped != null)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>Every component on one GameObject.</summary>
        private static void Inspect(
            GameObject go, string where, string scene, JArray found, int limit, ref string stopped)
        {
            var components = go.GetComponents<Component>();

            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] == null)
                {
                    // Unity leaves the slot and loses the type, so there is no SerializedObject to
                    // walk. The index is the only handle on which slot it is.
                    found.Add(new JObject
                    {
                        ["object"] = where,
                        ["source"] = scene,
                        ["kind"] = "missing_script",
                        ["componentIndex"] = i,
                    });

                    if (found.Count >= limit)
                    {
                        stopped = $"result_limit: reached {limit} findings";
                        return;
                    }

                    continue;
                }

                Record(components[i], where, components[i].GetType().Name, scene, found, limit, ref stopped);

                if (stopped != null)
                {
                    return;
                }
            }
        }

        /// <summary>Every object-reference property on one object.</summary>
        private static void Record(
            UnityEngine.Object target, string where, string what, string source,
            JArray found, int limit, ref string stopped)
        {
            using var serialized = new SerializedObject(target);
            var property = serialized.GetIterator();

            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                if (SerializedValues.IsBookkeeping(property.propertyPath))
                {
                    continue;
                }

                // Both halves are the test. An id with no object is a target that went away; no
                // id and no object is a field nobody filled in, which is not damage.
                if (property.objectReferenceValue != null
                    || EntityIdCompat.ObjectReferenceId(property) == 0)
                {
                    continue;
                }

                found.Add(new JObject
                {
                    ["object"] = where,
                    ["source"] = source,
                    ["kind"] = "broken_reference",
                    ["component"] = what,
                    ["property"] = property.propertyPath,
                    ["danglingId"] = EntityIdCompat.WireObjectReferenceId(property),
                });

                if (found.Count >= limit)
                {
                    stopped = $"result_limit: reached {limit} findings";
                    return;
                }
            }
        }

        /// <summary>The hierarchy path the other tools take.</summary>
        private static string Path(Transform transform)
        {
            var parts = new List<string>();

            for (var current = transform; current != null; current = current.parent)
            {
                parts.Add(current.name);
            }

            parts.Reverse();
            return "/" + string.Join("/", parts);
        }
    }
}
